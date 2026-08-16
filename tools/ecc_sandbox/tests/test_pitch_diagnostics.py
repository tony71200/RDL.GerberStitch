import json
import os
import sys
import tempfile
import unittest

import cv2
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
            anchor_image, anchor, target, "right", True)
        target_roi = pitch_diagnostics.crop_overlap_roi(
            target_image, anchor, target, "right", False)

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
            anchor_image, anchor, target, "bottom", True)
        target_roi = pitch_diagnostics.crop_overlap_roi(
            target_image, anchor, target, "bottom", False)

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
            pitch_diagnostics.crop_overlap_roi(image, anchor, target, "right", True)


def _grid_payload(rows, cols, width=120, height=100):
    tiles = []
    order = 0
    for row in range(rows):
        for column in range(cols):
            tiles.append({
                "OrderIndex": order, "Row": row, "Column": column,
                "ExpectedX": column * (width - 20), "ExpectedY": row * (height - 15),
                "Width": width, "Height": height,
            })
            order += 1
    return {"tiles": tiles, "image_width": width, "image_height": height,
            "sample_path": None, "overlap_x": None, "overlap_y": None}


def _textured_image(width, height, seed):
    rng = np.random.RandomState(seed)
    image = (rng.rand(height, width) * 40 + 30).astype(np.uint8)
    cv2.circle(image, (width // 3, height // 2), 8, 220, -1)
    cv2.rectangle(image, (width // 2, height // 4), (width // 2 + 15, height // 4 + 12), 200, -1)
    return image


class PhaseCorrelateShiftTests(unittest.TestCase):
    def _cfg(self):
        values = dict()
        values.update({"BackgroundSigma": 15.0, "ClaheClipLimit": 2.0, "ClaheTile": 8})
        return values

    def test_recovers_known_injected_shift(self):
        base = _textured_image(160, 140, seed=1)
        shift = (3.0, -2.0)
        matrix = np.array([[1.0, 0.0, shift[0]], [0.0, 1.0, shift[1]]], dtype=np.float32)
        shifted = cv2.warpAffine(base, matrix, (160, 140), flags=cv2.INTER_LINEAR,
                                 borderMode=cv2.BORDER_REPLICATE)

        dx, dy = pitch_diagnostics.phase_correlate_shift(base, shifted, self._cfg())

        self.assertAlmostEqual(dx, shift[0], delta=0.6)
        self.assertAlmostEqual(dy, shift[1], delta=0.6)


class NeighborEdgesForGridTests(unittest.TestCase):
    def test_enumerates_right_and_bottom_edges_exactly_once_each(self):
        payload = _grid_payload(rows=3, cols=4)

        edges = pitch_diagnostics.neighbor_edges_for_grid(payload)

        # 3 rows x 3 right-edges per row + 2 bottom-edges per column x 4 columns
        right_edges = [e for e in edges if e[2] == "right"]
        bottom_edges = [e for e in edges if e[2] == "bottom"]
        self.assertEqual(len(right_edges), 3 * 3)
        self.assertEqual(len(bottom_edges), 2 * 4)
        for anchor, target, direction in edges:
            if direction == "right":
                self.assertEqual(anchor["Row"], target["Row"])
                self.assertEqual(anchor["Column"] + 1, target["Column"])
            else:
                self.assertEqual(anchor["Column"], target["Column"])
                self.assertEqual(anchor["Row"] + 1, target["Row"])


class MeasurePitchTests(unittest.TestCase):
    def test_groups_by_direction_and_reports_constant_injected_residual(self):
        payload = _grid_payload(rows=2, cols=2, width=120, height=100)
        residual = (4.0, 0.0)

        with tempfile.TemporaryDirectory() as images_dir:
            base = _textured_image(120, 100, seed=7)
            matrix = np.array([[1.0, 0.0, residual[0]], [0.0, 1.0, residual[1]]], dtype=np.float32)
            shifted = cv2.warpAffine(base, matrix, (120, 100), flags=cv2.INTER_LINEAR,
                                     borderMode=cv2.BORDER_REPLICATE)
            # order 0=(0,0), 1=(0,1), 2=(1,0), 3=(1,1); every tile after the first is `shifted`
            # by the same constant residual relative to its left/top neighbor.
            for order, image in enumerate([base, shifted, shifted, base]):
                cv2.imwrite(os.path.join(images_dir, "%d.bmp" % order), image)

            cfg = dict()
            cfg.update({"BackgroundSigma": 15.0, "ClaheClipLimit": 2.0, "ClaheTile": 8})
            result = pitch_diagnostics.measure_pitch(payload, images_dir, ".bmp", cfg)

        self.assertIn("right", result)
        self.assertIn("bottom", result)
        self.assertEqual(result["right"]["n"], 2)
        self.assertAlmostEqual(result["right"]["mean_dx"], residual[0], delta=0.6)


if __name__ == "__main__":
    unittest.main()
