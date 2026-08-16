import os
import sys
import unittest
from unittest import mock

import cv2
import numpy as np


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import config
import preprocess


class BuildVariantsTests(unittest.TestCase):
    def setUp(self):
        self.image = np.arange(32 * 32, dtype=np.uint8).reshape(32, 32)

    def test_flatten_mode_uses_only_flattened_result(self):
        flattened = np.full_like(self.image, 17)
        with mock.patch.object(preprocess, "flatten_and_enhance",
                               return_value=flattened) as flatten_call:
            with mock.patch.object(preprocess, "to_binary_traces",
                                   return_value=np.full_like(self.image, 29)) as binary_call:
                variants = preprocess.build_variants(
                    self.image, config.DEFAULTS, "FlattenAndEnhance")

        self.assertTrue(np.array_equal(variants["final"], flattened))
        self.assertIn("flattened", variants)
        self.assertNotIn("binary", variants)
        self.assertEqual(flatten_call.call_count, 1)
        self.assertEqual(binary_call.call_count, 0)

    def test_binary_mode_uses_weighted_binary_without_flattening(self):
        binary = np.full_like(self.image, 31)
        with mock.patch.object(preprocess, "flatten_and_enhance",
                               return_value=np.full_like(self.image, 19)) as flatten_call:
            with mock.patch.object(preprocess, "to_binary_traces",
                                   return_value=binary) as binary_call:
                variants = preprocess.build_variants(
                    self.image, config.DEFAULTS, "ToBinaryTraces")

        self.assertTrue(np.array_equal(variants["final"], binary))
        self.assertIn("binary", variants)
        self.assertNotIn("flattened", variants)
        self.assertEqual(flatten_call.call_count, 0)
        self.assertEqual(binary_call.call_count, 1)

    def test_unknown_mode_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Preprocess mode"):
            preprocess.build_variants(self.image, config.DEFAULTS, "Both")


class BinaryBlendTests(unittest.TestCase):
    def test_binary_traces_returns_exact_otsu_close_weighted_blend(self):
        image = np.zeros((32, 32), dtype=np.uint8)
        image[:, 16:] = 220
        image[8:24, 8:24] = 120
        cfg = config.DEFAULTS

        binary = cv2.threshold(image, 128, 255,
                               cv2.THRESH_BINARY + cv2.THRESH_OTSU)[1]
        kernel_size = cfg["CloseKernel"]
        kernel = cv2.getStructuringElement(
            cv2.MORPH_ELLIPSE, (kernel_size, kernel_size))
        morph = cv2.morphologyEx(
            binary, cv2.MORPH_CLOSE, kernel,
            iterations=cfg["CloseIterations"])
        expected = cv2.addWeighted(image, 0.7, morph, 0.3, 0.0).astype(np.uint8)

        actual = preprocess.to_binary_traces(
            image, cfg["AdaptiveBlockSize"], cfg["AdaptiveC"],
            cfg["CloseKernel"], cfg["CloseIterations"])

        self.assertTrue(np.array_equal(actual, expected))


if __name__ == "__main__":
    unittest.main()
