"""Do pitch luoi thuc te bang Phase Correlation giua cac anh chup ke nhau.

Port cong thuc ROI overlap tu AlignStitchWorkflowService.cs:1295-1329 (AnchorRoi/TargetRoi):
overlap = giao cua ExpectedX/Y + Width/Height giua hai tile ke, cat o mep gan nhau cua moi anh.
Neu ROI da cat dung theo overlap khai bao, shift do duoc tu phase correlation CHINH LA residual --
khong can tru gi them (khac voi recoveryEdges trong Findings, phai tru expectedTargetToAnchorTransform
vi lam viec tren full-frame chua cat).
"""
import argparse
import json
import math
import os
import statistics as st

import cv2
import numpy as np

import pairs
import preprocess


def _overlap_extent(anchor_tile, target_tile, axis):
    """axis: 'x' hoac 'y'. Tra ve (w_hoac_h) phan giao giua hai tile theo truc do."""
    expected_key = "ExpectedX" if axis == "x" else "ExpectedY"
    size_key = "Width" if axis == "x" else "Height"
    anchor_start = anchor_tile[expected_key]
    target_start = target_tile[expected_key]
    anchor_end = anchor_start + anchor_tile[size_key]
    target_end = target_start + target_tile[size_key]
    extent = min(anchor_end, target_end) - max(anchor_start, target_start)
    return extent


def crop_overlap_roi(image, anchor_tile, target_tile, direction, is_anchor_image):
    """Cat dai overlap o mep gan nhau cua `image`, theo dung cong thuc AnchorRoi/TargetRoi.

    Cung mot `direction` cho ra crop KHAC NHAU tuy `image` thuoc anchor hay target -- vi du
    "right": anchor (ben trai) crop canh PHAI cua chinh no, target (ben phai) crop canh TRAI
    cua chinh no. Khong the tu doan duoc dieu nay tu noi dung pixel, nen `is_anchor_image` la
    tham so BAT BUOC, khong suy luan. Goi ham nay 2 lan (mot voi is_anchor_image=True cho anh
    anchor, mot voi is_anchor_image=False cho anh target) de co ca hai ROI.
    """
    if direction not in ("right", "left", "bottom", "top"):
        raise ValueError("direction phai la right/left/bottom/top.")

    height, width = image.shape[:2]
    if direction in ("right", "left"):
        w = int(round(_overlap_extent(anchor_tile, target_tile, "x")))
        if w <= 0 or w > width:
            raise ValueError(
                "Overlap ngang khong hop le (w=%d) giua tile %s va %s." %
                (w, anchor_tile.get("OrderIndex"), target_tile.get("OrderIndex")))
        anchor_crops_right_edge = (direction == "right")
        use_right_edge = (anchor_crops_right_edge if is_anchor_image
                          else not anchor_crops_right_edge)
        return image[:, width - w:width] if use_right_edge else image[:, 0:w]

    h = int(round(_overlap_extent(anchor_tile, target_tile, "y")))
    if h <= 0 or h > height:
        raise ValueError(
            "Overlap doc khong hop le (h=%d) giua tile %s va %s." %
            (h, anchor_tile.get("OrderIndex"), target_tile.get("OrderIndex")))
    anchor_crops_bottom_edge = (direction == "bottom")
    use_bottom_edge = (anchor_crops_bottom_edge if is_anchor_image
                       else not anchor_crops_bottom_edge)
    return image[height - h:height, :] if use_bottom_edge else image[0:h, :]


def phase_correlate_shift(anchor_roi, moving_roi, cfg):
    """Tra ve (dx, dy) -- shift can ap cho moving_roi de khop anchor_roi.

    Tien xu ly theo Findings Sec8.2: san phang illumination (flatten_and_enhance), KHONG
    threshold, nhan Hann window truoc khi dua vao phase correlation.
    """
    anchor = preprocess.flatten_and_enhance(
        anchor_roi, cfg["BackgroundSigma"], cfg["ClaheClipLimit"], cfg["ClaheTile"])
    moving = preprocess.flatten_and_enhance(
        moving_roi, cfg["BackgroundSigma"], cfg["ClaheClipLimit"], cfg["ClaheTile"])
    if anchor.shape != moving.shape:
        raise ValueError("phase_correlate_shift can hai ROI cung kich thuoc.")

    window = cv2.createHanningWindow((anchor.shape[1], anchor.shape[0]), cv2.CV_32F)
    anchor32 = anchor.astype(np.float32)
    moving32 = moving.astype(np.float32)
    (dx, dy), _response = cv2.phaseCorrelate(anchor32, moving32, window)
    return float(dx), float(dy)


def neighbor_edges_for_grid(payload):
    """Liet ke moi canh ke nhau (right, bottom) dung mot lan tren toan luoi."""
    by_position = {(t["Row"], t["Column"]): t for t in payload["tiles"]}
    edges = []
    for (row, column), tile in by_position.items():
        right = by_position.get((row, column + 1))
        if right is not None:
            edges.append((tile, right, "right"))
        bottom = by_position.get((row + 1, column))
        if bottom is not None:
            edges.append((tile, bottom, "bottom"))
    return edges


def measure_pitch(payload, images_dir, extension, cfg):
    """Chay phase_correlate_shift tren moi canh ke nhau, gom theo huong.

    Mot canh loi (thieu file anh, overlap suy bien) bi bo qua va khong lam dung cac canh con lai.
    """
    extension = extension or ".bmp"
    by_direction = {"right": {"dx": [], "dy": []}, "bottom": {"dx": [], "dy": []}}
    for anchor_tile, target_tile, direction in neighbor_edges_for_grid(payload):
        try:
            anchor_image = pairs.load_captured(
                images_dir, anchor_tile["OrderIndex"], extension)
            target_image = pairs.load_captured(
                images_dir, target_tile["OrderIndex"], extension)
            anchor_roi = crop_overlap_roi(anchor_image, anchor_tile, target_tile, direction, True)
            target_roi = crop_overlap_roi(target_image, anchor_tile, target_tile, direction, False)
            dx, dy = phase_correlate_shift(anchor_roi, target_roi, cfg)
        except (ValueError, IOError, cv2.error):
            continue
        by_direction[direction]["dx"].append(dx)
        by_direction[direction]["dy"].append(dy)

    result = {}
    for direction, values in by_direction.items():
        n = len(values["dx"])
        result[direction] = {
            "n": n,
            "mean_dx": st.mean(values["dx"]) if n else None,
            "mean_dy": st.mean(values["dy"]) if n else None,
            "std_dx": st.pstdev(values["dx"]) if n > 1 else 0.0,
            "std_dy": st.pstdev(values["dy"]) if n > 1 else 0.0,
        }
    return result


def _parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", required=True)
    parser.add_argument("--images", required=True)
    parser.add_argument("--ext", default="")
    parser.add_argument("--output", required=True)
    return parser.parse_args()


def main():
    args = _parse_args()
    payload = pairs.load_payload(args.payload)
    cfg = dict()
    cfg.update({"BackgroundSigma": 51.0, "ClaheClipLimit": 3.0, "ClaheTile": 16})
    result = measure_pitch(payload, args.images, args.ext or ".bmp", cfg)

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)

    for direction, stats in result.items():
        if stats["n"] == 0:
            print("%-8s n=0 (khong do duoc canh nao)" % direction)
            continue
        print("%-8s n=%3d mean_dx=%+8.3f std_dx=%.3f mean_dy=%+8.3f std_dy=%.3f"
              % (direction, stats["n"], stats["mean_dx"], stats["std_dx"],
                 stats["mean_dy"], stats["std_dy"]))
    print("Ket qua: %s" % args.output)


if __name__ == "__main__":
    main()
