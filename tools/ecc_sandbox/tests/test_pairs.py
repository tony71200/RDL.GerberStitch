import os
import sys
import tempfile
import unittest

import numpy as np
from PIL import Image


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import pairs


def _payload_and_files(temp_dir, row, column, expected_x, expected_y, width=64, height=64):
    tile = {"OrderIndex": 0, "Row": row, "Column": column,
            "ExpectedX": expected_x, "ExpectedY": expected_y,
            "Width": width, "Height": height}
    payload = {"tiles": [tile], "image_width": width, "image_height": height,
              "sample_path": None, "overlap_x": None, "overlap_y": None}

    raster_path = os.path.join(temp_dir, "raster.png")
    raster_image = (np.arange(400 * 400) % 256).astype(np.uint8).reshape(400, 400)
    Image.fromarray(raster_image).save(raster_path)
    raster = pairs.RasterSource(raster_path)

    captured = np.zeros((height, width), dtype=np.uint8)
    Image.fromarray(captured).save(os.path.join(temp_dir, "0.bmp"))

    return payload, raster


class DirectPairPitchCorrectionTests(unittest.TestCase):
    def test_default_offset_reproduces_current_origin(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            payload, raster = _payload_and_files(temp_dir, row=2, column=3,
                                                  expected_x=192, expected_y=128)
            _reference, _moving, meta = pairs.direct_pair(
                payload, raster, temp_dir, order_index=0)
            self.assertEqual(meta["reference_origin"], (192, 128))

    def test_pitch_correction_shifts_origin_by_column_and_row(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            payload, raster = _payload_and_files(temp_dir, row=2, column=3,
                                                  expected_x=192, expected_y=128)
            _reference, _moving, meta = pairs.direct_pair(
                payload, raster, temp_dir, order_index=0,
                pitch_correction_px_per_step_x=5.0, pitch_correction_px_per_step_y=-2.0)
            # column=3 * 5.0 = +15, row=2 * -2.0 = -4
            self.assertEqual(meta["reference_origin"], (192 + 15, 128 - 4))

    def test_pitch_correction_also_applies_with_step_override(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            payload, raster = _payload_and_files(temp_dir, row=1, column=2,
                                                  expected_x=999, expected_y=999)
            _reference, _moving, meta = pairs.direct_pair(
                payload, raster, temp_dir, order_index=0, step_override=64.0,
                pitch_correction_px_per_step_x=3.0, pitch_correction_px_per_step_y=1.0)
            # step_override branch: x = column * step = 2*64 = 128, then +column*3.0 = +6
            # y = row * step = 1*64 = 64, then +row*1.0 = +1
            self.assertEqual(meta["reference_origin"], (128 + 6, 64 + 1))


if __name__ == "__main__":
    unittest.main()
