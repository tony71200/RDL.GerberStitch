import os
import sys
import unittest

import cv2
import numpy as np


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import alignment_quality
import config


def _cfg(**overrides):
    values = dict(config.DEFAULTS)
    values.update(overrides)
    return values


def _structure():
    image = np.zeros((128, 160), dtype=np.uint8)
    cv2.circle(image, (38, 38), 15, 255, 3)
    cv2.line(image, (68, 29), (139, 54), 255, 4)
    cv2.rectangle(image, (77, 83), (124, 105), 210, 3)
    return image


def _candidate(tx, coverage, chamfer=2.0, raw_score=0.8):
    return {
        "matrix": np.array([[1.0, 0.0, float(tx)],
                            [0.0, 1.0, 0.0],
                            [0.0, 0.0, 1.0]]),
        "translation_x": float(tx),
        "translation_y": 0.0,
        "rotation_deg": 0.0,
        "raw_score": float(raw_score),
        "geometry_valid": True,
        "eligible": True,
        "reference_edge_coverage": float(coverage),
        "moving_edge_coverage": float(coverage),
        "symmetric_edge_coverage": float(coverage),
        "symmetric_chamfer_p95": float(chamfer),
    }


class AlignmentMetricTests(unittest.TestCase):
    def test_exact_transform_beats_displaced_transform(self):
        reference = _structure()
        moving = reference.copy()
        exact = np.eye(3)
        displaced = np.array([[1.0, 0.0, 14.0],
                              [0.0, 1.0, -9.0],
                              [0.0, 0.0, 1.0]])

        exact_metrics = alignment_quality.measure_alignment(
            reference, moving, exact, _cfg())
        displaced_metrics = alignment_quality.measure_alignment(
            reference, moving, displaced, _cfg())

        self.assertTrue(exact_metrics["eligible"])
        self.assertGreater(exact_metrics["symmetric_edge_coverage"],
                           displaced_metrics["symmetric_edge_coverage"])
        self.assertLess(exact_metrics["symmetric_chamfer_p95"],
                        displaced_metrics["symmetric_chamfer_p95"])
        self.assertAlmostEqual(exact_metrics["symmetric_edge_coverage"], 1.0, places=6)

    def test_blank_images_are_insufficient_structural_features(self):
        blank = np.zeros((64, 64), dtype=np.uint8)

        metrics = alignment_quality.measure_alignment(
            blank, blank, np.eye(3), _cfg())

        self.assertFalse(metrics["eligible"])
        self.assertEqual(metrics["reason"], "InsufficientStructuralFeatures")


class CandidateClassificationTests(unittest.TestCase):
    def test_distinct_near_equal_runner_up_is_uncertain(self):
        candidates = [_candidate(0.0, 0.80), _candidate(20.0, 0.79)]

        result = alignment_quality.classify_candidates(candidates, _cfg())

        self.assertFalse(result["success"])
        self.assertEqual(result["verification_status"], "Uncertain")
        self.assertEqual(result["failure_reason"], "RepeatedPatternAmbiguous")
        self.assertAlmostEqual(result["coverage_margin"], 0.01, places=6)

    def test_unique_structural_winner_is_verified(self):
        candidates = [_candidate(0.0, 0.80), _candidate(20.0, 0.70)]

        result = alignment_quality.classify_candidates(candidates, _cfg())

        self.assertTrue(result["success"])
        self.assertEqual(result["verification_status"], "Verified")
        self.assertIs(result["winner"], candidates[0])

    def test_candidate_below_structural_threshold_is_uncertain(self):
        candidate = _candidate(0.0, 0.10, chamfer=20.0)

        result = alignment_quality.classify_candidates([candidate], _cfg())

        self.assertFalse(result["success"])
        self.assertEqual(result["verification_status"], "Uncertain")
        self.assertEqual(result["failure_reason"], "StructuralVerificationFailed")


class TargetRegistrationErrorTests(unittest.TestCase):
    def test_exact_landmarks_have_zero_tre(self):
        points = np.array([[0.0, 0.0], [3.0, 4.0], [10.0, -2.0]])

        result = alignment_quality.compute_tre(np.eye(3), points, points)

        self.assertEqual(result, {"rms": 0.0, "median": 0.0,
                                  "p95": 0.0, "max": 0.0,
                                  "count": 3})

    def test_translation_against_unshifted_landmarks_has_five_pixel_tre(self):
        moving = np.array([[0.0, 0.0], [10.0, 2.0], [-4.0, 8.0]])
        matrix = np.array([[1.0, 0.0, 3.0],
                           [0.0, 1.0, 4.0],
                           [0.0, 0.0, 1.0]])

        result = alignment_quality.compute_tre(matrix, moving, moving)

        self.assertAlmostEqual(result["rms"], 5.0)
        self.assertAlmostEqual(result["median"], 5.0)
        self.assertAlmostEqual(result["p95"], 5.0)
        self.assertAlmostEqual(result["max"], 5.0)
        self.assertEqual(result["count"], 3)

    def test_empty_landmarks_are_rejected(self):
        with self.assertRaises(ValueError):
            alignment_quality.compute_tre(np.eye(3), [], [])


class SummarizeConsistencyTests(unittest.TestCase):
    def _case(self, row, column, scale, tx, ty, has_matrix=True):
        return {
            "row": row, "column": column, "scale": scale,
            "translation_x": tx, "translation_y": ty,
            "matrix": (np.eye(3).tolist() if has_matrix else None),
        }

    def test_computes_scale_spread_and_translation_slope_on_known_data(self):
        results = []
        for row in range(3):
            for column in range(4):
                tx = 5.0 * column + 100.0
                ty = -3.0 * row + 50.0
                scale = 0.98
                results.append(self._case(row, column, scale, tx, ty))
        # inject one outlier scale, matching the Findings-style signature this is meant to catch
        results[0]["scale"] = 0.94

        summary = alignment_quality.summarize_consistency(results)

        self.assertIsNotNone(summary)
        self.assertEqual(summary["n"], 12)
        self.assertAlmostEqual(summary["scale_spread"], 0.04, places=6)
        self.assertAlmostEqual(
            summary["translation_x_per_column"]["slope"], 5.0, places=4)
        self.assertAlmostEqual(
            summary["translation_y_per_row"]["slope"], -3.0, places=4)
        self.assertLess(summary["translation_x_per_column"]["residual_std"], 1e-6)

    def test_fewer_than_two_valid_cases_returns_none(self):
        results = [self._case(0, 0, 1.0, 0.0, 0.0),
                  self._case(0, 1, 1.0, 5.0, 0.0, has_matrix=False)]

        summary = alignment_quality.summarize_consistency(results)

        self.assertIsNone(summary)

    def test_empty_results_returns_none(self):
        self.assertIsNone(alignment_quality.summarize_consistency([]))


if __name__ == "__main__":
    unittest.main()
