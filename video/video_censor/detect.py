"""Runs Beta Censoring's detection endpoint on sampled frames."""

import base64
import json
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

from .errors import DetectionFailed


def detect_frames(paths: list[str], censor_options: dict, settings) -> list[dict]:
    """Returns one detection result ({width, height, results}) per JPEG frame, in order."""
    batches = [paths[i:i + settings.detect_batch] for i in range(0, len(paths), settings.detect_batch)]
    with ThreadPoolExecutor(max(1, settings.detect_concurrency)) as pool:
        results = pool.map(lambda batch: _detect_batch(batch, censor_options, settings), batches)
        return [result for batch in results for result in batch]


def _detect_batch(paths: list[str], censor_options: dict, settings) -> list[dict]:
    images = []
    for path in paths:
        with open(path, "rb") as f:
            images.append("data:image/jpeg;base64," + base64.b64encode(f.read()).decode("ascii"))
    # transform: the same box scaling and merging as image censoring
    body = json.dumps({"imageDataUrls": images, "censorOptions": censor_options, "transform": True}).encode()
    request = urllib.request.Request(f"{settings.censor_url}/censoring/detectBatch", data=body,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=settings.detect_timeout) as response:
            results = json.load(response)
    except (OSError, ValueError) as e:
        raise DetectionFailed(f"detection failed: {e}") from e
    if not isinstance(results, list) or len(results) != len(paths):
        raise DetectionFailed("detection returned the wrong number of results")
    return results
