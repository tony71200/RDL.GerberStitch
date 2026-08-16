import csv
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

import cv2
import numpy as np
from PIL import Image


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import experiment_runner


class ExperimentRunnerTests(unittest.TestCase):
    def test_requested_dataset_writes_fourteen_rows_and_defaults_empty_extension(self):
        coordinates = [(1, 1), (3, 0), (4, 0), (3, 1),
                       (1, 3), (4, 2), (4, 3)]
        self.assertEqual(experiment_runner.REQUESTED_COORDINATES, coordinates)

        with tempfile.TemporaryDirectory() as temp_dir:
            images_dir = os.path.join(temp_dir, "images")
            output_dir = os.path.join(temp_dir, "result_test")
            os.makedirs(images_dir)
            raster_path = os.path.join(temp_dir, "gerber.tiff")
            payload_path = os.path.join(temp_dir, "sample.json")

            base = np.zeros((32, 32), dtype=np.uint8)
            cv2.circle(base, (10, 9), 5, 255, 2)
            cv2.line(base, (7, 24), (27, 19), 220, 2)
            Image.fromarray(base).save(raster_path)

            tiles = []
            for order, (row, column) in enumerate(coordinates):
                tiles.append({
                    "OrderIndex": order,
                    "Row": row,
                    "Column": column,
                    "ExpectedX": 0,
                    "ExpectedY": 0,
                    "Width": 32,
                    "Height": 32,
                })
                self.assertTrue(cv2.imwrite(
                    os.path.join(images_dir, "%d.bmp" % order), base))
            with open(payload_path, "w", encoding="utf-8") as stream:
                json.dump({
                    "Width_CaptureImages": 32,
                    "Height_CaptureImages": 32,
                    "GerberTiles": tiles,
                }, stream)

            match_result = {
                "success": True,
                "verification_status": "Verified",
                "failure_reason": None,
                "message": "verified",
                "matrix": np.eye(3),
                "translation_x": 0.0,
                "translation_y": 0.0,
                "rotation_deg": 0.0,
                "scale": 1.0,
                "raw_score": 0.9,
                "reference_edge_coverage": 1.0,
                "moving_edge_coverage": 1.0,
                "symmetric_edge_coverage": 1.0,
                "symmetric_chamfer_p95": 0.0,
                "coverage_margin": None,
                "attempts": [],
            }
            with mock.patch.object(experiment_runner.ecc, "match",
                                   return_value=match_result) as match_call:
                rows = experiment_runner.run_experiment(
                    payload_path, images_dir, raster_path, "", output_dir)

            self.assertEqual(len(rows), 14)
            self.assertEqual(match_call.call_count, 14)
            self.assertEqual(
                {(row["row"], row["column"], row["preprocess_mode"])
                 for row in rows},
                {(row, column, mode)
                 for row, column in coordinates
                 for mode in ("FlattenAndEnhance", "ToBinaryTraces")})

            json_path = os.path.join(output_dir, "experiment_results.json")
            csv_path = os.path.join(output_dir, "requested_cases_summary.csv")
            with open(json_path, "r", encoding="utf-8") as stream:
                saved_json = json.load(stream)
            with open(csv_path, "r", encoding="utf-8", newline="") as stream:
                saved_csv = list(csv.DictReader(stream))
            self.assertEqual(len(saved_json["results"]), 14)
            self.assertEqual(len(saved_csv), 14)
            self.assertTrue(all(row["image_extension"] == ".bmp" for row in rows))
            self.assertEqual(len([
                name for name in os.listdir(output_dir)
                if name.endswith("_before.jpg")]), 14)
            self.assertEqual(len([
                name for name in os.listdir(output_dir)
                if name.endswith("_after.jpg")]), 14)

    def test_all_tiles_iterates_every_tile_instead_of_requested_coordinates(self):
        rows, cols = 3, 3
        coordinates = [(r, c) for r in range(rows) for c in range(cols)]

        with tempfile.TemporaryDirectory() as temp_dir:
            images_dir = os.path.join(temp_dir, "images")
            output_dir = os.path.join(temp_dir, "result_test")
            os.makedirs(images_dir)
            raster_path = os.path.join(temp_dir, "gerber.tiff")
            payload_path = os.path.join(temp_dir, "sample.json")

            base = np.zeros((32, 32), dtype=np.uint8)
            cv2.circle(base, (10, 9), 5, 255, 2)
            Image.fromarray(base).save(raster_path)

            tiles = []
            for order, (row, column) in enumerate(coordinates):
                tiles.append({
                    "OrderIndex": order, "Row": row, "Column": column,
                    "ExpectedX": 0, "ExpectedY": 0, "Width": 32, "Height": 32,
                })
                self.assertTrue(cv2.imwrite(
                    os.path.join(images_dir, "%d.bmp" % order), base))
            with open(payload_path, "w", encoding="utf-8") as stream:
                json.dump({"Width_CaptureImages": 32, "Height_CaptureImages": 32,
                          "GerberTiles": tiles}, stream)

            match_result = {
                "success": True, "verification_status": "Verified", "failure_reason": None,
                "message": "verified", "matrix": np.eye(3), "translation_x": 0.0,
                "translation_y": 0.0, "rotation_deg": 0.0, "scale": 1.0, "raw_score": 0.9,
                "reference_edge_coverage": 1.0, "moving_edge_coverage": 1.0,
                "symmetric_edge_coverage": 1.0, "symmetric_chamfer_p95": 0.0,
                "coverage_margin": None, "attempts": [],
            }
            with mock.patch.object(experiment_runner.ecc, "match",
                                   return_value=match_result) as match_call:
                rows_out = experiment_runner.run_experiment(
                    payload_path, images_dir, raster_path, "", output_dir, all_tiles=True)

            self.assertEqual(len(rows_out), rows * cols * 2)
            self.assertEqual(match_call.call_count, rows * cols * 2)
            self.assertEqual(
                {(row["row"], row["column"]) for row in rows_out},
                set(coordinates))


if __name__ == "__main__":
    unittest.main()
