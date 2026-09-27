"""HTTP API. POST /censor with a clip as the body (and censor options in X-Censor-Options) returns it censored."""

import json
import logging
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import media, pipeline
from .config import Settings
from .errors import CensorError

log = logging.getLogger("video_censor")


class CensorService:
    def __init__(self, settings: Settings):
        self.settings = settings
        self.encoder = self._pick_encoder()
        # one clip at a time, so video doesn't starve still images of GPU time
        self._lock = threading.Lock()

    @property
    def busy(self) -> bool:
        return self._lock.locked()

    def censor(self, data: bytes, options: dict) -> pipeline.Result:
        started = time.monotonic()
        with self._lock:
            result = pipeline.censor_clip(data, options, self.settings, self.encoder)
        log.info("%s clip in %.1f s: %s", "censored" if result.censored else "unchanged",
                 time.monotonic() - started, json.dumps(result.stats))
        return result

    def options_from(self, header: str | None) -> dict:
        if header:
            options = json.loads(header)
        elif self.settings.options_file:
            with open(self.settings.options_file, encoding="utf-8") as f:
                options = json.load(f)
        else:
            options = {}
        if not isinstance(options, dict):
            raise ValueError("censor options must be a JSON object")
        return options

    def _pick_encoder(self) -> str:
        if self.settings.encoder in ("auto", "nvenc"):
            if media.nvenc_available():
                return "nvenc"
            if self.settings.encoder == "nvenc":
                log.warning("NVENC isn't available, using x264")
        return "x264"


def make_handler(service: CensorService):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        server_version = "video-censor"

        def do_GET(self):
            if self.path == "/health":
                self._send_json(200, {"encoder": service.encoder, "busy": service.busy})
            else:
                self._send_json(404, {"error": "not found"})

        def do_POST(self):
            if self.path.split("?")[0] != "/censor":
                return self._send_json(404, {"error": "not found"}, close=True)
            length = self.headers.get("Content-Length", "")
            if not length.isdigit():
                return self._send_json(411, {"error": "Content-Length required"}, close=True)
            if int(length) > service.settings.max_bytes:
                return self._send_json(413, {"error": "clip is over the size limit"}, close=True)
            data = self.rfile.read(int(length))
            try:
                options = service.options_from(self.headers.get("X-Censor-Options"))
            except (OSError, ValueError) as e:
                return self._send_json(400, {"error": f"bad censor options: {e}"})
            try:
                result = service.censor(data, options)
            except CensorError as e:
                log.info("clip not censored (%d): %s", e.status, e)
                return self._send_json(e.status, {"error": str(e)})
            except Exception:
                log.exception("censoring failed")
                return self._send_json(500, {"error": "internal error"})
            self.send_response(200)
            content_type = "video/mp4" if result.censored else self.headers.get("Content-Type", "application/octet-stream")
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(result.body)))
            self.send_header("X-Censored", "1" if result.censored else "0")
            self.send_header("X-Video-Censor-Stats", json.dumps(result.stats, separators=(",", ":")))
            self.end_headers()
            self.wfile.write(result.body)

        def _send_json(self, status: int, payload: dict, close: bool = False):
            body = json.dumps(payload).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            if close:
                # the request body wasn't read, so the connection can't be reused
                self.send_header("Connection", "close")
                self.close_connection = True
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, format, *args):
            log.debug("%s " + format, self.address_string(), *args)

    return Handler


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    settings = Settings.from_env()
    service = CensorService(settings)
    log.info("listening on port %d (encoder %s, detection at %s)", settings.port, service.encoder, settings.censor_url)
    ThreadingHTTPServer(("", settings.port), make_handler(service)).serve_forever()


if __name__ == "__main__":
    main()
