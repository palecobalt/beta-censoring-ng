"""Runs the pipeline on generated clips with real ffmpeg, with detection faked."""

import json
import os
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

import cv2
import numpy as np

from video_censor import media, pipeline
from video_censor.config import Settings
from video_censor.errors import CensorError, TooLarge

OPTIONS = {"FACE_F": {"censorType": "blackbars", "level": 10}}
SETTINGS = Settings(censor_url="http://unused", motion_padding=0)


def fake_detection(labels=("FACE_F",)):
    """A box over the middle half of every sampled frame."""
    def detect_frames(paths, censor_options, settings):
        height, width = cv2.imread(paths[0]).shape[:2]
        box = {"x": width / 4, "y": height / 4, "width": width / 2, "height": height / 2}
        return [{"width": width, "height": height,
                 "results": [{"label": label, "confidence": 0.9, "box": box} for label in labels]} for _ in paths]
    return detect_frames


def detect_bright_areas(paths, censor_options, settings):
    """A box around the bright pixels on each sampled frame."""
    results = []
    for path in paths:
        gray = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
        ys, xs = np.nonzero(gray > 128)
        matches = [] if len(xs) == 0 else [{"label": "FACE_F", "confidence": 0.9, "box": {
            "x": float(xs.min()), "y": float(ys.min()), "width": float(xs.max() - xs.min() + 1), "height": float(ys.max() - ys.min() + 1)}}]
        results.append({"width": gray.shape[1], "height": gray.shape[0], "results": matches})
    return results


def ffprobe(data: bytes) -> dict:
    with tempfile.NamedTemporaryFile(suffix=".mp4") as f:
        f.write(data)
        f.flush()
        result = subprocess.run(["ffprobe", "-v", "error", "-count_frames", "-print_format", "json", "-show_streams", "-show_format", f.name],
                                capture_output=True, text=True, check=True)
    return json.loads(result.stdout)


def gray_frames(data: bytes, width: int, height: int) -> np.ndarray:
    with tempfile.NamedTemporaryFile(suffix=".mp4") as f:
        f.write(data)
        f.flush()
        raw = subprocess.run(["ffmpeg", "-v", "error", "-i", f.name, "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"],
                             capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.uint8).reshape(-1, height, width)


@unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "needs ffmpeg and ffprobe")
class PipelineTests(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.work)

    def encode(self, inputs: list[str], rotation=None) -> bytes:
        path = os.path.join(self.work, "clip.mp4")
        subprocess.run(["ffmpeg", "-v", "error", "-y", *inputs, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path], check=True)
        if rotation is not None:
            # rotation metadata, like phone videos have
            rotated = os.path.join(self.work, "rotated.mp4")
            subprocess.run(["ffmpeg", "-v", "error", "-y", "-display_rotation:v:0", str(rotation), "-i", path, "-c", "copy", rotated], check=True)
            path = rotated
        with open(path, "rb") as f:
            return f.read()

    def make_clip(self, seconds=2, size="320x240", rate=25, rotation=None) -> bytes:
        return self.encode(["-f", "lavfi", "-i", f"testsrc2=s={size}:r={rate}:d={seconds}",
                            "-f", "lavfi", "-i", f"sine=frequency=440:duration={seconds}"], rotation)

    def test_censors_every_frame_and_keeps_the_audio(self):
        clip = self.make_clip()
        with mock.patch("video_censor.detect.detect_frames", fake_detection()):
            result = pipeline.censor_clip(clip, OPTIONS, SETTINGS, "x264")
        self.assertTrue(result.censored)
        streams = {s["codec_type"]: s for s in ffprobe(result.body)["streams"]}
        self.assertEqual((streams["video"]["width"], streams["video"]["height"]), (320, 240))
        self.assertEqual(int(streams["video"]["nb_read_frames"]), 50)
        self.assertEqual(streams["audio"]["codec_name"], "aac")
        self.assertEqual((result.stats["frames"], result.stats["censored_frames"]), (50, 50))
        frames = gray_frames(result.body, 320, 240)
        for index in (0, 13, 49):
            self.assertLess(frames[index, 70:170, 90:230].mean(), 20)  # the black bar
            self.assertGreater(frames[index, :50].mean(), 40)  # testsrc2's colours around it

    def test_boxes_follow_content_that_moves_between_samples(self):
        # a square moving 60 px per sample interval, 1.5 times its own width
        clip = self.encode(["-f", "lavfi", "-i", "color=black:s=400x240:r=25:d=1[bg];color=white:s=40x40:r=25:d=1[fg];"
                                                 "[bg][fg]overlay=x='t*300':y=100:shortest=1"])
        with mock.patch("video_censor.detect.detect_frames", detect_bright_areas):
            result = pipeline.censor_clip(clip, OPTIONS, Settings(censor_url="http://unused"), "x264")
        frames = gray_frames(result.body, 400, 240)
        self.assertEqual(len(frames), 25)
        exposed = {index: int((frame > 128).sum()) for index, frame in enumerate(frames) if (frame > 128).any()}
        self.assertEqual(exposed, {})

    def test_returns_the_original_when_nothing_is_censored(self):
        clip = self.make_clip(seconds=1)
        with mock.patch("video_censor.detect.detect_frames", fake_detection(labels=("EXPOSED_FEET",))):
            result = pipeline.censor_clip(clip, OPTIONS, SETTINGS, "x264")
        self.assertFalse(result.censored)
        self.assertEqual(result.body, clip)

    def test_refuses_playlists(self):
        # a playlist pointing at another file, which ffmpeg would otherwise open
        self.make_clip(seconds=1)
        playlist = os.path.join(self.work, "source")
        with open(playlist, "w") as f:
            f.write("ffconcat version 1.0\nfile clip.mp4\n")
        with self.assertRaises(CensorError):
            media.probe(playlist)

    def test_returns_audio_only_files_unchanged(self):
        path = os.path.join(self.work, "audio.webm")
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", "libopus", path], check=True)
        with open(path, "rb") as f:
            audio = f.read()
        result = pipeline.censor_clip(audio, OPTIONS, SETTINGS, "x264")
        self.assertFalse(result.censored)
        self.assertEqual(result.body, audio)

    def test_rotated_video_keeps_its_display_orientation(self):
        clip = self.make_clip(seconds=1, rotation=90)
        with mock.patch("video_censor.detect.detect_frames", fake_detection()):
            result = pipeline.censor_clip(clip, OPTIONS, SETTINGS, "x264")
        video = next(s for s in ffprobe(result.body)["streams"] if s["codec_type"] == "video")
        self.assertEqual((video["width"], video["height"]), (240, 320))

    def test_rejects_clips_over_the_limits(self):
        clip = self.make_clip(seconds=3)
        with self.assertRaises(TooLarge):
            pipeline.censor_clip(clip, OPTIONS, Settings(max_seconds=2), "x264")
        with self.assertRaises(TooLarge):
            pipeline.censor_clip(clip, OPTIONS, Settings(max_bytes=1000), "x264")


if __name__ == "__main__":
    unittest.main()
