"""Independent structural metrics for ECC alignment candidates."""
import math

import cv2
import numpy as np


_MIN_EDGE_PIXELS = 8


def _mono8(image):
    arr = np.asarray(image)
    if arr.ndim != 2 or arr.size == 0:
        raise ValueError("Alignment verification can anh mono8 2D khong rong.")
    if arr.dtype == np.uint8:
        return arr
    return np.clip(arr, 0, 255).astype(np.uint8)


def _edges(image, cfg):
    return cv2.Canny(
        _mono8(image), float(cfg["CoarseCannyLow"]),
        float(cfg["CoarseCannyHigh"]))


def _insufficient(reason="InsufficientStructuralFeatures"):
    return {
        "eligible": False,
        "reference_edge_coverage": 0.0,
        "moving_edge_coverage": 0.0,
        "symmetric_edge_coverage": 0.0,
        "symmetric_chamfer_p95": float("inf"),
        "reason": reason,
    }


def measure_alignment(reference_mono8, moving_mono8, matrix, cfg):
    """Measure bidirectional edge agreement for a Moving -> Reference matrix."""
    reference = _mono8(reference_mono8)
    moving = _mono8(moving_mono8)
    if reference.shape != moving.shape:
        raise ValueError("Alignment verification can hai anh cung kich thuoc.")

    transform = np.asarray(matrix, dtype=float)
    if transform.shape != (3, 3) or not np.isfinite(transform).all():
        return _insufficient("NonFiniteTransform")

    reference_edges = _edges(reference, cfg)
    moving_edges = _edges(moving, cfg)
    reference_yx = np.argwhere(reference_edges > 0)
    moving_yx = np.argwhere(moving_edges > 0)
    if (len(reference_yx) < _MIN_EDGE_PIXELS or
            len(moving_yx) < _MIN_EDGE_PIXELS):
        return _insufficient()

    moving_xy1 = np.column_stack((
        moving_yx[:, 1].astype(float), moving_yx[:, 0].astype(float),
        np.ones(len(moving_yx), dtype=float)))
    transformed = (transform @ moving_xy1.T).T
    denominators = transformed[:, 2]
    finite = (np.isfinite(transformed).all(axis=1) &
              (np.abs(denominators) > 1e-12))
    transformed_xy = np.full((len(moving_yx), 2), np.nan, dtype=float)
    transformed_xy[finite] = (
        transformed[finite, :2] / denominators[finite, None])
    rounded = np.zeros((len(moving_yx), 2), dtype=int)
    rounded[finite] = np.rint(transformed_xy[finite]).astype(int)

    height, width = reference.shape
    in_bounds = (finite &
                 (rounded[:, 0] >= 0) & (rounded[:, 0] < width) &
                 (rounded[:, 1] >= 0) & (rounded[:, 1] < height))
    warped_moving_edges = np.zeros(reference.shape, dtype=np.uint8)
    warped_moving_edges[rounded[in_bounds, 1], rounded[in_bounds, 0]] = 255
    if np.count_nonzero(warped_moving_edges) < _MIN_EDGE_PIXELS:
        return _insufficient()

    distance_to_moving = cv2.distanceTransform(
        255 - warped_moving_edges, cv2.DIST_L2, 3)
    distance_to_reference = cv2.distanceTransform(
        255 - reference_edges, cv2.DIST_L2, 3)
    reference_distances = distance_to_moving[
        reference_yx[:, 0], reference_yx[:, 1]].astype(float)

    outside_distance = math.hypot(width, height)
    moving_distances = np.full(len(moving_yx), outside_distance, dtype=float)
    moving_distances[in_bounds] = distance_to_reference[
        rounded[in_bounds, 1], rounded[in_bounds, 0]]

    tolerance = max(0.0, float(cfg["VerificationEdgeTolerancePixels"]))
    reference_coverage = float(np.mean(reference_distances <= tolerance))
    moving_coverage = float(np.mean(moving_distances <= tolerance))
    if reference_coverage + moving_coverage <= 0.0:
        symmetric_coverage = 0.0
    else:
        symmetric_coverage = float(
            2.0 * reference_coverage * moving_coverage /
            (reference_coverage + moving_coverage))
    chamfer_p95 = float((
        np.percentile(reference_distances, 95) +
        np.percentile(moving_distances, 95)) / 2.0)

    return {
        "eligible": True,
        "reference_edge_coverage": reference_coverage,
        "moving_edge_coverage": moving_coverage,
        "symmetric_edge_coverage": symmetric_coverage,
        "symmetric_chamfer_p95": chamfer_p95,
        "reason": None,
    }


def _finite_ecc_candidate(candidate, cfg):
    matrix = np.asarray(candidate.get("matrix"), dtype=float)
    raw_score = float(candidate.get("raw_score", float("nan")))
    return (candidate.get("geometry_valid", True) and
            matrix.shape == (3, 3) and np.isfinite(matrix).all() and
            math.isfinite(raw_score) and
            raw_score >= float(cfg["EccMinCorrelation"]))


def _rank_key(candidate):
    coverage = float(candidate.get("symmetric_edge_coverage", -1.0))
    chamfer = float(candidate.get("symmetric_chamfer_p95", float("inf")))
    raw_score = float(candidate.get("raw_score", -1.0))
    return (-coverage, chamfer, -raw_score)


def _is_distinct(first, second, cfg):
    first_matrix = np.asarray(first["matrix"], dtype=float)
    second_matrix = np.asarray(second["matrix"], dtype=float)
    translation_distance = float(np.linalg.norm(
        first_matrix[:2, 2] - second_matrix[:2, 2]))
    first_rotation = float(first.get(
        "rotation_deg", math.degrees(math.atan2(first_matrix[1, 0], first_matrix[0, 0]))))
    second_rotation = float(second.get(
        "rotation_deg", math.degrees(math.atan2(second_matrix[1, 0], second_matrix[0, 0]))))
    return (translation_distance > float(cfg["VerificationSameTransformPixels"]) or
            abs(first_rotation - second_rotation) > 0.02)


def classify_candidates(candidates, cfg):
    """Choose a winner and assign Verified, Uncertain, or Rejected."""
    valid = [candidate for candidate in candidates
             if _finite_ecc_candidate(candidate, cfg)]
    if not valid:
        return {
            "success": False,
            "verification_status": "Rejected",
            "failure_reason": "NoValidEccCandidate",
            "message": "Khong co ECC candidate huu han dat nguong correlation va geometry.",
            "winner": None,
            "runner_up": None,
            "coverage_margin": None,
        }

    ranked = sorted(valid, key=_rank_key)
    winner = ranked[0]
    runner_up = next((candidate for candidate in ranked[1:]
                      if _is_distinct(winner, candidate, cfg)), None)
    winner_coverage = float(winner.get("symmetric_edge_coverage", 0.0))
    winner_chamfer = float(winner.get("symmetric_chamfer_p95", float("inf")))
    structural_ok = (
        bool(winner.get("eligible")) and
        math.isfinite(winner_coverage) and
        math.isfinite(winner_chamfer) and
        winner_coverage >= float(cfg["VerificationMinEdgeCoverage"]) and
        winner_chamfer <= float(cfg["VerificationMaxChamferP95Pixels"]))
    if not structural_ok:
        return {
            "success": False,
            "verification_status": "Uncertain",
            "failure_reason": "StructuralVerificationFailed",
            "message": "ECC hoi tu nhung khong dat nguong xac minh cau truc.",
            "winner": winner,
            "runner_up": runner_up,
            "coverage_margin": None,
        }

    coverage_margin = None
    if runner_up is not None:
        runner_coverage = float(runner_up.get("symmetric_edge_coverage", 0.0))
        coverage_margin = winner_coverage - runner_coverage
        if (math.isfinite(runner_coverage) and
                coverage_margin < float(cfg["VerificationMinCoverageMargin"])):
            return {
                "success": False,
                "verification_status": "Uncertain",
                "failure_reason": "RepeatedPatternAmbiguous",
                "message": "Hai transform khac nhau co structural score gan bang nhau.",
                "winner": winner,
                "runner_up": runner_up,
                "coverage_margin": coverage_margin,
            }

    return {
        "success": True,
        "verification_status": "Verified",
        "failure_reason": None,
        "message": "ECC hoi tu va dat xac minh cau truc doc lap.",
        "winner": winner,
        "runner_up": runner_up,
        "coverage_margin": coverage_margin,
    }


def compute_tre(matrix, moving_points, reference_points):
    """Compute target-registration-error statistics for trusted landmarks."""
    transform = np.asarray(matrix, dtype=float)
    moving = np.asarray(moving_points, dtype=float)
    reference = np.asarray(reference_points, dtype=float)
    if transform.shape != (3, 3) or not np.isfinite(transform).all():
        raise ValueError("TRE can ma tran 3x3 huu han.")
    if (moving.ndim != 2 or moving.shape[1:] != (2,) or
            reference.shape != moving.shape or len(moving) == 0 or
            not np.isfinite(moving).all() or not np.isfinite(reference).all()):
        raise ValueError("TRE can hai tap landmark Nx2 huu han, cung kich thuoc va khong rong.")

    homogeneous = np.column_stack((moving, np.ones(len(moving), dtype=float)))
    transformed = (transform @ homogeneous.T).T
    if (not np.isfinite(transformed).all() or
            np.any(np.abs(transformed[:, 2]) <= 1e-12)):
        raise ValueError("TRE transform tao diem khong huu han.")
    transformed_xy = transformed[:, :2] / transformed[:, 2, None]
    errors = np.linalg.norm(transformed_xy - reference, axis=1)
    return {
        "rms": float(math.sqrt(np.mean(errors * errors))),
        "median": float(np.median(errors)),
        "p95": float(np.percentile(errors, 95)),
        "max": float(np.max(errors)),
        "count": int(len(errors)),
    }
