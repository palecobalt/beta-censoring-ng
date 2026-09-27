"""Censors one clip: probe it, run the model on sampled frames, then re-encode every frame with the censoring drawn on."""

import glob
import os
import subprocess
import tempfile
import time
from dataclasses import dataclass, field

import numpy as np

from . import detect, effects, media, timeline
from .errors import CensorError, EncodeFailed, NoVideo


@dataclass
class Result:
    body: bytes
    censored: bool
    stats: dict = field(default_factory=dict)


def censor_clip(data: bytes, censor_options: dict, settings, encoder: str) -> Result:
    """Returns the censored clip as MP4, or the original bytes when nothing in it needs censoring."""
    started = time.monotonic()
    stats = {}

    def checkpoint(name):
        stats[name] = round(time.monotonic() - started, 2)

    with tempfile.TemporaryDirectory(prefix="video-censor-") as work:
        source = os.path.join(work, "source")
        with open(source, "wb") as f:
            f.write(data)
        try:
            info = media.probe(source)
        except NoVideo:
            # audio only: nothing to censor
            return Result(data, False, {"video": False})
        media.check_limits(info, len(data), settings)
        geometry = media.output_geometry(info, settings.max_short_side, settings.max_fps)
        step = timeline.sample_step(geometry.fps, settings.sample_fps)
        stats.update(duration=round(info.duration, 2), size=f"{geometry.width}x{geometry.height}", fps=round(float(geometry.fps), 2))

        samples_dir = os.path.join(work, "samples")
        os.mkdir(samples_dir)
        size = media.sample_size(geometry, settings.sample_size)
        _run(media.sample_command(source, geometry, step, size, os.path.join(samples_dir, "%06d.jpg")), "sampling frames")
        samples = sorted(glob.glob(os.path.join(samples_dir, "*.jpg")))
        if not samples:
            raise CensorError("no frames could be decoded")
        stats["samples"] = len(samples)
        checkpoint("sampled_s")

        detections = detect.detect_frames(samples, censor_options, settings)
        sample_boxes = [timeline.censor_boxes(d, (geometry.width, geometry.height), censor_options, settings.motion_padding)
                        for d in detections]
        checkpoint("detected_s")
        if not any(sample_boxes):
            return Result(data, False, stats)

        output = os.path.join(work, "censored.mp4")
        try:
            frames, censored = _render(source, output, info, geometry, step, sample_boxes, settings.hold_samples, encoder)
        except EncodeFailed:
            if encoder == "x264":
                raise
            encoder = "x264"
            frames, censored = _render(source, output, info, geometry, step, sample_boxes, settings.hold_samples, encoder)
        stats.update(frames=frames, censored_frames=censored, encoder=encoder)
        checkpoint("total_s")
        with open(output, "rb") as f:
            return Result(f.read(), True, stats)


def _render(source, output, info, geometry, step, sample_boxes, hold, encoder) -> tuple[int, int]:
    frame_size = geometry.width * geometry.height * 3
    frames = censored = 0
    with tempfile.TemporaryFile() as decode_log, tempfile.TemporaryFile() as encode_log:
        decoder = subprocess.Popen(media.decode_command(source, geometry),
                                   stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=decode_log)
        encoding = subprocess.Popen(media.encode_command(source, geometry, info, encoder, output),
                                    stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=encode_log)
        try:
            while True:
                data = decoder.stdout.read(frame_size)
                if len(data) < frame_size:
                    break
                boxes = timeline.frame_boxes(frames, step, sample_boxes, hold)
                if boxes:
                    frame = np.frombuffer(data, dtype=np.uint8).reshape(geometry.height, geometry.width, 3).copy()
                    effects.apply_censoring(frame, boxes)
                    data = frame.data
                    censored += 1
                encoding.stdin.write(data)
                frames += 1
        except BrokenPipeError:
            pass  # the encoder stopped; its exit code says why
        finally:
            try:
                encoding.stdin.close()
            except BrokenPipeError:
                pass
            decoder.stdout.close()
            decoder.wait()
            encoder_code = encoding.wait()
        if frames == 0:
            raise CensorError("no frames could be decoded: " + _tail(decode_log))
        if encoder_code != 0:
            raise EncodeFailed(f"{encoder} encoding failed: " + _tail(encode_log))
    return frames, censored


def _run(command: list[str], action: str) -> None:
    result = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=300)
    if result.returncode != 0:
        raise CensorError(f"{action} failed: " + result.stderr.strip()[-300:])


def _tail(log) -> str:
    log.seek(0)
    return log.read().decode("utf-8", "replace").strip()[-300:]
