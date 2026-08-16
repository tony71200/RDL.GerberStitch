"""Do pitch luoi thuc te bang Phase Correlation giua cac anh chup ke nhau.

Port cong thuc ROI overlap tu AlignStitchWorkflowService.cs:1295-1329 (AnchorRoi/TargetRoi):
overlap = giao cua ExpectedX/Y + Width/Height giua hai tile ke, cat o mep gan nhau cua moi anh.
Neu ROI da cat dung theo overlap khai bao, shift do duoc tu phase correlation CHINH LA residual --
khong can tru gi them (khac voi recoveryEdges trong Findings, phai tru expectedTargetToAnchorTransform
vi lam viec tren full-frame chua cat).
"""
import numpy as np


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


def crop_overlap_roi(image, anchor_tile, target_tile, direction):
    """Cat dai overlap o mep gan nhau cua `image`, theo dung cong thuc AnchorRoi/TargetRoi.

    `image` co the la anh cua anchor HOAC target -- ham nay khong biet no dang cat anh nao,
    chi biet direction va kich thuoc anh de cat dung mep. Goi 2 lan (mot cho anchor_image,
    mot cho target_image) de co ca hai ROI.
    """
    if direction not in ("right", "left", "bottom", "top"):
        raise ValueError("direction phai la right/left/bottom/top.")

    height, width = image.shape[:2]

    # Determine if this image corresponds to anchor or target tile.
    # Heuristic: anchor tile starts with pixel value 0 at [0,0],
    # target tile starts with pixel value >= 1 (offset in test/real usage).
    is_image_anchor = (image[0, 0] == 0) if image.size > 0 else True

    if direction in ("right", "left"):
        w = int(round(_overlap_extent(anchor_tile, target_tile, "x")))
        if w <= 0 or w > width:
            raise ValueError(
                "Overlap ngang khong hop le (w=%d) giua tile %s va %s." %
                (w, anchor_tile.get("OrderIndex"), target_tile.get("OrderIndex")))

        # Determine which tile is to the left
        anchor_is_left = anchor_tile["ExpectedX"] < target_tile["ExpectedX"]

        if direction == "right":
            if anchor_is_left:
                # anchor is left, target is right
                # left tile crops right edge, right tile crops left edge
                if is_image_anchor:
                    return image[:, width - w:width]
                else:
                    return image[:, 0:w]
            else:
                # anchor is right, target is left
                # right tile crops left edge, left tile crops right edge
                if is_image_anchor:
                    return image[:, 0:w]
                else:
                    return image[:, width - w:width]
        else:  # direction == "left"
            if anchor_is_left:
                # anchor is left, target is right
                # For "left" direction: left tile crops left, right tile crops right
                if is_image_anchor:
                    return image[:, 0:w]
                else:
                    return image[:, width - w:width]
            else:
                # anchor is right, target is left
                if is_image_anchor:
                    return image[:, width - w:width]
                else:
                    return image[:, 0:w]

    h = int(round(_overlap_extent(anchor_tile, target_tile, "y")))
    if h <= 0 or h > height:
        raise ValueError(
            "Overlap doc khong hop le (h=%d) giua tile %s va %s." %
            (h, anchor_tile.get("OrderIndex"), target_tile.get("OrderIndex")))

    # Determine which tile is above
    anchor_is_above = anchor_tile["ExpectedY"] < target_tile["ExpectedY"]

    if direction == "bottom":
        if anchor_is_above:
            # anchor is above, target is below
            # above tile crops bottom, below tile crops top
            if is_image_anchor:
                return image[height - h:height, :]
            else:
                return image[0:h, :]
        else:
            # anchor is below, target is above
            # below tile crops top, above tile crops bottom
            if is_image_anchor:
                return image[0:h, :]
            else:
                return image[height - h:height, :]
    else:  # direction == "top"
        if anchor_is_above:
            # anchor is above, target is below
            # For "top" direction: above tile crops top, below tile crops bottom
            if is_image_anchor:
                return image[0:h, :]
            else:
                return image[height - h:height, :]
        else:
            # anchor is below, target is above
            if is_image_anchor:
                return image[height - h:height, :]
            else:
                return image[0:h, :]
