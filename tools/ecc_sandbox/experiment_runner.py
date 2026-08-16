"""Non-GUI runner for the fixed 14-case direct-alignment regression dataset.

Runs Direct mode for the seven requested (row, column) coordinates against both
mutually exclusive preprocessing modes with Contrast=150 (all other values from
config.DEFAULTS), and saves machine-readable plus visual evidence into a result
directory. Does not modify any C# production code -- experimental sandbox only.
"""
import argparse
import csv
import json
import os

import cv2
import numpy as np

import config
import pairs
import preprocess
import pyramid_ecc as ecc

REQUESTED_COORDINATES = [(1, 1), (3, 0), (4, 0), (3, 1), (1, 3), (4, 2), (4, 3)]
PREPROCESS_MODES = ("FlattenAndEnhance", "ToBinaryTraces")
_EXPERIMENT_CONTRAST = 150.0

_PREVIEW = 512

_JSON_NAME = "experiment_results.json"
_CSV_NAME = "requested_cases_summary.csv"

_CSV_FIELDS = [
    "row", "column", "preprocess_mode", "image_extension",
    "success", "verification_status", "failure_reason", "message",
    "translation_x", "translation_y", "rotation_deg", "scale", "raw_score",
    "symmetric_edge_coverage", "symmetric_chamfer_p95", "coverage_margin",
]


def _thumb(image):
    scale = _PREVIEW / float(max(image.shape[0], image.shape[1]))
    if scale >= 1.0:
        return image
    return cv2.resize(image, (int(image.shape[1] * scale), int(image.shape[0] * scale)),
                      interpolation=cv2.INTER_AREA)


def _json_safe(value):
    if isinstance(value, np.ndarray):
        return value.tolist()
    if isinstance(value, (np.floating,)):
        return float(value)
    if isinstance(value, (np.integer,)):
        return int(value)
    if isinstance(value, dict):
        return {key: _json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_safe(item) for item in value]
    return value


def _case_id(row, column, mode):
    return "row%d_col%d_%s" % (row, column, mode)


def _row_summary(row, column, mode, extension, result):
    summary = {
        "row": row,
        "column": column,
        "preprocess_mode": mode,
        "image_extension": extension,
    }
    for field in _CSV_FIELDS[4:]:
        summary[field] = result.get(field)
    return summary


def _save_overlay(output_dir, case_id, reference, moving, matrix):
    before = cv2.merge([_thumb(reference), _thumb(moving), _thumb(reference)])
    cv2.imwrite(os.path.join(output_dir, case_id + "_before.jpg"), before)

    if matrix is not None:
        h, w = reference.shape
        warped = ecc.warp_moving_to_reference(moving, matrix, (w, h))
    else:
        warped = moving
    after = cv2.merge([_thumb(reference), _thumb(warped), _thumb(reference)])
    cv2.imwrite(os.path.join(output_dir, case_id + "_after.jpg"), after)


def run_experiment(payload_path, images_dir, raster_path, extension, output_dir,
                   all_tiles=False):
    """Run each (coordinate x preprocessing mode) case and save evidence.

    Without all_tiles: the fixed 7 REQUESTED_COORDINATES (14 rows total) -- unchanged behavior.
    With all_tiles=True: every tile in the payload x both modes -- for judging whether chamfer
    bootstrap / pitch-corrected seeding actually help across the whole grid, not just 7 samples.
    Returns the list of per-case summary dicts (also written to the CSV).
    """
    extension = extension or ".bmp"
    os.makedirs(output_dir, exist_ok=True)

    payload = pairs.load_payload(payload_path)
    raster = pairs.RasterSource(raster_path)

    base_cfg = dict(config.DEFAULTS)
    base_cfg["Contrast"] = _EXPERIMENT_CONTRAST

    if all_tiles:
        coordinates = [(tile["Row"], tile["Column"]) for tile in payload["tiles"]]
    else:
        coordinates = REQUESTED_COORDINATES

    rows = []
    json_results = []
    for row, column in coordinates:
        order = pairs.index_of(payload, row, column)
        if order is None:
            raise ValueError("Khong co tile o (row=%d, col=%d)." % (row, column))
        reference_raw, moving_raw, _meta = pairs.direct_pair(
            payload, raster, images_dir, order, None, extension,
            pitch_correction_px_per_step_x=base_cfg["PitchCorrectionPxPerStepX"],
            pitch_correction_px_per_step_y=base_cfg["PitchCorrectionPxPerStepY"])

        for mode in PREPROCESS_MODES:
            cfg = dict(base_cfg)
            ref_variants = preprocess.build_variants(reference_raw, cfg, mode)
            mov_variants = preprocess.build_variants(moving_raw, cfg, mode)

            result = ecc.match(
                ref_variants["final"], mov_variants["final"], cfg,
                verification_reference=ref_variants["contrast"],
                verification_moving=mov_variants["contrast"])

            case_id = _case_id(row, column, mode)
            row_summary = _row_summary(row, column, mode, extension, result)
            rows.append(row_summary)
            json_results.append(_json_safe(dict(row_summary, matrix=result.get("matrix"),
                                                 attempts=result.get("attempts"))))
            _save_overlay(output_dir, case_id, reference_raw, moving_raw,
                         result.get("matrix"))

    with open(os.path.join(output_dir, _JSON_NAME), "w", encoding="utf-8") as stream:
        json.dump({
            "config": _json_safe(base_cfg),
            "requested_coordinates": REQUESTED_COORDINATES,
            "preprocess_modes": list(PREPROCESS_MODES),
            "results": json_results,
        }, stream, indent=2)

    with open(os.path.join(output_dir, _CSV_NAME), "w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=_CSV_FIELDS)
        writer.writeheader()
        for row_summary in rows:
            writer.writerow(row_summary)

    return rows


def _parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", required=True, help="Duong dan payload JSON.")
    parser.add_argument("--images", required=True, help="Thu muc anh chup.")
    parser.add_argument("--raster", required=True, help="Duong dan raster Gerber.")
    parser.add_argument("--ext", default="", help="Duoi file anh chup (mac dinh .bmp).")
    parser.add_argument("--output", required=True, help="Thu muc result_test de ghi ket qua.")
    parser.add_argument("--all-tiles", action="store_true",
                        help="Chay toan bo tile trong payload thay vi 7 toa do mac dinh.")
    return parser.parse_args()


def main():
    args = _parse_args()
    rows = run_experiment(args.payload, args.images, args.raster, args.ext, args.output,
                          all_tiles=args.all_tiles)
    verified = sum(1 for row in rows if row.get("verification_status") == "Verified")
    uncertain = sum(1 for row in rows if row.get("verification_status") == "Uncertain")
    rejected = sum(1 for row in rows if row.get("verification_status") == "Rejected")
    print("Da chay %d case: Verified=%d Uncertain=%d Rejected=%d"
          % (len(rows), verified, uncertain, rejected))
    print("Ket qua: %s" % os.path.join(args.output, _JSON_NAME))
    print("Tom tat : %s" % os.path.join(args.output, _CSV_NAME))


if __name__ == "__main__":
    main()
