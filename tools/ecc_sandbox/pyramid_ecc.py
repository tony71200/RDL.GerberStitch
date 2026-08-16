"""Port 1:1 cua GerberStitching.Core/Matching/OpenCv/PyramidEccMatcher.cs."""
import math
import numpy as np
import cv2

import alignment_quality
import chamfer_alignment
import coarse_alignment

MOTION = {
    "Translation": cv2.MOTION_TRANSLATION,
    "Euclidean": cv2.MOTION_EUCLIDEAN,
    "Affine": cv2.MOTION_AFFINE,
}


def _restrict_motion(m, motion_model):
    """RestrictMotion (PyramidEccMatcher.cs:277)."""
    if motion_model == "Translation":
        return np.array([[1.0, 0.0, m[0, 2]], [0.0, 1.0, m[1, 2]], [0.0, 0.0, 1.0]])
    if motion_model == "Euclidean":
        angle = math.atan2(m[1, 0], m[0, 0])
        c, s = math.cos(angle), math.sin(angle)
        return np.array([[c, -s, m[0, 2]], [s, c, m[1, 2]], [0.0, 0.0, 1.0]])
    return np.array([[m[0, 0], m[0, 1], m[0, 2]],
                     [m[1, 0], m[1, 1], m[1, 2]],
                     [0.0, 0.0, 1.0]])


def _to_warp_at_scale(full, scale, motion_model):
    """ToWarpMatAtScale (:256) -- CHI translation duoc nhan scale."""
    r = _restrict_motion(full, motion_model)
    return np.array([[r[0, 0], r[0, 1], r[0, 2] * scale],
                     [r[1, 0], r[1, 1], r[1, 2] * scale]], dtype=np.float32)


def _from_warp_at_scale(warp, scale):
    """FromWarpMatAtScale (:269)."""
    s = 1.0 if abs(scale) < 1e-12 else scale
    return np.array([[float(warp[0, 0]), float(warp[0, 1]), float(warp[0, 2]) / s],
                     [float(warp[1, 0]), float(warp[1, 1]), float(warp[1, 2]) / s],
                     [0.0, 0.0, 1.0]])


def _build_pyramids(ref32, mov32, levels):
    """BuildPyramids (:219). Index 0 = FULL resolution."""
    rp, mp = [ref32.copy()], [mov32.copy()]
    for _ in range(1, max(1, levels)):
        if rp[-1].shape[1] < 32 or rp[-1].shape[0] < 32:
            break
        rp.append(cv2.pyrDown(rp[-1]))
        mp.append(cv2.pyrDown(mp[-1]))
    return rp, mp


def _normalize_ecc_result(matrix, motion_model, mode, max_abs_rotation_deg):
    """Chuan hoa transform cuoi cung, sau khi ECC da hoi tu.

    Affine: dong nhat scale X/Y va loai shear. Affine/Euclidean: clamp rotation co giu dau.
    Translation duoc giu nguyen. Translation cua moi ma tran luon duoc bao toan.
    """
    m = np.asarray(matrix, dtype=float)
    if m.shape != (3, 3) or not np.isfinite(m).all():
        raise ValueError("ECC result khong phai ma tran 3x3 huu han.")

    scale_x = math.hypot(m[0, 0], m[1, 0])
    scale_y = math.hypot(m[0, 1], m[1, 1])
    raw_rotation_deg = math.degrees(math.atan2(m[1, 0], m[0, 0]))

    if motion_model == "Affine":
        if mode == "median":
            uniform_scale = (scale_x + scale_y) / 2.0
        elif mode == "min":
            uniform_scale = min(scale_x, scale_y)
        else:
            raise ValueError("AffineNormalize phai la 'median' hoac 'min'.")
    else:
        uniform_scale = scale_x

    if not all(math.isfinite(v) for v in
               (scale_x, scale_y, raw_rotation_deg, uniform_scale)):
        raise ValueError("ECC result chua gia tri hinh hoc khong huu han.")
    if uniform_scale <= 1e-12:
        raise ValueError("ECC result co scale suy bien.")

    rotation_limit = abs(float(max_abs_rotation_deg))
    if not math.isfinite(rotation_limit):
        raise ValueError("MaxAbsRotationDeg phai la gia tri huu han.")
    rotation_deg = max(-rotation_limit, min(raw_rotation_deg, rotation_limit))
    rotation_clamped = abs(rotation_deg - raw_rotation_deg) > 1e-12
    should_rebuild = motion_model == "Affine" or rotation_clamped

    if should_rebuild:
        angle = math.radians(rotation_deg)
        c = math.cos(angle) * uniform_scale
        s = math.sin(angle) * uniform_scale
        normalized = np.array([
            [c, -s, m[0, 2]],
            [s, c, m[1, 2]],
            [0.0, 0.0, 1.0],
        ])
    else:
        normalized = m.copy()

    diagnostics = {
        "raw_scale_x": scale_x,
        "raw_scale_y": scale_y,
        "raw_rotation_deg": raw_rotation_deg,
        "affine_normalize": mode if motion_model == "Affine" else None,
        "rotation_clamped": rotation_clamped,
    }
    return normalized, diagnostics


def _run_single_attempt(reference_mono8, moving_mono8, cfg,
                        initial_moving_to_reference=None, source="primary"):
    """Run one pyramid-ECC candidate without aborting sibling candidates.

    reference = tile Gerber, moving = anh chup. Transform tra ve la MovingImage -> ReferenceImage,
    dung contract ghi o Diagnostics["TransformDirection"] (:122).
    """
    result = {"success": False, "matcher": "PyramidEccMatcher", "levels": [],
              "failure_reason": None, "message": None, "matrix": None,
              "source": source, "geometry_valid": False}

    if initial_moving_to_reference is None:
        seed_matrix = np.eye(3)
    else:
        seed_matrix = np.asarray(initial_moving_to_reference, dtype=float)
    result["seed_matrix"] = seed_matrix.copy()

    if reference_mono8.shape != moving_mono8.shape:
        result["failure_reason"] = "SizeMismatch"
        result["message"] = ("ReferenceImage va MovingImage phai cung kich thuoc "
                             "(MatcherGeometryValidator.ValidatePreparedPair). "
                             "reference=%s moving=%s" % (reference_mono8.shape, moving_mono8.shape))
        return result

    ref32 = reference_mono8.astype(np.float32)
    mov32 = moving_mono8.astype(np.float32)

    motion_model = cfg["EccMotionModel"]
    motion_type = MOTION[motion_model]
    rp, mp = _build_pyramids(ref32, mov32, cfg["EccPyramidLevels"])

    try:
        if seed_matrix.shape != (3, 3) or not np.isfinite(seed_matrix).all():
            raise ValueError("Initial transform khong phai ma tran 3x3 huu han.")
        full_ref_to_mov = np.linalg.inv(seed_matrix)
    except (np.linalg.LinAlgError, ValueError) as ex:
        result["failure_reason"] = "NonFiniteTransform"
        result["message"] = "Initial transform khong kha nghich: %s" % ex
        return result
    full_ref_to_mov = _restrict_motion(full_ref_to_mov, motion_model)

    correlation = float("nan")
    criteria = (cv2.TERM_CRITERIA_COUNT | cv2.TERM_CRITERIA_EPS,
                max(1, int(cfg["EccMaxIterations"])), float(cfg["EccEpsilon"]))

    # Chay tu muc THO nhat (index lon nhat) ve muc day du (index 0).
    for level in range(len(rp) - 1, -1, -1):
        scale = rp[level].shape[1] / float(max(1, ref32.shape[1]))
        warp = _to_warp_at_scale(full_ref_to_mov, scale, motion_model)
        try:
            # THU TU: template = reference, input = moving. gaussFiltSize=5 khop OpenCvSharp.
            correlation, warp = cv2.findTransformECC(
                rp[level], mp[level], warp, motion_type, criteria,
                None, int(cfg["EccGaussFiltSize"]))
        except cv2.error as ex:
            result["failure_reason"] = "RuntimeFailure"
            result["message"] = "ECC khong hoi tu o level %d: %s" % (level, ex)
            result["levels"].append({"level": level, "size": rp[level].shape[::-1],
                                     "scale": scale, "correlation": None})
            return result
        full_ref_to_mov = _from_warp_at_scale(warp, scale)
        result["levels"].append({"level": level, "size": rp[level].shape[::-1],
                                 "scale": scale, "correlation": float(correlation)})

    try:
        moving_to_reference = np.linalg.inv(full_ref_to_mov)
    except np.linalg.LinAlgError as ex:
        result["failure_reason"] = "NonFiniteTransform"
        result["message"] = "ECC result khong kha nghich: %s" % ex
        return result
    try:
        moving_to_reference, geometry_diagnostics = _normalize_ecc_result(
            moving_to_reference, motion_model, cfg["AffineNormalize"],
            cfg["MaxAbsRotationDeg"])
    except ValueError as ex:
        result["failure_reason"] = "NonFiniteTransform"
        result["message"] = str(ex)
        return result

    tx = moving_to_reference[0, 2]
    ty = moving_to_reference[1, 2]
    rotation_deg = math.degrees(math.atan2(moving_to_reference[1, 0], moving_to_reference[0, 0]))
    scale_value = math.hypot(moving_to_reference[0, 0], moving_to_reference[1, 0])

    result.update({
        "matrix": moving_to_reference,
        "translation_x": tx,
        "translation_y": ty,
        "rotation_deg": rotation_deg,
        "scale": scale_value,
        "raw_score": float(correlation),
        "normalized_confidence": max(0.0, min(1.0, (correlation + 1.0) / 2.0)),
        "pyramid_levels": len(rp),
    })
    result.update(geometry_diagnostics)

    # Giu thu tu validation cua C#, tru rotation da duoc clamp o tren thay vi reject.
    if abs(tx) > cfg["MaxTranslationPixels"] or abs(ty) > cfg["MaxTranslationPixels"]:
        result["failure_reason"] = "GeometryRejected"
        result["message"] = "Translation vuot MaxTranslationPixels (%.1f)." % cfg["MaxTranslationPixels"]
        return result
    if math.isnan(correlation) or math.isinf(correlation) or correlation < cfg["EccMinCorrelation"]:
        result["failure_reason"] = "CorrelationBelowThreshold"
        result["message"] = "Correlation %.4f < MinCorrelation %.4f." % (correlation, cfg["EccMinCorrelation"])
        return result
    if scale_value < cfg["MinScale"] or scale_value > cfg["MaxScale"]:
        result["failure_reason"] = "GeometryRejected"
        result["message"] = "Scale %.6f ngoai khoang [%.2f, %.2f]." % (
            scale_value, cfg["MinScale"], cfg["MaxScale"])
        return result

    result["geometry_valid"] = True
    result["success"] = True
    return result


def _seed_is_duplicate(matrix, used_matrices):
    candidate = np.asarray(matrix, dtype=float)
    if candidate.shape != (3, 3) or not np.isfinite(candidate).all():
        return True
    return any(np.linalg.norm(candidate[:2, 2] - used[:2, 2]) <= 1.0
               for used in used_matrices)


def _failed_level(attempt):
    levels = attempt.get("levels") or []
    failed = [int(level["level"]) for level in levels
              if level.get("correlation") is None]
    return max(failed) if failed else -1


def _most_informative_failure(attempts):
    runtime_failures = [attempt for attempt in attempts
                        if attempt.get("failure_reason") == "RuntimeFailure"]
    if runtime_failures:
        return max(runtime_failures, key=_failed_level)
    return attempts[0] if attempts else {
        "success": False,
        "matcher": "PyramidEccMatcher",
        "levels": [],
        "matrix": None,
        "failure_reason": "NoValidEccCandidate",
        "message": "Khong co ECC candidate de danh gia.",
    }


def match(reference_mono8, moving_mono8, cfg, initial_moving_to_reference=None,
          verification_reference=None, verification_moving=None):
    """Run primary plus structural seeds and verify the resulting candidates."""
    primary_seed = (np.eye(3) if initial_moving_to_reference is None
                    else np.asarray(initial_moving_to_reference, dtype=float))
    attempts = [_run_single_attempt(
        reference_mono8, moving_mono8, cfg, primary_seed, "primary")]

    if reference_mono8.shape == moving_mono8.shape:
        used_matrices = []
        if (primary_seed.shape == (3, 3) and
                np.isfinite(primary_seed).all()):
            used_matrices.append(primary_seed.copy())
        try:
            seeds = coarse_alignment.find_translation_seeds(
                reference_mono8, moving_mono8, cfg)
        except (ValueError, cv2.error) as ex:
            seeds = []
            attempts.append({
                "success": False,
                "matcher": "PyramidEccMatcher",
                "source": "structural_bootstrap",
                "seed_matrix": None,
                "levels": [],
                "matrix": None,
                "geometry_valid": False,
                "failure_reason": "CoarseBootstrapFailure",
                "message": str(ex),
            })
        for seed in seeds:
            seed_matrix = np.asarray(seed["matrix"], dtype=float)
            if _seed_is_duplicate(seed_matrix, used_matrices):
                continue
            used_matrices.append(seed_matrix.copy())
            attempt = _run_single_attempt(
                reference_mono8, moving_mono8, cfg, seed_matrix,
                seed.get("source", "structural_bootstrap"))
            attempt["coarse_score"] = float(seed.get("coarse_score", float("nan")))
            attempts.append(attempt)

        try:
            chamfer_seeds = chamfer_alignment.find_chamfer_candidates(
                reference_mono8, moving_mono8, cfg)
        except (ValueError, cv2.error) as ex:
            chamfer_seeds = []
            attempts.append({
                "success": False,
                "matcher": "PyramidEccMatcher",
                "source": "chamfer_bootstrap",
                "seed_matrix": None,
                "levels": [],
                "matrix": None,
                "geometry_valid": False,
                "failure_reason": "ChamferBootstrapFailure",
                "message": str(ex),
            })
        for seed in chamfer_seeds:
            seed_matrix = np.asarray(seed["matrix"], dtype=float)
            if _seed_is_duplicate(seed_matrix, used_matrices):
                continue
            used_matrices.append(seed_matrix.copy())
            attempt = _run_single_attempt(
                reference_mono8, moving_mono8, cfg, seed_matrix,
                seed.get("source", "chamfer_bootstrap"))
            attempt["coarse_score"] = float(seed.get("coarse_score", float("nan")))
            attempts.append(attempt)

    quality_reference = (reference_mono8 if verification_reference is None
                         else verification_reference)
    quality_moving = (moving_mono8 if verification_moving is None
                      else verification_moving)
    candidates = []
    for attempt in attempts:
        if attempt.get("geometry_valid") and attempt.get("matrix") is not None:
            metrics = alignment_quality.measure_alignment(
                quality_reference, quality_moving, attempt["matrix"], cfg)
            attempt.update(metrics)
            candidates.append(attempt)

    classification = alignment_quality.classify_candidates(candidates, cfg)
    winner = classification["winner"]
    if winner is None:
        final = dict(_most_informative_failure(attempts))
        final["success"] = False
        final["verification_status"] = "Rejected"
    else:
        final = dict(winner)
        final["success"] = bool(classification["success"])
        final["verification_status"] = classification["verification_status"]
        final["failure_reason"] = classification["failure_reason"]
        final["message"] = classification["message"]

    final["attempts"] = attempts
    final["runner_up"] = classification["runner_up"]
    final["coverage_margin"] = classification["coverage_margin"]
    return final


def warp_moving_to_reference(moving_mono8, matrix, size_wh):
    """Dung de ve ket qua: dua anh moving ve he toa do reference."""
    m = np.asarray(matrix, dtype=np.float32)[:2, :]
    return cv2.warpAffine(moving_mono8, m, size_wh, flags=cv2.INTER_LINEAR)
