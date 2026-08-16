import os
import sys
import unittest

import cv2
import numpy as np


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import chamfer_alignment
import config


def _cfg(**overrides):
    values = dict(config.DEFAULTS)
    values.update({
        "ChamferAngleStepDeg": 0.05,
        "ChamferCandidateCount": 3,
        "ChamferSeparationPixels": 10.0,
        "MaxAbsRotationDeg": 1.0,
        "MaxTranslationPixels": 40.0,
        "CoarseSearchDownsample": 2,
    })
    values.update(overrides)
    return values


def _traces_image(width=160, height=140):
    image = np.zeros((height, width), dtype=np.uint8)
    cv2.line(image, (20, 30), (140, 34), 255, 3)
    cv2.line(image, (25, 70), (135, 66), 255, 3)
    cv2.rectangle(image, (60, 90), (100, 118), 255, 3)
    cv2.circle(image, (30, 110), 9, 255, 3)
    return image


class FindChamferCandidatesTests(unittest.TestCase):
    def test_recovers_known_translation_with_small_rotation(self):
        reference = _traces_image()
        angle, tx, ty = 0.3, 7.0, -5.0
        h, w = reference.shape
        center = (w / 2.0, h / 2.0)
        rot = cv2.getRotationMatrix2D(center, -angle, 1.0)
        rot[0, 2] += tx
        rot[1, 2] += ty
        moving = cv2.warpAffine(reference, rot, (w, h), flags=cv2.INTER_LINEAR)

        # find_chamfer_candidates returns a MovingImage -> ReferenceImage matrix -- the
        # inverse of the transform used above to build `moving` from `reference`.
        expected_matrix = np.linalg.inv(np.vstack([rot, [0.0, 0.0, 1.0]]))

        candidates = chamfer_alignment.find_chamfer_candidates(reference, moving, _cfg())

        self.assertGreater(len(candidates), 0)
        best = candidates[0]
        self.assertEqual(best["source"], "chamfer_bootstrap")
        self.assertEqual(best["matrix"].shape, (3, 3))
        # Chamfer only has to produce a coarse SEED (refined later by ECC in
        # pyramid_ecc.match()), not sub-pixel precision -- a few px off is expected given
        # CoarseSearchDownsample=2 quantizing the coarse translation search.
        self.assertAlmostEqual(best["matrix"][0, 2], expected_matrix[0, 2], delta=5.0)
        self.assertAlmostEqual(best["matrix"][1, 2], expected_matrix[1, 2], delta=5.0)

    def test_candidates_respect_separation_and_count_bound(self):
        reference = _traces_image()
        moving = _traces_image()

        candidates = chamfer_alignment.find_chamfer_candidates(reference, moving, _cfg())

        self.assertLessEqual(len(candidates), 3)
        translations = [(c["matrix"][0, 2], c["matrix"][1, 2]) for c in candidates]
        for i in range(len(translations)):
            for j in range(i + 1, len(translations)):
                dist = np.hypot(translations[i][0] - translations[j][0],
                                translations[i][1] - translations[j][1])
                self.assertGreaterEqual(dist, 10.0 - 1e-6)

    def test_blank_images_return_no_candidates(self):
        blank = np.zeros((80, 80), dtype=np.uint8)

        candidates = chamfer_alignment.find_chamfer_candidates(blank, blank, _cfg())

        self.assertEqual(candidates, [])

    def test_zero_candidate_count_returns_no_candidates(self):
        reference = _traces_image()
        angle, tx, ty = 0.3, 7.0, -5.0
        h, w = reference.shape
        center = (w / 2.0, h / 2.0)
        rot = cv2.getRotationMatrix2D(center, -angle, 1.0)
        rot[0, 2] += tx
        rot[1, 2] += ty
        moving = cv2.warpAffine(reference, rot, (w, h), flags=cv2.INTER_LINEAR)

        candidates = chamfer_alignment.find_chamfer_candidates(
            reference, moving, _cfg(ChamferCandidateCount=0))

        self.assertEqual(candidates, [])

    def test_mismatched_shapes_raise(self):
        reference = _traces_image()
        moving = _traces_image()[:, :100]

        with self.assertRaises(ValueError):
            chamfer_alignment.find_chamfer_candidates(reference, moving, _cfg())


if __name__ == "__main__":
    unittest.main()
