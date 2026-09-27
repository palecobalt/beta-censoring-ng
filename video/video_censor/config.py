"""Service settings, read from environment variables."""

import os
from dataclasses import dataclass


def _env(name, default, cast=str):
    value = os.environ.get(name, "").strip()
    return cast(value) if value else default


@dataclass(frozen=True)
class Settings:
    censor_url: str = "http://beta-censoring:2382"
    port: int = 2383
    max_bytes: int = 25 * 1024 * 1024
    max_seconds: float = 20.0
    # videos whose shorter side is larger than this are scaled down to it
    max_short_side: int = 1080
    max_fps: float = 30.0
    sample_fps: float = 5.0
    # longest side of the frames sent to the model
    sample_size: int = 640
    # each frame is censored with the matches from samples up to this many sample intervals away,
    # so something the model misses on one or two samples in a row stays covered
    hold_samples: int = 2
    # boxes grow by this fraction of their size on each side, to cover movement between samples
    motion_padding: float = 0.15
    detect_batch: int = 16
    detect_concurrency: int = 2
    detect_timeout: float = 120.0
    # auto (NVENC when it works, otherwise x264), nvenc or x264
    encoder: str = "auto"
    # censor options for requests that don't send any
    options_file: str = ""

    @classmethod
    def from_env(cls):
        return cls(
            censor_url=_env("CENSOR_URL", cls.censor_url).rstrip("/"),
            port=_env("VIDEO_PORT", cls.port, int),
            max_bytes=int(_env("VIDEO_MAX_MB", cls.max_bytes / 1024 / 1024, float) * 1024 * 1024),
            max_seconds=_env("VIDEO_MAX_SECONDS", cls.max_seconds, float),
            max_short_side=_env("VIDEO_MAX_SHORT_SIDE", cls.max_short_side, int),
            max_fps=_env("VIDEO_MAX_FPS", cls.max_fps, float),
            sample_fps=_env("VIDEO_SAMPLE_FPS", cls.sample_fps, float),
            sample_size=_env("VIDEO_SAMPLE_SIZE", cls.sample_size, int),
            hold_samples=_env("VIDEO_HOLD_SAMPLES", cls.hold_samples, int),
            motion_padding=_env("VIDEO_MOTION_PADDING", cls.motion_padding, float),
            detect_batch=_env("VIDEO_DETECT_BATCH", cls.detect_batch, int),
            detect_concurrency=_env("VIDEO_DETECT_CONCURRENCY", cls.detect_concurrency, int),
            detect_timeout=_env("VIDEO_DETECT_TIMEOUT", cls.detect_timeout, float),
            encoder=_env("VIDEO_ENCODER", cls.encoder).lower(),
            options_file=_env("CENSOR_OPTIONS_FILE", cls.options_file),
        )
