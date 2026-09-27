"""Probing clips, and the ffmpeg commands that decode and encode them."""

import json
import subprocess
from dataclasses import dataclass
from fractions import Fraction

from .errors import CensorError, NoVideo, TooLarge

# audio that Firefox plays from MP4 can be copied; anything else is re-encoded as AAC
MP4_AUDIO_CODECS = {"aac", "mp3", "opus", "flac"}

ENCODER_ARGS = {
    "nvenc": ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-b:v", "0"],
    "x264": ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23"],
}


@dataclass(frozen=True)
class VideoInfo:
    # display size, after any rotation metadata
    width: int
    height: int
    duration: float
    fps: Fraction
    video_codec: str
    audio_codec: str | None


@dataclass(frozen=True)
class Geometry:
    """Size and constant frame rate of the frames decoded for rendering."""

    width: int
    height: int
    fps: Fraction


def probe(path: str) -> VideoInfo:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
        stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=60)
    if result.returncode != 0:
        raise CensorError("not a readable video: " + result.stderr.replace(path, "input").strip()[-300:])
    return parse_probe(json.loads(result.stdout or "{}"))


def parse_probe(data: dict) -> VideoInfo:
    streams = data.get("streams", [])
    video = next((s for s in streams if s.get("codec_type") == "video"
                  and not s.get("disposition", {}).get("attached_pic")), None)
    if video is None:
        raise NoVideo("no video stream")
    audio = next((s for s in streams if s.get("codec_type") == "audio"), None)
    width, height = int(video.get("width") or 0), int(video.get("height") or 0)
    if width <= 0 or height <= 0:
        raise CensorError("unknown video size")
    # decoders apply the rotation, so frames come out with the display size
    if _rotation(video) % 180 != 0:
        width, height = height, width
    fps = _rate(video.get("avg_frame_rate")) or _rate(video.get("r_frame_rate"))
    if not fps:
        raise CensorError("unknown frame rate")
    duration = _float(data.get("format", {}).get("duration")) or _float(video.get("duration"))
    if duration <= 0:
        raise CensorError("unknown duration")
    return VideoInfo(width, height, duration, fps, video.get("codec_name", ""), audio.get("codec_name") if audio else None)


def check_limits(info: VideoInfo, size: int, settings) -> None:
    if size > settings.max_bytes:
        raise TooLarge(f"{size} bytes is over the {settings.max_bytes} byte limit")
    if info.duration > settings.max_seconds:
        raise TooLarge(f"{info.duration:.1f} s is over the {settings.max_seconds:g} s limit")


def output_geometry(info: VideoInfo, max_short_side: int, max_fps: float) -> Geometry:
    scale = min(1.0, max_short_side / min(info.width, info.height))
    fps = info.fps if info.fps <= max_fps else Fraction(max_fps).limit_denominator(1001)
    return Geometry(_even(info.width * scale), _even(info.height * scale), fps)


def sample_size(geometry: Geometry, longest_side: int) -> tuple[int, int]:
    """Size of the frames sent to the model: the rendered size, scaled down to fit longest_side."""
    scale = min(1.0, longest_side / max(geometry.width, geometry.height))
    return max(1, round(geometry.width * scale)), max(1, round(geometry.height * scale))


def sample_command(source: str, geometry: Geometry, step: int, size: tuple[int, int], pattern: str) -> list[str]:
    """Writes every step-th frame as a JPEG. Uses the same frame rate as decode_command, so sample i is frame i * step."""
    width, height = size
    return ["ffmpeg", "-v", "error", "-nostdin", "-i", source, "-map", "0:v:0",
            "-vf", f"fps={geometry.fps},select='not(mod(n,{step}))',scale={width}:{height}",
            "-fps_mode", "passthrough", "-q:v", "3", pattern]


def decode_command(source: str, geometry: Geometry) -> list[str]:
    """Decodes every frame as raw BGR at a constant frame rate."""
    return ["ffmpeg", "-v", "error", "-nostdin", "-i", source, "-map", "0:v:0",
            "-vf", f"fps={geometry.fps},scale={geometry.width}:{geometry.height}",
            "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1"]


def encode_command(source: str, geometry: Geometry, info: VideoInfo, encoder: str, output: str) -> list[str]:
    """Encodes raw BGR frames from stdin to MP4, with the source's first audio track."""
    command = ["ffmpeg", "-v", "error", "-y",
               "-f", "rawvideo", "-pix_fmt", "bgr24", "-video_size", f"{geometry.width}x{geometry.height}",
               "-framerate", str(geometry.fps), "-i", "pipe:0",
               "-i", source, "-map", "0:v:0", "-map", "1:a:0?"]
    command += ENCODER_ARGS[encoder] + ["-pix_fmt", "yuv420p"]
    if info.audio_codec:
        command += ["-c:a", "copy"] if info.audio_codec in MP4_AUDIO_CODECS else ["-c:a", "aac", "-b:a", "128k"]
    # moov first, so browsers can start playing before the whole file has arrived
    return command + ["-movflags", "+faststart", "-f", "mp4", output]


def nvenc_available() -> bool:
    result = subprocess.run(
        ["ffmpeg", "-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=0.2",
         "-c:v", "h264_nvenc", "-f", "null", "-"],
        stdin=subprocess.DEVNULL, capture_output=True, timeout=60)
    return result.returncode == 0


def _rotation(stream: dict) -> int:
    for side_data in stream.get("side_data_list", []):
        if "rotation" in side_data:
            return round(_float(side_data["rotation"]))
    return round(_float(stream.get("tags", {}).get("rotate")))


def _rate(value) -> Fraction:
    try:
        rate = Fraction(value)
    except (TypeError, ValueError, ZeroDivisionError):
        return Fraction(0)
    return rate if rate > 0 else Fraction(0)


def _float(value) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def _even(value: float) -> int:
    return max(2, int(value) // 2 * 2)
