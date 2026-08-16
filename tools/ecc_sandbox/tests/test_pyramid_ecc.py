import os
import sys
import unittest
from unittest import mock

import cv2
import numpy as np


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import alignment_quality
import coarse_alignment
import config
import pyramid_ecc


def _cfg(**overrides):
    values = dict(config.DEFAULTS)
    values.update({
        "CoarseCandidateCount": 1,
        "MaxTranslationPixels": 40.0,
        "CoarseSearchDownsample": 2,
    })
    values.update(overrides)
    return values


def _structure():
    image = np.zeros((96, 128), dtype=np.uint8)
    cv2.circle(image, (31, 29), 11, 255, 3)
    cv2.rectangle(image, (67, 51), (105, 71), 210, 3)
    cv2.line(image, (25, 80), (91, 86), 255, 3)
    return image


def _success(matrix, source):
    matrix = np.asarray(matrix, dtype=float)
    return {
        "success": True,
        "matcher": "PyramidEccMatcher",
        "source": source,
        "seed_matrix": matrix.copy(),
        "levels": [{"level": 0, "size": (128, 96),
                    "scale": 1.0, "correlation": 0.9}],
        "failure_reason": None,
        "message": None,
        "matrix": matrix,
        "translation_x": float(matrix[0, 2]),
        "translation_y": float(matrix[1, 2]),
        "rotation_deg": 0.0,
        "scale": 1.0,
        "raw_score": 0.9,
        "normalized_confidence": 0.95,
        "pyramid_levels": 1,
        "geometry_valid": True,
    }


def _failure(level, source):
    return {
        "success": False,
        "matcher": "PyramidEccMatcher",
        "source": source,
        "seed_matrix": np.eye(3),
        "levels": [{"level": level, "size": (32, 24),
                    "scale": 0.25, "correlation": None}],
        "failure_reason": "RuntimeFailure",
        "message": "ECC khong hoi tu o level %d: correlation minimized" % level,
        "matrix": None,
        "geometry_valid": False,
    }


def _seed(tx):
    return {
        "matrix": np.array([[1.0, 0.0, float(tx)],
                            [0.0, 1.0, 0.0],
                            [0.0, 0.0, 1.0]]),
        "source": "structural_bootstrap",
        "coarse_score": 0.75,
    }


def _metrics(coverage):
    return {
        "eligible": True,
        "reference_edge_coverage": float(coverage),
        "moving_edge_coverage": float(coverage),
        "symmetric_edge_coverage": float(coverage),
        "symmetric_chamfer_p95": 2.0,
        "reason": None,
    }


class NormalizationRegressionTests(unittest.TestCase):
    def test_min_scale_and_signed_rotation_clamp_are_preserved(self):
        angle = np.deg2rad(-0.7)
        matrix = np.array([
            [1.04 * np.cos(angle), -0.96 * np.sin(angle), 12.0],
            [1.04 * np.sin(angle), 0.96 * np.cos(angle), -8.0],
            [0.0, 0.0, 1.0],
        ])

        normalized, diagnostics = pyramid_ecc._normalize_ecc_result(
            matrix, "Affine", "min", 0.1)

        self.assertAlmostEqual(np.hypot(normalized[0, 0], normalized[1, 0]), 0.96)
        self.assertAlmostEqual(
            np.degrees(np.arctan2(normalized[1, 0], normalized[0, 0])), -0.1)
        self.assertEqual(float(normalized[0, 2]), 12.0)
        self.assertEqual(float(normalized[1, 2]), -8.0)
        self.assertTrue(diagnostics["rotation_clamped"])


class MultiCandidateMatchTests(unittest.TestCase):
    def test_primary_failure_does_not_abort_bootstrap_success(self):
        image = _structure()
        bootstrap_matrix = np.eye(3)
        with mock.patch.object(coarse_alignment, "find_translation_seeds",
                               return_value=[_seed(12.0)]):
            with mock.patch.object(
                    pyramid_ecc, "_run_single_attempt",
                    side_effect=[_failure(2, "primary"),
                                 _success(bootstrap_matrix, "structural_bootstrap")]):
                result = pyramid_ecc.match(image, image, _cfg())

        self.assertTrue(result["success"])
        self.assertEqual(result["verification_status"], "Verified")
        self.assertEqual(len(result["attempts"]), 2)
        self.assertEqual(result["attempts"][0]["failure_reason"], "RuntimeFailure")
        self.assertTrue(np.array_equal(result["matrix"], bootstrap_matrix))

    def test_successful_primary_still_runs_distinct_seed_and_detects_ambiguity(self):
        image = _structure()
        primary = _success(np.eye(3), "primary")
        alternate = _success(_seed(20.0)["matrix"], "structural_bootstrap")
        with mock.patch.object(coarse_alignment, "find_translation_seeds",
                               return_value=[_seed(20.0)]):
            with mock.patch.object(pyramid_ecc, "_run_single_attempt",
                                   side_effect=[primary, alternate]):
                with mock.patch.object(alignment_quality, "measure_alignment",
                                       side_effect=[_metrics(0.80), _metrics(0.79)]):
                    result = pyramid_ecc.match(image, image, _cfg())

        self.assertFalse(result["success"])
        self.assertEqual(result["verification_status"], "Uncertain")
        self.assertEqual(result["failure_reason"], "RepeatedPatternAmbiguous")
        self.assertEqual(len(result["attempts"]), 2)

    def test_all_failures_keep_the_most_informative_level_error(self):
        image = _structure()
        with mock.patch.object(coarse_alignment, "find_translation_seeds",
                               return_value=[_seed(16.0)]):
            with mock.patch.object(
                    pyramid_ecc, "_run_single_attempt",
                    side_effect=[_failure(2, "primary"),
                                 _failure(1, "structural_bootstrap")]):
                result = pyramid_ecc.match(image, image, _cfg())

        self.assertFalse(result["success"])
        self.assertEqual(result["verification_status"], "Rejected")
        self.assertEqual(result["failure_reason"], "RuntimeFailure")
        self.assertIn("level 2", result["message"])
        self.assertEqual(len(result["attempts"]), 2)


if __name__ == "__main__":
    unittest.main()
