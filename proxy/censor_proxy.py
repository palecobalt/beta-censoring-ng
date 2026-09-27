"""mitmproxy addon: censors images passing through the proxy using a Beta Censoring server.

Configured with environment variables:
  CENSOR_URL          Beta Censoring server (default http://beta-censoring:2382)
  CENSOR_OPTIONS      JSON file mapping classes to censor types (default censor-options.json next to this file)
  CENSOR_MIN_BYTES    skip images smaller than this, e.g. icons (default 4000)
  CENSOR_CONCURRENCY  images censored at once (default 6)
  CENSOR_TIMEOUT      seconds to wait for the server per image (default 60)
  CENSOR_ON_ERROR     "block" replaces images that can't be censored with a placeholder, "pass" lets them through
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

from mitmproxy import http

CENSOR_URL = os.environ.get("CENSOR_URL", "http://beta-censoring:2382").rstrip("/")
OPTIONS_PATH = os.environ.get("CENSOR_OPTIONS", os.path.join(os.path.dirname(__file__), "censor-options.json"))
MIN_BYTES = int(os.environ.get("CENSOR_MIN_BYTES", "4000"))
CONCURRENCY = int(os.environ.get("CENSOR_CONCURRENCY", "6"))
TIMEOUT = float(os.environ.get("CENSOR_TIMEOUT", "60"))
ON_ERROR = os.environ.get("CENSOR_ON_ERROR", "block").lower()

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

    async def response(self, flow: http.HTTPFlow):
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


addons = [CensorProxy()]
