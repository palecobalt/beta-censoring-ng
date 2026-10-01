"""mitmproxy addon: censors images and short video clips passing through the proxy using a Beta Censoring server.

Configured with environment variables:
  CENSOR_URL          Beta Censoring server (default http://beta-censoring:2382)
  CENSOR_OPTIONS      JSON file mapping classes to censor types (default censor-options.json next to this file)
  CENSOR_MIN_BYTES    skip images smaller than this, e.g. icons (default 4000)
  CENSOR_CONCURRENCY  images censored at once (default 6)
  CENSOR_TIMEOUT      seconds to wait for the server per image (default 60)
  CENSOR_ON_ERROR     "block" replaces images that can't be censored with a placeholder, "pass" lets them through
  VIDEO_CENSOR_URL    video censoring service (default http://video-censor:2383); empty turns video censoring off
  VIDEO_POLICY        "block" or "pass" for video that can't be censored: adaptive streams, clips over the size
                      limit, partial responses and failures (default: the same as CENSOR_ON_ERROR)
  VIDEO_MAX_MB        largest clip to censor (default 25)
  VIDEO_TIMEOUT       seconds to wait for the video service per clip (default 120)
  VIDEO_CACHE_MB      censored clips kept in memory for repeat and range requests (default 256)
  PROXY_AUTH          "user:password" clients must give to use the proxy (not possible in transparent mode)
  CENSOR_PASSTHROUGH_HOSTS  hosts (and their subdomains) that are never intercepted, such as banks and sign-in
                      pages, comma separated; added to a few built-in sign-in hosts unless the list contains "none"
  PINNED_POLICY       for apps that reject the proxy's certificate (pinned certificates): "block" (default) lets their
                      connections fail, "pass" lets them through uncensored from the second attempt on
"""
import asyncio
import base64
import json
import logging
import os
import re
import time
import urllib.error
import urllib.request

from mitmproxy import ctx, http, tls

import access_rules
import video_rules

CENSOR_URL = os.environ.get("CENSOR_URL", "http://beta-censoring:2382").rstrip("/")
OPTIONS_PATH = os.environ.get("CENSOR_OPTIONS", os.path.join(os.path.dirname(__file__), "censor-options.json"))
MIN_BYTES = int(os.environ.get("CENSOR_MIN_BYTES", "4000"))
CONCURRENCY = int(os.environ.get("CENSOR_CONCURRENCY", "6"))
TIMEOUT = float(os.environ.get("CENSOR_TIMEOUT", "60"))
ON_ERROR = os.environ.get("CENSOR_ON_ERROR", "block").lower()
VIDEO_CENSOR_URL = os.environ.get("VIDEO_CENSOR_URL", "http://video-censor:2383").rstrip("/")
VIDEO_POLICY = (os.environ.get("VIDEO_POLICY") or ON_ERROR).lower()
VIDEO_MAX_BYTES = int(float(os.environ.get("VIDEO_MAX_MB", "25")) * 1024 * 1024)
VIDEO_TIMEOUT = float(os.environ.get("VIDEO_TIMEOUT", "120"))
VIDEO_CACHE_BYTES = int(float(os.environ.get("VIDEO_CACHE_MB", "256")) * 1024 * 1024)
PROXY_AUTH = os.environ.get("PROXY_AUTH", "").strip()
PASSTHROUGH_HOSTS = access_rules.passthrough_hosts(os.environ.get("CENSOR_PASSTHROUGH_HOSTS"))
PINNED_POLICY = os.environ.get("PINNED_POLICY", "block")
# kept with the proxy's certificate authority, which docker-compose.yml stores in a volume
PINNED_HOSTS_FILE = os.path.expanduser("~/.mitmproxy/pinned-hosts.txt")

# formats Beta Censoring (ImageSharp 2) can decode
SUPPORTED_TYPES = {"image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif", "image/bmp", "image/tiff"}
# raster formats it can't decode; these get the ON_ERROR treatment
UNSUPPORTED_TYPES = {"image/avif", "image/jxl", "image/heic", "image/heif"}
UNSUPPORTED_ACCEPT = re.compile(r"image/(avif|jxl|heic|heif)(;q=[0-9.]+)?\s*,?\s*", re.IGNORECASE)

# 1x1 grey PNG
PLACEHOLDER = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNoaGgAAAMEAYFL09IQAAAAAElFTkSuQmCC"
)


class CensorProxy:
    def __init__(self):
        self._semaphore = asyncio.Semaphore(CONCURRENCY)
        # the video service censors one clip at a time anyway
        self._video_semaphore = asyncio.Semaphore(2)
        self._video_cache = video_rules.VideoCache(VIDEO_CACHE_BYTES)
        self._video_cache_options = None
        self._options = {}
        self._options_mtime = None

    def _censor_options(self):
        """Loads the class -> censor type mapping, reloading it whenever the file changes."""
        try:
            mtime = os.stat(OPTIONS_PATH).st_mtime
            if mtime != self._options_mtime:
                with open(OPTIONS_PATH) as f:
                    self._options = json.load(f)
                self._options_mtime = mtime
                logging.info(f"censor-proxy: loaded {len(self._options)} censor options from {OPTIONS_PATH}")
        except (OSError, ValueError) as e:
            logging.warning(f"censor-proxy: could not load {OPTIONS_PATH}: {e}")
        return self._options

    def requestheaders(self, flow: http.HTTPFlow):
        # ask servers for formats Beta Censoring can read instead of AVIF/JPEG XL
        accept = flow.request.headers.get("accept")
        if accept and UNSUPPORTED_ACCEPT.search(accept):
            flow.request.headers["accept"] = UNSUPPORTED_ACCEPT.sub("", accept).strip(" ,") or "*/*"

    def request(self, flow: http.HTTPFlow):
        request = flow.request
        if request.method != "GET":
            return
        cached = self._cached_video(request.pretty_url)
        if cached is not None:
            status, headers, body = video_rules.cached_response(cached, request.headers.get("range"))
            flow.response = http.Response.make(status, body, headers)
            # mitmproxy still runs the response hooks, which mustn't treat this as a new video
            flow.metadata["video_from_cache"] = True
            return
        if VIDEO_CENSOR_URL and video_rules.should_strip_range(request.headers, request.path):
            # ask for the whole file, so it can be censored before the browser gets any of it
            del request.headers["range"]

    def responseheaders(self, flow: http.HTTPFlow):
        if flow.metadata.get("video_from_cache"):
            return
        response = flow.response
        decision, reason = video_rules.classify_response(
            flow.request.method, response.status_code, response.headers, flow.request.path, VIDEO_MAX_BYTES)
        if decision == video_rules.IGNORE:
            return
        if decision == video_rules.CENSOR and VIDEO_CENSOR_URL:
            # the body is buffered, then censored in response()
            flow.metadata["censor_video"] = True
            return
        url = flow.request.pretty_url[:120]
        reason = reason or "video censoring is off"
        if VIDEO_POLICY == "pass":
            logging.info(f"censor-proxy: passing video uncensored ({reason}) {url}")
            response.stream = True
            return
        logging.warning(f"censor-proxy: blocked video ({reason}) {url}")
        self._make_blocked_video(response)
        # the headers go out now, so throw the body away as it arrives
        response.stream = lambda chunk: b""

    async def response(self, flow: http.HTTPFlow):
        if flow.metadata.get("censor_video"):
            await self._censor_video(flow)
            return
        response = flow.response
        content_type = response.headers.get("content-type", "").split(";")[0].strip().lower()
        if content_type in UNSUPPORTED_TYPES:
            self._handle_failure(flow, f"unsupported format {content_type}")
            return
        if content_type not in SUPPORTED_TYPES:
            return
        body = response.get_content(strict=False) or b""
        if len(body) < MIN_BYTES:
            return

        async with self._semaphore:
            started = time.monotonic()
            try:
                censored, censored_type = await asyncio.to_thread(self._censor, body, content_type)
            except Exception as e:
                self._handle_failure(flow, str(e))
                return
        response.content = censored
        response.headers["content-type"] = censored_type
        response.headers["x-censored"] = "1"
        logging.info(f"censor-proxy: censored {len(body)} bytes in {time.monotonic() - started:.2f}s {flow.request.pretty_url[:120]}")

    def _censor(self, body: bytes, content_type: str):
        payload = json.dumps({
            "imageDataUrl": f"data:{content_type};base64,{base64.b64encode(body).decode()}",
            "censorOptions": self._censor_options(),
        }).encode()
        request = urllib.request.Request(
            f"{CENSOR_URL}/censoring/censorImage", data=payload, headers={"Content-Type": "application/json"}
        )
        try:
            with urllib.request.urlopen(request, timeout=TIMEOUT) as result:
                return result.read(), result.headers.get_content_type()
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"server returned {e.code}") from e

    def _handle_failure(self, flow: http.HTTPFlow, reason: str):
        url = flow.request.pretty_url[:120]
        if ON_ERROR == "pass":
            logging.warning(f"censor-proxy: passing uncensored ({reason}) {url}")
            return
        logging.warning(f"censor-proxy: blocked ({reason}) {url}")
        flow.response.content = PLACEHOLDER
        flow.response.headers["content-type"] = "image/png"
        flow.response.headers["x-censored"] = "blocked"

    async def _censor_video(self, flow: http.HTTPFlow):
        response, url = flow.response, flow.request.pretty_url
        content_type = video_rules.media_type(response.headers.get("content-type", "")) or "application/octet-stream"
        body = response.get_content(strict=False) or b""
        async with self._video_semaphore:
            started = time.monotonic()
            try:
                result, censored = await asyncio.to_thread(self._censor_clip, body, content_type)
            except Exception as e:
                self._video_failure(flow, str(e))
                return
        video = video_rules.CachedVideo(result, "video/mp4" if censored else content_type, censored)
        self._video_cache.put(url, video)
        self._video_cache_options = self._options_mtime
        # a complete 200 without Accept-Ranges, so the browser doesn't ask the origin for (uncensored) ranges
        response.status_code, response.reason = 200, "OK"
        for name in ("content-range", "accept-ranges", "content-encoding"):
            response.headers.pop(name, None)
        response.content = video.body
        response.headers["content-type"] = video.content_type
        response.headers["x-censored"] = "1" if censored else "0"
        logging.info(f"censor-proxy: {'censored' if censored else 'nothing to censor in'} {len(body)} byte video "
                     f"in {time.monotonic() - started:.1f}s {url[:120]}")

    def _censor_clip(self, body: bytes, content_type: str):
        request = urllib.request.Request(f"{VIDEO_CENSOR_URL}/censor", data=body, headers={
            "Content-Type": content_type, "X-Censor-Options": json.dumps(self._censor_options())})
        try:
            with urllib.request.urlopen(request, timeout=VIDEO_TIMEOUT) as result:
                return result.read(), result.headers.get("X-Censored") == "1"
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"video service returned {e.code}: {e.read()[:200].decode('utf-8', 'replace')}") from e

    def _video_failure(self, flow: http.HTTPFlow, reason: str):
        url = flow.request.pretty_url[:120]
        if VIDEO_POLICY == "pass":
            logging.warning(f"censor-proxy: passing video uncensored ({reason}) {url}")
            return
        logging.warning(f"censor-proxy: blocked video ({reason}) {url}")
        self._make_blocked_video(flow.response)
        flow.response.content = b""

    @staticmethod
    def _make_blocked_video(response: http.Response):
        response.status_code, response.reason = 403, "Forbidden"
        for name in ("content-length", "content-range", "content-encoding", "transfer-encoding", "accept-ranges"):
            response.headers.pop(name, None)
        response.headers["content-type"] = "text/plain"
        response.headers["content-length"] = "0"
        response.headers["x-censored"] = "blocked"

    def _cached_video(self, url: str):
        if not len(self._video_cache):
            return None
        self._censor_options()
        if self._options_mtime != self._video_cache_options:
            # censored with options that have since changed
            self._video_cache.clear()
            return None
        return self._video_cache.get(url)


class AccessControl:
    """Proxy password, hosts that are never intercepted, and apps that reject the proxy's certificate."""

    def __init__(self):
        self._pinned = access_rules.PinnedHosts(PINNED_POLICY, PINNED_HOSTS_FILE)

    def running(self):
        transparent = any(str(mode).startswith("transparent") for mode in ctx.options.mode)
        if PROXY_AUTH and not transparent:
            ctx.options.update(proxyauth=PROXY_AUTH)
        elif PROXY_AUTH:
            logging.warning("censor-proxy: PROXY_AUTH is ignored, a transparent proxy can't ask for a password; "
                            "limit who reaches it with firewall rules instead")
        elif not transparent:
            logging.warning("censor-proxy: no PROXY_AUTH set, so anyone who can reach the proxy port can use it")
        logging.info(f"censor-proxy: never intercepting {', '.join(PASSTHROUGH_HOSTS) or 'no hosts'}; "
                     f"apps that reject the certificate: {self._pinned.policy}")

    @staticmethod
    def _host(sni, context):
        return sni or (context.server.address[0] if context.server.address else None)

    @staticmethod
    def _client(context):
        return context.client.peername[0] if context.client.peername else None

    # The two hooks below run after mitmproxy's own password check, which has answered 407 by then when the request
    # came without the password. Android's connectivity checks always do (see access_rules).
    def http_connect(self, flow: http.HTTPFlow):
        if (flow.response and flow.response.status_code == 407
                and access_rules.is_connectivity_check_tunnel(flow.request.host, flow.request.port, PASSTHROUGH_HOSTS)):
            flow.response = None

    def requestheaders(self, flow: http.HTTPFlow):
        if (flow.response and flow.response.status_code == 407
                and access_rules.is_connectivity_check(flow.request.method, flow.request.scheme, flow.request.path)):
            flow.response = http.Response.make(204)

    def tls_clienthello(self, data: tls.ClientHelloData):
        host = self._host(data.client_hello.sni, data.context)
        if access_rules.matches(host, PASSTHROUGH_HOSTS) or self._pinned.should_pass(self._client(data.context), host):
            # relayed as it is: not decrypted, not censored
            data.ignore_connection = True

    def tls_failed_client(self, data: tls.TlsData):
        host = self._host(data.conn.sni, data.context)
        client = self._client(data.context)
        if self._pinned.record_failure(client, host, access_rules.is_rejection(data.conn.error)):
            outcome = ("it will be passed through uncensored for that device from now on" if self._pinned.policy == "pass"
                       else "its connections stay blocked (PINNED_POLICY=pass would let them through uncensored)")
            logging.warning(f"censor-proxy: {client} rejected the proxy's certificate for {host}: an app with a pinned "
                            f"certificate, or a device without the proxy's CA installed; {outcome}")

    def tls_established_client(self, data: tls.TlsData):
        self._pinned.record_success(self._client(data.context), self._host(data.conn.sni, data.context))


addons = [AccessControl(), CensorProxy()]
