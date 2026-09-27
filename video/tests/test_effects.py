import unittest

import numpy as np

from video_censor import effects
from video_censor.timeline import CensorBox


def noise_frame(width=640, height=360):
    return np.random.default_rng(1).integers(0, 256, (height, width, 3), dtype=np.uint8)


class StrengthTests(unittest.TestCase):
    def test_blur_sigma_matches_censor_core(self):
        self.assertEqual(effects.blur_sigma(150, 320, 20), 50)  # 20 * max(2.5, 150 // 100)
        self.assertEqual(effects.blur_sigma(420, 380, 10), 30)
        self.assertEqual(effects.blur_sigma(40, 40, 0), 2.5)

    def test_pixel_size_matches_censor_core(self):
        self.assertEqual(effects.pixel_size(1280, 720, 20), 64)  # 1280 / 3 / 5 * 0.75
        self.assertEqual(effects.pixel_size(1280, 720, 10), 29)
        self.assertEqual(effects.pixel_size(100, 100, 10), 5)

    def test_padding_and_black_bar_growth(self):
        self.assertEqual(effects.padding_for(1280, 720), 18)
        self.assertEqual(effects.padding_for(320, 240), 10)
        self.assertEqual(effects.black_bar_growth(20), 0.2)
        self.assertEqual(effects.black_bar_growth(10), 0)


class DrawingTests(unittest.TestCase):
    def test_black_bars_grow_with_the_level(self):
        frame, original = noise_frame(), noise_frame()
        effects.apply_censoring(frame, [CensorBox(100, 100, 200, 150, "A", "blackbars", 20)])
        self.assertTrue((frame[90:160, 80:220] == 0).all())
        changed = (frame != original).any(axis=2)
        self.assertFalse(changed[:90].any() or changed[160:].any() or changed[:, :80].any() or changed[:, 220:].any())

    def test_blur_covers_the_box_and_fades_out_in_the_padding(self):
        frame, original = noise_frame(), noise_frame()
        effects.apply_censoring(frame, [CensorBox(200, 100, 400, 250, "A", "blur", 10)])
        padding = effects.padding_for(640, 360)
        self.assertLess(frame[100:250, 200:400].std(), 10)  # uniform noise has a standard deviation of about 74
        changed = (frame != original).any(axis=2)
        self.assertFalse(changed[:100 - padding].any() or changed[250 + padding:].any())
        self.assertFalse(changed[:, :200 - padding].any() or changed[:, 400 + padding:].any())
        # partly blended just outside the box
        self.assertTrue(changed[175, 400 + padding // 2])

    def test_pixelate_leaves_blocks(self):
        frame = noise_frame()
        effects.apply_censoring(frame, [CensorBox(160, 80, 480, 280, "A", "pixelate", 20)])
        inside = frame[100:260, 180:460].reshape(-1, 3)
        self.assertLess(len(np.unique(inside, axis=0)), inside.shape[0] / 100)

    def test_boxes_partly_outside_the_frame(self):
        frame, original = noise_frame(), noise_frame()
        effects.apply_censoring(frame, [CensorBox(-50, -50, 20, 20, "A", "blur", 10),
                                        CensorBox(600, 300, 700, 400, "B", "blackbars", 10),
                                        CensorBox(700, 400, 800, 500, "C", "blur", 10)])
        self.assertFalse((frame[0:20, 0:20] == original[0:20, 0:20]).all())
        self.assertTrue((frame[300:, 600:] == 0).all())

    def test_overlapping_blurs_start_from_the_original_frame(self):
        frame = noise_frame()
        boxes = [CensorBox(100, 100, 300, 300, "A", "blur", 5), CensorBox(150, 150, 250, 250, "B", "blur", 5)]
        effects.apply_censoring(frame, boxes)
        single = noise_frame()
        effects.apply_censoring(single, boxes[:1])
        # the larger box is drawn last, from the original frame, so the smaller one doesn't change the result
        self.assertTrue((frame[160:240, 160:240] == single[160:240, 160:240]).all())


if __name__ == "__main__":
    unittest.main()
