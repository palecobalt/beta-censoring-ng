"""Censoring effects drawn on video frames, sized like censor-core's image effects."""

import math

import cv2
import numpy as np

from .timeline import CensorBox


def padding_for(width: int, height: int, padding_scale: float = 1.0) -> int:
    """Width of the soft edge around blurred and pixelated areas (censor-core's GetPadding)."""
    return round(max(10, min(width, height) / (40 / padding_scale)))


def blur_sigma(box_width: float, box_height: float, level: int) -> float:
    """censor-core's blur strength: grows with the level and, in steps of 100 px, with the box's shorter side."""
    return max(1, level) * max(2.5, min(int(box_width), int(box_height)) // 100)


def pixel_size(image_width: int, image_height: int, level: int) -> int:
    """censor-core's pixelation block size."""
    return max(5, round(max(image_width, image_height) / 3 / max(21 - level, 5) * 0.75))


def black_bar_growth(level: int) -> float:
    """Fraction of a box's size that censor-core's black bars add on each side (none at level 10)."""
    return (level - 10) * 2 / 100


def apply_censoring(frame: np.ndarray, boxes: list[CensorBox]) -> None:
    """Draws the censoring for boxes onto a BGR frame, in place."""
    height, width = frame.shape[:2]
    soft = sorted((b for b in boxes if b.censor_type != "blackbars"), key=lambda b: (b.x2 - b.x1) * (b.y2 - b.y1))
    if soft:
        # every effect starts from the uncensored frame, so overlapping boxes don't blur twice
        source = frame.copy()
        padding = padding_for(width, height)
        for box in soft:
            _draw_soft(frame, source, box, padding)
    for box in boxes:
        if box.censor_type == "blackbars":
            _draw_black_bar(frame, box)


def gaussian_blur(region: np.ndarray, sigma: float) -> np.ndarray:
    height, width = region.shape[:2]
    # strong blurs run on a downscaled copy; the result looks the same and is much faster
    factor = int(min(sigma / 4, min(width, height) / 8))
    if factor >= 2:
        small = cv2.resize(region, (max(1, width // factor), max(1, height // factor)), interpolation=cv2.INTER_AREA)
        small = cv2.GaussianBlur(small, (0, 0), sigma / factor, borderType=cv2.BORDER_REFLECT)
        return cv2.resize(small, (width, height), interpolation=cv2.INTER_LINEAR)
    return cv2.GaussianBlur(region, (0, 0), sigma, borderType=cv2.BORDER_REFLECT)


def pixelate(region: np.ndarray, size: int) -> np.ndarray:
    height, width = region.shape[:2]
    small = cv2.resize(region, (max(1, round(width / size)), max(1, round(height / size))), interpolation=cv2.INTER_AREA)
    return cv2.resize(small, (width, height), interpolation=cv2.INTER_NEAREST)


def feather_mask(width: int, height: int, inner: tuple[int, int, int, int], padding: int) -> np.ndarray:
    """Opacity for a region: 1 inside the inner box, fading to 0 at padding pixels outside it.

    censor-core fades its effects out inside the box, leaving the corners uncovered; video keeps the whole box
    covered and fades out in the padding instead.
    """
    x1, y1, x2, y2 = inner
    xs = np.arange(width, dtype=np.float32) + 0.5
    ys = np.arange(height, dtype=np.float32) + 0.5
    dx = np.maximum(np.maximum(x1 - xs, xs - x2), 0)
    dy = np.maximum(np.maximum(y1 - ys, ys - y2), 0)
    distance = np.sqrt(dx[None, :] ** 2 + dy[:, None] ** 2)
    return np.clip(1 - distance / max(padding, 1), 0, 1)


def _draw_soft(frame: np.ndarray, source: np.ndarray, box: CensorBox, padding: int) -> None:
    height, width = frame.shape[:2]
    x1, y1, x2, y2 = _pixel_bounds(box.x1, box.y1, box.x2, box.y2, width, height)
    if x2 <= x1 or y2 <= y1:
        return
    rx1, ry1, rx2, ry2 = max(0, x1 - padding), max(0, y1 - padding), min(width, x2 + padding), min(height, y2 + padding)
    region = source[ry1:ry2, rx1:rx2]
    if box.censor_type == "pixelate":
        effect = pixelate(region, pixel_size(width, height, box.level))
    else:
        effect = gaussian_blur(region, blur_sigma(box.x2 - box.x1, box.y2 - box.y1, box.level))
    alpha = feather_mask(rx2 - rx1, ry2 - ry1, (x1 - rx1, y1 - ry1, x2 - rx1, y2 - ry1), padding)[..., None]
    target = frame[ry1:ry2, rx1:rx2]
    target[:] = (effect * alpha + target * (1 - alpha) + 0.5).astype(np.uint8)


def _draw_black_bar(frame: np.ndarray, box: CensorBox) -> None:
    height, width = frame.shape[:2]
    growth = black_bar_growth(box.level)
    grow_x, grow_y = (box.x2 - box.x1) * growth, (box.y2 - box.y1) * growth
    x1, y1, x2, y2 = _pixel_bounds(box.x1 - grow_x, box.y1 - grow_y, box.x2 + grow_x, box.y2 + grow_y, width, height)
    if x2 > x1 and y2 > y1:
        frame[y1:y2, x1:x2] = 0


def _pixel_bounds(x1: float, y1: float, x2: float, y2: float, width: int, height: int) -> tuple[int, int, int, int]:
    # round outwards, so partly covered pixels are censored too
    return max(0, math.floor(x1)), max(0, math.floor(y1)), min(width, math.ceil(x2)), min(height, math.ceil(y2))
