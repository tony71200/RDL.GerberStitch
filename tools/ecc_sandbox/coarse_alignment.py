"""Bounded structural translation seeds for Pyramid ECC recovery."""
import math

import cv2
import numpy as np


def _mono8(image):
    arr = np.asarray(image)
    if arr.ndim != 2 or arr.size == 0:
        raise ValueError("Coarse alignment can anh mono8 2D khong rong.")
    if arr.dtype == np.uint8:
        return arr
    return np.clip(arr, 0, 255).astype(np.uint8)


def build_distance_similarity(mono8, cfg):
    """Return a coarse field whose value decreases with distance from an edge."""
    image = _mono8(mono8)
    downsample = max(1, int(cfg["CoarseSearchDownsample"]))
    if downsample > 1:
        width = max(1, int(round(image.shape[1] / float(downsample))))
        height = max(1, int(round(image.shape[0] / float(downsample))))
        image = cv2.resize(image, (width, height), interpolation=cv2.INTER_AREA)

    edges = cv2.Canny(
        image, float(cfg["CoarseCannyLow"]), float(cfg["CoarseCannyHigh"]))
    if np.count_nonzero(edges) < 8:
        return np.zeros(image.shape, dtype=np.float32)

    distance = cv2.distanceTransform(255 - edges, cv2.DIST_L2, 3)
    cap = float(cfg["CoarseDistanceCapPixels"]) / float(downsample)
    if not math.isfinite(cap) or cap <= 0.0:
        raise ValueError("CoarseDistanceCapPixels phai lon hon 0.")
    return (1.0 - np.minimum(distance, cap) / cap).astype(np.float32)


def find_translation_seeds(reference_mono8, moving_mono8, cfg):
    """Find top bounded MovingImage -> ReferenceImage translation seeds."""
    reference = _mono8(reference_mono8)
    moving = _mono8(moving_mono8)
    if reference.shape != moving.shape:
        raise ValueError("Coarse alignment can hai anh cung kich thuoc.")

    ref_field = build_distance_similarity(reference, cfg)
    mov_field = build_distance_similarity(moving, cfg)
    if np.count_nonzero(ref_field) < 8 or np.count_nonzero(mov_field) < 8:
        return []

    downsample = max(1, int(cfg["CoarseSearchDownsample"]))
    max_translation = abs(float(cfg["MaxTranslationPixels"]))
    coarse_bound = max(0, int(math.floor(max_translation / downsample)))
    padded = cv2.copyMakeBorder(
        ref_field, coarse_bound, coarse_bound, coarse_bound, coarse_bound,
        cv2.BORDER_CONSTANT, value=0.0)
    scores = cv2.matchTemplate(padded, mov_field, cv2.TM_CCOEFF_NORMED)
    scores = np.asarray(scores, dtype=np.float32)
    scores[~np.isfinite(scores)] = -1.0

    count = max(0, int(cfg["CoarseCandidateCount"]))
    separation = max(0.0, float(cfg["CoarseCandidateSeparationPixels"]))
    suppress_radius = int(math.ceil(separation / float(downsample)))
    seeds = []
    for _ in range(count):
        _min_value, max_value, _min_location, max_location = cv2.minMaxLoc(scores)
        if not math.isfinite(max_value) or max_value <= -1.0:
            break
        peak_x, peak_y = max_location
        tx = float((peak_x - coarse_bound) * downsample)
        ty = float((peak_y - coarse_bound) * downsample)
        if abs(tx) <= max_translation and abs(ty) <= max_translation:
            seeds.append({
                "matrix": np.array([
                    [1.0, 0.0, tx],
                    [0.0, 1.0, ty],
                    [0.0, 0.0, 1.0],
                ]),
                "source": "structural_bootstrap",
                "coarse_score": float(max_value),
            })
        cv2.circle(scores, (peak_x, peak_y), suppress_radius, -1.0, -1)

    return seeds
