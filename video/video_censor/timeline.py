"""Which boxes to censor on each frame, from the matches found on sampled frames."""

import math
from dataclasses import dataclass
from fractions import Fraction


@dataclass(frozen=True)
class CensorBox:
    """An area to censor, in rendered-frame pixels."""

    x1: float
    y1: float
    x2: float
    y2: float
    label: str
    censor_type: str
    level: int


def sample_step(fps: Fraction, sample_fps: float) -> int:
    """How many frames apart the sampled frames are."""
    return max(1, round(float(fps) / sample_fps))


def sample_window(frame: int, step: int, sample_count: int, hold: int = 1) -> range:
    """The samples whose matches censor a frame as they are: every sample within hold sample intervals of it.

    With hold=1, frames between two samples use both, and sampled frames also use their neighbours, so a
    single sample where the model misses something never leaves a frame uncensored.
    """
    if sample_count <= 0:
        return range(0)
    last = min(sample_count - 1, (frame + hold * step) // step)
    first = min(last, max(0, -(-(frame - hold * step) // step)))
    return range(first, last + 1)


def frame_boxes(frame: int, step: int, sample_boxes: list[list[CensorBox]], hold: int = 1) -> list[CensorBox]:
    """The boxes to censor on a frame: the nearby samples' boxes, plus boxes moved along with anything that moves."""
    boxes = [box for index in sample_window(frame, step, len(sample_boxes), hold) for box in sample_boxes[index]]
    last = len(sample_boxes) - 1
    if last < 1:
        return boxes
    if frame > last * step:
        # after the last sample, carry its movement on for up to one more interval
        start, end, fraction = sample_boxes[last - 1], sample_boxes[last], min(2.0, frame / step - last + 1)
    elif frame % step:
        before = frame // step
        start, end, fraction = sample_boxes[before], sample_boxes[before + 1], frame / step - before
    else:
        return boxes
    return boxes + moving_boxes(start, end, fraction)


def moving_boxes(start: list[CensorBox], end: list[CensorBox], fraction: float) -> list[CensorBox]:
    """Boxes a fraction of the way from each box on one sample to its match on the next (beyond it when fraction > 1)."""
    moved, taken = [], set()
    for box in start:
        index = _nearest_match(box, end, taken)
        if index is None:
            continue
        taken.add(index)
        other = end[index]
        moved.append(CensorBox(*(a + (b - a) * fraction for a, b in zip(_corners(box), _corners(other))),
                               box.label, box.censor_type, max(box.level, other.level)))
    return moved


def censor_type(options: dict | None) -> str | None:
    """The effect for a class's censor options, matching censor-core's providers; None when it isn't censored."""
    name = str(_option(options, "censorType") or "").lower()
    if not name or name == "none":
        return None
    if "bars" in name or name == "bb" or "blackb" in name:
        return "blackbars"
    if name.startswith("pixel"):
        return "pixelate"
    if "blur" in name or name.startswith(("sticker", "caption")):
        # stickers and captions can't follow moving boxes, so video blurs those areas instead
        return "blur"
    return None


def censor_boxes(detection: dict, render_size: tuple[int, int], censor_options: dict, motion_padding: float) -> list[CensorBox]:
    """Converts one sample's detection result to boxes in rendered-frame pixels, keeping only censored classes."""
    width, height = render_size
    scale_x, scale_y = width / detection["width"], height / detection["height"]
    boxes = []
    for match in detection.get("results", []):
        options = censor_options.get(match["label"])
        effect = censor_type(options)
        if effect is None:
            continue
        box = match["box"]
        pad_x, pad_y = box["width"] * motion_padding, box["height"] * motion_padding
        boxes.append(CensorBox(
            (box["x"] - pad_x) * scale_x, (box["y"] - pad_y) * scale_y,
            (box["x"] + box["width"] + pad_x) * scale_x, (box["y"] + box["height"] + pad_y) * scale_y,
            match["label"], effect, _level(options)))
    return boxes


def _nearest_match(box: CensorBox, candidates: list[CensorBox], taken: set[int]) -> int | None:
    """The closest untaken box with the same label whose centre is within twice the boxes' size, so could be the same thing moved."""
    best, best_distance = None, None
    for index, candidate in enumerate(candidates):
        if index in taken or candidate.label != box.label:
            continue
        distance = math.dist(_centre(box), _centre(candidate))
        reach = 2 * max(box.x2 - box.x1, box.y2 - box.y1, candidate.x2 - candidate.x1, candidate.y2 - candidate.y1)
        if distance <= reach and (best_distance is None or distance < best_distance):
            best, best_distance = index, distance
    return best


def _corners(box: CensorBox) -> tuple[float, float, float, float]:
    return box.x1, box.y1, box.x2, box.y2


def _centre(box: CensorBox) -> tuple[float, float]:
    return (box.x1 + box.x2) / 2, (box.y1 + box.y2) / 2


def _option(options: dict | None, name: str):
    # the server reads options case-insensitively, so do the same
    for key, value in (options or {}).items():
        if key.lower() == name.lower():
            return value
    return None


def _level(options: dict | None) -> int:
    try:
        return int(_option(options, "level"))
    except (TypeError, ValueError):
        return 10
