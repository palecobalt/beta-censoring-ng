import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import video_rules  # noqa: E402
from video_rules import CENSOR, IGNORE, UNCENSORABLE, CachedVideo, VideoCache  # noqa: E402

FIREFOX_VIDEO_ACCEPT = "video/webm,video/ogg,video/*;q=0.9,application/ogg;q=0.7,audio/*;q=0.6,*/*;q=0.5"
MB = 1024 * 1024


class RequestTests(unittest.TestCase):
    def test_recognises_media_requests(self):
        self.assertTrue(video_rules.is_media_request({"sec-fetch-dest": "video"}, "/v"))
        self.assertTrue(video_rules.is_media_request({"accept": FIREFOX_VIDEO_ACCEPT}, "/v"))
        self.assertTrue(video_rules.is_media_request({}, "/clips/a.MP4?x=1"))
        self.assertFalse(video_rules.is_media_request({"sec-fetch-dest": "image", "accept": "image/avif,*/*"}, "/a.jpg"))

    def test_strips_only_whole_file_ranges_on_media_requests(self):
        self.assertTrue(video_rules.should_strip_range({"sec-fetch-dest": "video", "range": "bytes=0-"}, "/v"))
        self.assertFalse(video_rules.should_strip_range({"sec-fetch-dest": "video", "range": "bytes=1000-"}, "/v"))
        self.assertFalse(video_rules.should_strip_range({"sec-fetch-dest": "video"}, "/v"))
        self.assertFalse(video_rules.should_strip_range({"sec-fetch-dest": "empty", "range": "bytes=0-"}, "/data.bin"))


class ClassifyTests(unittest.TestCase):
    def classify(self, status=200, path="/clip", method="GET", **headers):
        headers = {name.replace("_", "-"): value for name, value in headers.items()}
        return video_rules.classify_response(method, status, headers, path, 25 * MB)[0]

    def test_censors_small_clips(self):
        self.assertEqual(self.classify(content_type="video/mp4", content_length="1000"), CENSOR)
        self.assertEqual(self.classify(content_type="video/webm; codecs=vp9", content_length="1000"), CENSOR)
        self.assertEqual(self.classify(content_type="application/octet-stream", content_length="1000", path="/a.mp4"), CENSOR)

    def test_censors_a_206_covering_the_whole_file(self):
        self.assertEqual(self.classify(206, content_type="video/mp4", content_range="bytes 0-999/1000"), CENSOR)
        self.assertEqual(self.classify(206, content_type="video/mp4", content_range="bytes 0-499/1000"), UNCENSORABLE)
        self.assertEqual(self.classify(206, content_type="video/mp4", content_range="bytes 500-999/1000"), UNCENSORABLE)
        self.assertEqual(self.classify(206, content_type="video/mp4", content_range="bytes 0-999/*"), UNCENSORABLE)

    def test_large_or_unknown_size_clips_are_uncensorable(self):
        self.assertEqual(self.classify(content_type="video/mp4", content_length=str(26 * MB)), UNCENSORABLE)
        self.assertEqual(self.classify(content_type="video/mp4"), UNCENSORABLE)

    def test_adaptive_streams_are_uncensorable(self):
        self.assertEqual(self.classify(content_type="application/vnd.apple.mpegURL", path="/master.m3u8"), UNCENSORABLE)
        self.assertEqual(self.classify(content_type="application/dash+xml"), UNCENSORABLE)
        self.assertEqual(self.classify(content_type="video/mp2t", content_length="1000", path="/seg1.ts"), UNCENSORABLE)
        self.assertEqual(self.classify(content_type="application/octet-stream", path="/seg-3.m4s"), UNCENSORABLE)

    def test_other_video_formats_are_uncensorable(self):
        self.assertEqual(self.classify(content_type="video/x-flv", content_length="1000"), UNCENSORABLE)

    def test_ignores_everything_else(self):
        self.assertEqual(self.classify(content_type="image/jpeg", content_length="1000"), IGNORE)
        self.assertEqual(self.classify(content_type="application/javascript", path="/app.ts"), IGNORE)
        self.assertEqual(self.classify(content_type="audio/mpeg", content_length="1000"), IGNORE)
        self.assertEqual(self.classify(304, content_type="video/mp4"), IGNORE)
        self.assertEqual(self.classify(method="HEAD", content_type="video/mp4", content_length="1000"), IGNORE)


class RangeTests(unittest.TestCase):
    def test_parses_single_ranges(self):
        self.assertEqual(video_rules.parse_range("bytes=0-", 1000), (0, 999))
        self.assertEqual(video_rules.parse_range("bytes=100-199", 1000), (100, 199))
        self.assertEqual(video_rules.parse_range("bytes=900-5000", 1000), (900, 999))
        self.assertEqual(video_rules.parse_range("bytes=-100", 1000), (900, 999))

    def test_sends_the_whole_body_for_missing_or_unhandled_ranges(self):
        for header in (None, "", "bytes=0-1,5-6", "items=0-5", "bytes=500-100"):
            self.assertIsNone(video_rules.parse_range(header, 1000), header)

    def test_unsatisfiable_ranges(self):
        with self.assertRaises(ValueError):
            video_rules.parse_range("bytes=1000-", 1000)
        with self.assertRaises(ValueError):
            video_rules.parse_range("bytes=-0", 1000)

    def test_cached_responses(self):
        video = CachedVideo(bytes(range(256)) * 4, "video/mp4", True)
        status, headers, body = video_rules.cached_response(video, "bytes=10-19")
        self.assertEqual((status, headers["content-range"], body), (206, "bytes 10-19/1024", video.body[10:20]))
        self.assertEqual(headers["x-censored"], "1")
        self.assertEqual(video_rules.cached_response(video, None)[0::2], (200, video.body))
        self.assertEqual(video_rules.cached_response(video, "bytes=0-")[0::2], (200, video.body))
        self.assertEqual(video_rules.cached_response(video, "bytes=2000-")[:2], (416, {"content-type": "video/mp4", "x-censored": "1", "content-range": "bytes */1024"}))


class VideoCacheTests(unittest.TestCase):
    def test_evicts_least_recently_used_clips_over_the_size_limit(self):
        cache = VideoCache(250)
        for key in "abc":
            cache.put(key, CachedVideo(b"x" * 100, "video/mp4", True))
        self.assertEqual((len(cache), cache.get("a")), (2, None))
        cache.get("b")
        cache.put("d", CachedVideo(b"x" * 100, "video/mp4", True))
        self.assertIsNotNone(cache.get("b"))
        self.assertIsNone(cache.get("c"))

    def test_skips_clips_bigger_than_the_cache_and_replaces_existing_keys(self):
        cache = VideoCache(100)
        cache.put("big", CachedVideo(b"x" * 101, "video/mp4", True))
        self.assertEqual(len(cache), 0)
        cache.put("a", CachedVideo(b"x" * 60, "video/mp4", True))
        cache.put("a", CachedVideo(b"y" * 60, "video/mp4", False))
        self.assertEqual(cache.get("a").body, b"y" * 60)
        cache.clear()
        self.assertEqual(len(cache), 0)


if __name__ == "__main__":
    unittest.main()
