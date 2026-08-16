"""Directional chamfer matching: tim (goc, tx, ty) khong phu thuoc gradient cuong do anh.

Khac ECC (Gauss-Newton tren gradient) va Phase Correlation (noi dung tan so cao on dinh) -- chamfer
chi can "diem canh gan mot diem canh nao do", nen chiu duoc trace mong/dut gay va seed lech xa.
Tai dung ha tang da co thay vi viet lai: build_distance_similarity (coarse_alignment) cho buoc loc
tho theo goc, measure_alignment (alignment_quality) cho diem so chamfer that o buoc xep hang cuoi --
chi mot dinh nghia "gan la gan bao nhieu" duy nhat trong codebase.
"""
import math

import cv2
import numpy as np

import alignment_quality
import coarse_alignment


def _mono8(image):
    arr = np.asarray(image)
    if arr.ndim != 2 or arr.size == 0:
        raise ValueError("Chamfer alignment can anh mono8 2D khong rong.")
    if arr.dtype == np.uint8:
        return arr
    return np.clip(arr, 0, 255).astype(np.uint8)


def _rotation_matrix_3x3(angle_deg, center):
    m2x3 = cv2.getRotationMatrix2D(center, angle_deg, 1.0)
    return np.vstack([m2x3, [0.0, 0.0, 1.0]]).astype(float)


def _best_translation(ref_field, mov_field, cfg):
    """Mot dinh duy nhat (khong multi-peak nhu find_translation_seeds -- chamfer da loc theo goc)."""
    downsample = max(1, int(cfg["CoarseSearchDownsample"]))
    max_translation = abs(float(cfg["MaxTranslationPixels"]))
    coarse_bound = max(0, int(math.floor(max_translation / downsample)))
    padded = cv2.copyMakeBorder(
        ref_field, coarse_bound, coarse_bound, coarse_bound, coarse_bound,
        cv2.BORDER_CONSTANT, value=0.0)
    scores = cv2.matchTemplate(padded, mov_field, cv2.TM_CCOEFF_NORMED)
    scores = np.asarray(scores, dtype=np.float32)
    scores[~np.isfinite(scores)] = -1.0
    _min_value, max_value, _min_loc, max_location = cv2.minMaxLoc(scores)
    if not math.isfinite(max_value) or max_value <= -1.0:
        return None
    peak_x, peak_y = max_location
    tx = float((peak_x - coarse_bound) * downsample)
    ty = float((peak_y - coarse_bound) * downsample)
    if abs(tx) > max_translation or abs(ty) > max_translation:
        return None
    return tx, ty, float(max_value)


def _angle_grid(cfg):
    limit = abs(float(cfg["MaxAbsRotationDeg"]))
    step = abs(float(cfg["ChamferAngleStepDeg"]))
    if step <= 0.0:
        return [0.0]
    count = int(math.floor(limit / step))
    return [i * step for i in range(-count, count + 1)]


def find_chamfer_candidates(reference_mono8, moving_mono8, cfg):
    """Tim toi da ChamferCandidateCount ung vien MovingImage -> ReferenceImage.

    Buoc 1 (loc tho, theo goc): xoay moving quanh tam, dung NCC tren distance-similarity field
      de tim tinh tien tot nhat cho tung goc trong luoi [-MaxAbsRotationDeg, +MaxAbsRotationDeg].
    Buoc 2 (xep hang chinh xac): cham diem lai moi ung vien song bang chamfer distance THAT
      (alignment_quality.measure_alignment), khong tin NCC lam diem cuoi -- tranh false convergence
      tren pattern lap lai giong nhu structural bootstrap da lam voi ECC.
    """
    reference = _mono8(reference_mono8)
    moving = _mono8(moving_mono8)
    if reference.shape != moving.shape:
        raise ValueError("Chamfer alignment can hai anh cung kich thuoc.")

    ref_field = coarse_alignment.build_distance_similarity(reference, cfg)
    if np.count_nonzero(ref_field) < 8:
        return []

    height, width = moving.shape
    center = (width / 2.0, height / 2.0)

    raw_candidates = []
    for angle in _angle_grid(cfg):
        try:
            rot = _rotation_matrix_3x3(angle, center)
            rotated_moving = cv2.warpAffine(
                moving, rot[:2, :].astype(np.float32), (width, height),
                flags=cv2.INTER_LINEAR)
            mov_field = coarse_alignment.build_distance_similarity(rotated_moving, cfg)
            if np.count_nonzero(mov_field) < 8:
                continue
            found = _best_translation(ref_field, mov_field, cfg)
        except (ValueError, cv2.error):
            continue
        if found is None:
            continue
        tx, ty, coarse_score = found
        translate = np.array([[1.0, 0.0, tx], [0.0, 1.0, ty], [0.0, 0.0, 1.0]])
        matrix = translate @ rot
        raw_candidates.append({"matrix": matrix, "coarse_score": coarse_score})

    scored = []
    for candidate in raw_candidates:
        metrics = alignment_quality.measure_alignment(reference, moving, candidate["matrix"], cfg)
        if not metrics["eligible"]:
            continue
        scored.append((
            -metrics["symmetric_edge_coverage"],
            metrics["symmetric_chamfer_p95"],
            candidate,
        ))
    scored.sort(key=lambda item: (item[0], item[1]))

    separation = max(0.0, float(cfg["ChamferSeparationPixels"]))
    count = max(0, int(cfg["ChamferCandidateCount"]))
    kept = []
    for _neg_coverage, _chamfer_p95, candidate in scored:
        translation = candidate["matrix"][:2, 2]
        if any(np.linalg.norm(translation - kept_item["matrix"][:2, 2]) < separation
               for kept_item in kept):
            continue
        kept.append({
            "matrix": candidate["matrix"],
            "source": "chamfer_bootstrap",
            "coarse_score": candidate["coarse_score"],
        })
        if len(kept) >= count:
            break

    return kept
