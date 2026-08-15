"""Port 1:1 cua GerberStitching.Core/Matching/OpenCv/PyramidEccMatcher.cs."""
import math
import numpy as np
import cv2

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


def match(reference_mono8, moving_mono8, cfg, initial_moving_to_reference=None):
    """Tra ve dict ket qua, mo phong MatchResult cua C#.

    reference = tile Gerber, moving = anh chup. Transform tra ve la MovingImage -> ReferenceImage,
    dung contract ghi o Diagnostics["TransformDirection"] (:122).
    """
    result = {"success": False, "matcher": "PyramidEccMatcher", "levels": [],
              "failure_reason": None, "message": None}

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

    if initial_moving_to_reference is None:
        full_ref_to_mov = np.eye(3)
    else:
        full_ref_to_mov = np.linalg.inv(np.asarray(initial_moving_to_reference, dtype=float))
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

    moving_to_reference = np.linalg.inv(full_ref_to_mov)
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

    # ValidateMovingToReference (:161) -- dung thu tu kiem tra nhu C#.
    if abs(tx) > cfg["MaxTranslationPixels"] or abs(ty) > cfg["MaxTranslationPixels"]:
        result["failure_reason"] = "NonFiniteTransform"
        result["message"] = "Translation vuot MaxTranslationPixels (%.1f)." % cfg["MaxTranslationPixels"]
        return result
    if math.isnan(correlation) or math.isinf(correlation) or correlation < cfg["EccMinCorrelation"]:
        result["failure_reason"] = "CorrelationBelowThreshold"
        result["message"] = "Correlation %.4f < MinCorrelation %.4f." % (correlation, cfg["EccMinCorrelation"])
        return result
    if abs(rotation_deg) > cfg["MaxAbsRotationDeg"]:
        result["failure_reason"] = "GeometryRejected"
        result["message"] = "Rotation %.4f deg vuot MaxAbsRotationDeg %.4f." % (
            rotation_deg, cfg["MaxAbsRotationDeg"])
        return result
    if scale_value < cfg["MinScale"] or scale_value > cfg["MaxScale"]:
        result["failure_reason"] = "GeometryRejected"
        result["message"] = "Scale %.6f ngoai khoang [%.2f, %.2f]." % (
            scale_value, cfg["MinScale"], cfg["MaxScale"])
        return result

    result["success"] = True
    return result


def warp_moving_to_reference(moving_mono8, matrix, size_wh):
    """Dung de ve ket qua: dua anh moving ve he toa do reference."""
    m = np.asarray(matrix, dtype=np.float32)[:2, :]
    return cv2.warpAffine(moving_mono8, m, size_wh, flags=cv2.INTER_LINEAR)
