import os
import sys
import unittest

import numpy as np


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import pitch_diagnostics


def _tile(order, row, column, expected_x, expected_y, width=100, height=80):
    return {"OrderIndex": order, "Row": row, "Column": column,
            "ExpectedX": expected_x, "ExpectedY": expected_y,
            "Width": width, "Height": height}


class OverlapRoiTests(unittest.TestCase):
    def test_right_direction_takes_near_edge_strips_matching_overlap_width(self):
        anchor = _tile(0, 0, 0, 0, 0)
        target = _tile(1, 0, 1, 90, 0)
        anchor_image = np.arange(80 * 100, dtype=np.uint8).reshape(80, 100)
        target_image = np.arange(80 * 100, dtype=np.uint8).reshape(80, 100) + 1

        anchor_roi = pitch_diagnostics.crop_overlap_roi(
            anchor_image, anchor, target, "right")
        target_roi = pitch_diagnostics.crop_overlap_roi(
            target_image, anchor, target, "right")

        # overlap width = min(0+100, 90+100) - max(0, 90) = 100 - 90 = 10
        self.assertEqual(anchor_roi.shape, (80, 10))
        self.assertEqual(target_roi.shape, (80, 10))
        # anchor crop is the rightmost 10 columns, target crop is the leftmost 10 columns
        np.testing.assert_array_equal(anchor_roi, anchor_image[:, 90:100])
        np.testing.assert_array_equal(target_roi, target_image[:, 0:10])

    def test_bottom_direction_takes_near_edge_strips_matching_overlap_height(self):
        anchor = _tile(0, 0, 0, 0, 0)
        target = _tile(2, 1, 0, 0, 65)
        anchor_image = np.arange(80 * 100, dtype=np.uint8).reshape(80, 100)
        target_image = np.arange(80 * 100, dtype=np.uint8).reshape(80, 100) + 1

        anchor_roi = pitch_diagnostics.crop_overlap_roi(
            anchor_image, anchor, target, "bottom")
        target_roi = pitch_diagnostics.crop_overlap_roi(
            target_image, anchor, target, "bottom")

        # overlap height = min(0+80, 65+80) - max(0, 65) = 80 - 65 = 15
        self.assertEqual(anchor_roi.shape, (15, 100))
        self.assertEqual(target_roi.shape, (15, 100))
        np.testing.assert_array_equal(anchor_roi, anchor_image[65:80, :])
        np.testing.assert_array_equal(target_roi, target_image[0:15, :])

    def test_degenerate_overlap_raises(self):
        anchor = _tile(0, 0, 0, 0, 0)
        target = _tile(1, 0, 5, 500, 0)  # far apart, no real overlap
        image = np.zeros((80, 100), dtype=np.uint8)
        with self.assertRaises(ValueError):
            pitch_diagnostics.crop_overlap_roi(image, anchor, target, "right")


if __name__ == "__main__":
    unittest.main()
