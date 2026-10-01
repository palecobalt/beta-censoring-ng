"""Decisions about video requests and responses, kept free of mitmproxy so they can be unit tested."""

import asyncio
import re
from collections import OrderedDict
from dataclasses import dataclass

CENSOR = "censor"
UNCENSORABLE = "uncensorable"
IGNORE = "ignore"

# single-file clips the video service can censor
CLIP_TYPES = {"video/mp4", "video/webm", "video/ogg", "video/quicktime", "video/x-m4v", "application/ogg"}
CLIP_EXTENSIONS = {"mp4", "m4v", "webm", "ogv", "mov"}
# playlists and segments of adaptive streams (HLS, DASH), which can't be censored a file at a time
STREAM_TYPES = {"application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl",
                "application/dash+xml", "video/mp2t", "video/iso.segment"}
STREAM_EXTENSIONS = {"m3u8", "mpd", "m4s", "ts"}
GENERIC_TYPES = {"", "application/octet-stream", "binary/octet-stream"}


def media_type(content_type: str) -> str:
    return content_type.split(";")[0].strip().lower()


def extension(path: str) -> str:
    name = path.split("?")[0].split("#")[0].rsplit("/", 1)[-1]
    return name.rsplit(".", 1)[-1].lower() if "." in name else ""


def is_media_request(headers, path: str) -> bool:
    """Whether a request is probably for audio or video, e.g. from a <video> element."""
    if headers.get("sec-fetch-dest", "").lower() in ("video", "audio"):
        return True
    return "video/" in headers.get("accept", "").lower() or extension(path) in CLIP_EXTENSIONS


def should_strip_range(headers, path: str) -> bool:
    """Media requests for the whole file as a range (Firefox's first request is "bytes=0-") are sent without the
    Range header, so the origin answers with the complete file, which can be censored."""
    return is_media_request(headers, path) and headers.get("range", "").replace(" ", "").lower() == "bytes=0-"


def classify_response(method: str, status: int, headers, path: str, max_bytes: int) -> tuple[str, str]:
    """What to do with a response: CENSOR (buffer it and censor it), UNCENSORABLE (video that can't be censored, which
    follows the video policy) or IGNORE (not video). The second value is the reason, for logs."""
    if method != "GET" or status not in (200, 206):
        return IGNORE, ""
    content_type = media_type(headers.get("content-type", ""))
    ext = extension(path)
    if content_type in STREAM_TYPES or (ext in STREAM_EXTENSIONS and (content_type in GENERIC_TYPES or content_type.startswith("video/"))):
        return UNCENSORABLE, f"adaptive stream ({content_type or ext})"
    if content_type not in CLIP_TYPES and not (content_type in GENERIC_TYPES and ext in CLIP_EXTENSIONS):
        return (UNCENSORABLE, f"unsupported video format {content_type}") if content_type.startswith("video/") else (IGNORE, "")
    if status == 206:
        size = whole_file_size(headers.get("content-range", ""))
        if size is None:
            return UNCENSORABLE, "partial content"
    else:
        size = _int(headers.get("content-length"))
        if size is None:
            return UNCENSORABLE, "unknown size"
    if size > max_bytes:
        return UNCENSORABLE, f"{size} bytes is over the {max_bytes} byte limit"
    return CENSOR, ""


def whole_file_size(content_range: str) -> int | None:
    """The file size when a Content-Range covers the whole file, otherwise None."""
    match = re.fullmatch(r"\s*bytes\s+(\d+)-(\d+)/(\d+)\s*", content_range, re.IGNORECASE)
    if match and int(match.group(1)) == 0 and int(match.group(2)) == int(match.group(3)) - 1:
        return int(match.group(3))
    return None


def parse_range(header: str | None, size: int) -> tuple[int, int] | None:
    """The inclusive byte range a Range header asks for, or None to send the whole body (no header, or one this
    doesn't handle, such as several ranges). Raises ValueError when the range can't be satisfied."""
    match = re.fullmatch(r"\s*bytes\s*=\s*(\d*)\s*-\s*(\d*)\s*", header or "", re.IGNORECASE)
    if not match or not (match.group(1) or match.group(2)):
        return None
    first, last = match.group(1), match.group(2)
    if first:
        start = int(first)
        if last and int(last) < start:
            return None
        end = min(int(last), size - 1) if last else size - 1
    else:
        if int(last) == 0:
            raise ValueError("empty suffix range")
        start, end = max(0, size - int(last)), size - 1
    if start >= size:
        raise ValueError("range starts after the end of the file")
    return start, end


@dataclass(frozen=True)
class CachedVideo:
    body: bytes
    content_type: str
    censored: bool


def cached_response(video: CachedVideo, range_header: str | None) -> tuple[int, dict, bytes]:
    """Status, headers and body answering a request from the cache, honouring a Range header."""
    size = len(video.body)
    headers = {"content-type": video.content_type, "x-censored": "1" if video.censored else "0"}
    try:
        byte_range = parse_range(range_header, size)
    except ValueError:
        return 416, {**headers, "content-range": f"bytes */{size}"}, b""
    # a whole-file range gets a 200, like the first response, so the browser isn't encouraged to use ranges
    if byte_range is None or byte_range == (0, size - 1):
        return 200, headers, video.body
    start, end = byte_range
    return 206, {**headers, "content-range": f"bytes {start}-{end}/{size}"}, video.body[start:end + 1]


class VideoCache:
    """Least recently used clips, up to a total size in bytes."""

    def __init__(self, max_bytes: int):
        self.max_bytes = max_bytes
        self._items: OrderedDict[str, CachedVideo] = OrderedDict()
        self._size = 0

    def __len__(self):
        return len(self._items)

    def get(self, key: str) -> CachedVideo | None:
        video = self._items.get(key)
        if video is not None:
            self._items.move_to_end(key)
        return video

    def put(self, key: str, video: CachedVideo) -> None:
        self._remove(key)
        if len(video.body) > self.max_bytes:
            return
        self._items[key] = video
        self._size += len(video.body)
        while self._size > self.max_bytes:
            self._remove(next(iter(self._items)))

    def clear(self) -> None:
        self._items.clear()
        self._size = 0

    def _remove(self, key: str) -> None:
        video = self._items.pop(key, None)
        if video is not None:
            self._size -= len(video.body)


class PendingClips:
    """Clips that are being censored right now, by URL.

    A browser asks for the same clip several times at once: a first probe, the request it plays from, and retries
    when the first answer is slow. Only the first of them has the clip censored; the others wait here for its
    outcome: the censored CachedVideo, the reason as text when censoring failed, or None when the first request
    never got as far as censoring, in which case a waiting request takes over."""

    def __init__(self):
        self._futures: dict[str, asyncio.Future] = {}

    def __contains__(self, url: str) -> bool:
        return url in self._futures

    def start(self, url: str) -> bool:
        """True when nobody is censoring this clip yet: the caller does it, and calls finish() whatever happens."""
        if url in self._futures:
            return False
        self._futures[url] = asyncio.get_running_loop().create_future()
        return True

    def finish(self, url: str, outcome: "CachedVideo | str | None") -> None:
        future = self._futures.pop(url, None)
        if future is not None and not future.done():
            future.set_result(outcome)

    async def wait(self, url: str, timeout: float) -> "CachedVideo | str | None":
        """The outcome of the run in progress for this clip; None when there is none (any more)."""
        future = self._futures.get(url)
        if future is None:
            return None
        try:
            # shielded: one waiter giving up must not cancel the result for the others
            return await asyncio.wait_for(asyncio.shield(future), timeout)
        except asyncio.TimeoutError:
            return "gave up waiting for another request for the same clip"


def _int(value) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None
