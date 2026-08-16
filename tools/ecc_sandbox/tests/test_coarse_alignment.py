import os
import sys
import unittest

import cv2
import numpy as np


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import coarse_alignment
import config


def _cfg(**overrides):
    values = dict(config.DEFAULTS)
    values.update({
        "MaxTranslationPixels": 40.0,
        "CoarseSearchDownsample": 2,
        "CoarseDistanceCapPixels": 20.0,
        "CoarseCandidateCount": 4,
        "CoarseCandidateSeparationPixels": 16.0,
    })
    values.update(overrides)
    return values


def _asymmetric_reference():
    image = np.zeros((160, 192), dtype=np.uint8)
    cv2.circle(image, (52, 47), 13, 230, -1)
    cv2.rectangle(image, (104, 75), (151, 88), 180, -1)
    cv2.line(image, (78, 124), (166, 113), 255, 5)
    return image


class TranslationSeedTests(unittest.TestCase):
    def test_best_seed_maps_moving_back_to_reference(self):
        reference = _asymmetric_reference()
        # Content in moving is 24 px left and 16 px down. Therefore the
        # required MovingImage -> ReferenceImage translation is (+24, -16).
        moving = cv2.warpAffine(
            reference,
            np.array([[1.0, 0.0, -24.0], [0.0, 1.0, 16.0]], dtype=np.float32),
            (reference.shape[1], reference.shape[0]))

        seeds = coarse_alignment.find_translation_seeds(reference, moving, _cfg())

        self.assertGreater(len(seeds), 0)
        best = seeds[0]["matrix"]
        self.assertAlmostEqual(float(best[0, 2]), 24.0, delta=4.0)
        self.assertAlmostEqual(float(best[1, 2]), -16.0, delta=4.0)
        self.assertEqual(seeds[0]["source"], "structural_bootstrap")

    def test_all_seeds_respect_translation_bound(self):
        reference = _asymmetric_reference()
        moving = np.roll(reference, 7, axis=1)
        cfg = _cfg(MaxTranslationPixels=24.0)

        seeds = coarse_alignment.find_translation_seeds(reference, moving, cfg)

        self.assertGreater(len(seeds), 0)
        for seed in seeds:
            self.assertLessEqual(abs(float(seed["matrix"][0, 2])), 24.0)
            self.assertLessEqual(abs(float(seed["matrix"][1, 2])), 24.0)

    def test_retained_peaks_obey_non_maximum_separation(self):
        reference = np.zeros((160, 192), dtype=np.uint8)
        for x in (40, 88, 136):
            cv2.rectangle(reference, (x, 55), (x + 14, 92), 255, -1)
        moving = np.roll(reference, 10, axis=1)
        cfg = _cfg(CoarseCandidateSeparationPixels=20.0,
                   CoarseCandidateCount=5)

        seeds = coarse_alignment.find_translation_seeds(reference, moving, cfg)

        translations = [np.asarray(seed["matrix"], dtype=float)[:2, 2]
                        for seed in seeds]
        for i, first in enumerate(translations):
            for second in translations[i + 1:]:
                self.assertGreaterEqual(
                    float(np.linalg.norm(first - second)), 20.0)


if __name__ == "__main__":
    unittest.main()
