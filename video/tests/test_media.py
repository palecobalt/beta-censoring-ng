import unittest
from fractions import Fraction

from video_censor import media
from video_censor.config import Settings
from video_censor.errors import CensorError, TooLarge
from video_censor.media import Geometry, VideoInfo


def probe_data(streams=None, **video):
    stream = {"codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
              "avg_frame_rate": "30000/1001", "r_frame_rate": "30000/1001"}
    stream.update(video)
    return {"format": {"duration": "12.5"},
            "streams": streams if streams is not None else [stream, {"codec_type": "audio", "codec_name": "aac"}]}


class ParseProbeTests(unittest.TestCase):
    def test_reads_size_rate_duration_and_audio(self):
        info = media.parse_probe(probe_data())
        self.assertEqual(info, VideoInfo(1920, 1080, 12.5, Fraction(30000, 1001), "h264", "aac"))

    def test_rotation_swaps_the_display_size(self):
        rotated = media.parse_probe(probe_data(side_data_list=[{"side_data_type": "Display Matrix", "rotation": -90}]))
        self.assertEqual((rotated.width, rotated.height), (1080, 1920))
        tagged = media.parse_probe(probe_data(tags={"rotate": "270"}))
        self.assertEqual((tagged.width, tagged.height), (1080, 1920))
        upside_down = media.parse_probe(probe_data(side_data_list=[{"rotation": 180}]))
        self.assertEqual((upside_down.width, upside_down.height), (1920, 1080))

    def test_falls_back_to_the_stream_frame_rate(self):
        self.assertEqual(media.parse_probe(probe_data(avg_frame_rate="0/0", r_frame_rate="25/1")).fps, 25)

    def test_ignores_cover_art(self):
        cover = {"codec_type": "video", "codec_name": "mjpeg", "width": 500, "height": 500, "avg_frame_rate": "0/0",
                 "r_frame_rate": "90000/1", "disposition": {"attached_pic": 1}}
        video = {"codec_type": "video", "codec_name": "vp9", "width": 640, "height": 360, "avg_frame_rate": "25/1"}
        info = media.parse_probe(probe_data(streams=[cover, video]))
        self.assertEqual((info.video_codec, info.width, info.audio_codec), ("vp9", 640, None))

    def test_rejects_files_without_video(self):
        with self.assertRaises(CensorError):
            media.parse_probe(probe_data(streams=[{"codec_type": "audio", "codec_name": "mp3"}]))


class LimitTests(unittest.TestCase):
    SETTINGS = Settings(max_bytes=1000, max_seconds=20)
    INFO = VideoInfo(640, 360, 12.5, Fraction(25), "h264", None)

    def test_size_and_duration_limits(self):
        media.check_limits(self.INFO, 1000, self.SETTINGS)
        with self.assertRaises(TooLarge):
            media.check_limits(self.INFO, 1001, self.SETTINGS)
        with self.assertRaises(TooLarge):
            media.check_limits(VideoInfo(640, 360, 20.5, Fraction(25), "h264", None), 10, self.SETTINGS)


class GeometryTests(unittest.TestCase):
    def test_scales_the_short_side_down_and_caps_the_frame_rate(self):
        info = VideoInfo(3840, 2160, 5, Fraction(60), "h264", None)
        self.assertEqual(media.output_geometry(info, 1080, 30), Geometry(1920, 1080, Fraction(30)))
        portrait = VideoInfo(2160, 3840, 5, Fraction(30000, 1001), "h264", None)
        self.assertEqual(media.output_geometry(portrait, 1080, 30), Geometry(1080, 1920, Fraction(30000, 1001)))

    def test_rounds_sizes_down_to_even(self):
        self.assertEqual(media.output_geometry(VideoInfo(853, 481, 5, Fraction(25), "vp9", None), 1080, 30),
                         Geometry(852, 480, Fraction(25)))

    def test_sample_size_fits_the_longest_side(self):
        self.assertEqual(media.sample_size(Geometry(1280, 720, Fraction(30)), 640), (640, 360))
        self.assertEqual(media.sample_size(Geometry(1080, 1920, Fraction(30)), 640), (360, 640))
        self.assertEqual(media.sample_size(Geometry(320, 240, Fraction(30)), 640), (320, 240))


class CommandTests(unittest.TestCase):
    GEOMETRY = Geometry(1280, 720, Fraction(30000, 1001))

    def test_sampling_and_rendering_use_the_same_frame_rate(self):
        sample = media.sample_command("in", self.GEOMETRY, 6, (640, 360), "out/%06d.jpg")
        decode = media.decode_command("in", self.GEOMETRY)
        self.assertIn("fps=30000/1001,select='not(mod(n,6))',scale=640:360", sample)
        self.assertIn("fps=30000/1001,scale=1280:720", decode)

    def test_copies_mp4_compatible_audio_and_reencodes_the_rest(self):
        def audio_args(codec):
            command = media.encode_command("in", self.GEOMETRY, VideoInfo(1280, 720, 5, Fraction(30), "h264", codec), "x264", "out.mp4")
            return command[command.index("-c:a") + 1] if "-c:a" in command else None
        self.assertEqual(audio_args("aac"), "copy")
        self.assertEqual(audio_args("opus"), "copy")
        self.assertEqual(audio_args("vorbis"), "aac")
        self.assertIsNone(audio_args(None))

    def test_clip_inputs_are_limited_to_clip_formats(self):
        info = VideoInfo(1280, 720, 5, Fraction(30), "h264", "aac")
        for command in (media.sample_command("in", self.GEOMETRY, 6, (640, 360), "out/%06d.jpg"),
                        media.decode_command("in", self.GEOMETRY),
                        media.encode_command("in", self.GEOMETRY, info, "x264", "out.mp4")):
            source = command.index("in")
            self.assertEqual(command[source - 3:source], ["-format_whitelist", media.INPUT_FORMATS, "-i"])

    def test_encodes_mp4_with_faststart(self):
        command = media.encode_command("in", self.GEOMETRY, VideoInfo(1280, 720, 5, Fraction(30), "h264", None), "nvenc", "out.mp4")
        self.assertIn("h264_nvenc", command)
        self.assertEqual(command[-5:], ["-movflags", "+faststart", "-f", "mp4", "out.mp4"])


if __name__ == "__main__":
    unittest.main()
