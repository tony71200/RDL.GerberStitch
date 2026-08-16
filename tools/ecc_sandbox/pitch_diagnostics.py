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
