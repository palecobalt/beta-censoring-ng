import unittest
from fractions import Fraction

from video_censor import timeline
from video_censor.timeline import CensorBox


class SampleStepTests(unittest.TestCase):
    def test_rounds_to_the_nearest_frame(self):
        self.assertEqual(timeline.sample_step(Fraction(30000, 1001), 5), 6)
        self.assertEqual(timeline.sample_step(Fraction(24), 5), 5)

    def test_is_at_least_one(self):
        self.assertEqual(timeline.sample_step(Fraction(3), 5), 1)


class SampleWindowTests(unittest.TestCase):
    def test_frames_between_samples_use_both(self):
        self.assertEqual(list(timeline.sample_window(7, 6, 10)), [1, 2])

    def test_sampled_frames_also_use_their_neighbours(self):
        self.assertEqual(list(timeline.sample_window(6, 6, 10)), [0, 1, 2])
        self.assertEqual(list(timeline.sample_window(0, 6, 10)), [0, 1])

    def test_frames_after_the_last_sample_use_it(self):
        self.assertEqual(list(timeline.sample_window(14, 6, 3)), [2])
        self.assertEqual(list(timeline.sample_window(100, 6, 3)), [2])

    def test_longer_hold(self):
        self.assertEqual(list(timeline.sample_window(7, 6, 10, hold=2)), [0, 1, 2, 3])

    def test_no_samples(self):
        self.assertEqual(list(timeline.sample_window(0, 6, 0)), [])

    def test_a_single_missed_sample_leaves_no_frame_uncensored(self):
        box = CensorBox(0, 0, 10, 10, "FACE_F", "blur", 10)
        samples = [[box], [], [box], [box]]
        uncensored = [frame for frame in range(19) if not timeline.frame_boxes(frame, 6, samples)]
        self.assertEqual(uncensored, [])


class MovingBoxTests(unittest.TestCase):
    SAMPLES = [[CensorBox(0, 0, 10, 10, "A", "blur", 10)], [CensorBox(20, 0, 30, 10, "A", "blur", 20)]]

    def test_boxes_between_samples_follow_the_movement(self):
        self.assertIn(CensorBox(10, 0, 20, 10, "A", "blur", 20), timeline.frame_boxes(3, 6, self.SAMPLES))

    def test_sampled_frames_have_no_moved_boxes(self):
        self.assertEqual(len(timeline.frame_boxes(6, 6, self.SAMPLES)), 2)

    def test_frames_after_the_last_sample_carry_the_movement_on(self):
        self.assertIn(CensorBox(30, 0, 40, 10, "A", "blur", 20), timeline.frame_boxes(9, 6, self.SAMPLES))
        self.assertIn(CensorBox(40, 0, 50, 10, "A", "blur", 20), timeline.frame_boxes(60, 6, self.SAMPLES))

    def test_only_matches_the_same_label_nearby(self):
        start = [CensorBox(0, 0, 10, 10, "A", "blur", 10)]
        self.assertEqual(timeline.moving_boxes(start, [CensorBox(0, 0, 10, 10, "B", "blur", 10)], 0.5), [])
        self.assertEqual(timeline.moving_boxes(start, [CensorBox(100, 0, 110, 10, "A", "blur", 10)], 0.5), [])

    def test_matches_each_box_once(self):
        start = [CensorBox(0, 0, 10, 10, "A", "blur", 10), CensorBox(12, 0, 22, 10, "A", "blur", 10)]
        moved = timeline.moving_boxes(start, [CensorBox(6, 0, 16, 10, "A", "blur", 10)], 0.5)
        self.assertEqual(moved, [CensorBox(3, 0, 13, 10, "A", "blur", 10)])


class CensorTypeTests(unittest.TestCase):
    def test_matches_censor_core_providers(self):
        for name, expected in [("blackbars", "blackbars"), ("bb", "blackbars"), ("BlackBars", "blackbars"),
                               ("pixelate", "pixelate"), ("pixel", "pixelate"), ("blur", "blur")]:
            self.assertEqual(timeline.censor_type({"censorType": name}), expected, name)

    def test_stickers_and_captions_become_blur(self):
        self.assertEqual(timeline.censor_type({"censorType": "sticker"}), "blur")
        self.assertEqual(timeline.censor_type({"censorType": "caption?category=x"}), "blur")

    def test_uncensored(self):
        for options in [None, {}, {"censorType": "none"}, {"censorType": ""}, {"censorType": "unknown"}]:
            self.assertIsNone(timeline.censor_type(options), options)


class CensorBoxesTests(unittest.TestCase):
    OPTIONS = {
        "FACE_F": {"censorType": "blur", "level": 20},
        "EXPOSED_FEET": {"censorType": "none"},
        "EXPOSED_BUTTOCKS": {"CensorType": "blackbars"},
    }

    @staticmethod
    def detection(*matches):
        return {"width": 640, "height": 360, "results": [
            {"label": label, "confidence": 0.9, "box": {"x": x, "y": y, "width": w, "height": h}}
            for label, x, y, w, h in matches]}

    def test_scales_to_the_render_size_and_pads_for_motion(self):
        boxes = timeline.censor_boxes(self.detection(("FACE_F", 100, 50, 40, 20)), (1280, 720), self.OPTIONS, 0.1)
        self.assertEqual(boxes, [CensorBox(192, 96, 288, 144, "FACE_F", "blur", 20)])

    def test_skips_classes_that_are_not_censored(self):
        detection = self.detection(("EXPOSED_FEET", 0, 0, 10, 10), ("COVERED_BELLY", 0, 0, 10, 10))
        self.assertEqual(timeline.censor_boxes(detection, (640, 360), self.OPTIONS, 0), [])

    def test_reads_options_case_insensitively_and_defaults_the_level(self):
        [box] = timeline.censor_boxes(self.detection(("EXPOSED_BUTTOCKS", 0, 0, 10, 10)), (640, 360), self.OPTIONS, 0)
        self.assertEqual((box.censor_type, box.level), ("blackbars", 10))


if __name__ == "__main__":
    unittest.main()
