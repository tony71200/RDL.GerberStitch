"""Sinh danh sach cap de kiem tra.

Direct   : tile Gerber (crop tu raster) <-> anh chup cung orderIndex
Neighbor : anh chup <-> anh chup ke ben
"""
import json
import os
import cv2
import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None


def load_payload(path):
    with open(path, "r", encoding="utf-8-sig") as fh:
        data = json.load(fh)
    tiles = sorted(data["GerberTiles"], key=lambda t: t["OrderIndex"])
    return {
        "tiles": tiles,
        "sample_path": data.get("GerberSampleImagePath"),
        "image_width": data.get("Width_CaptureImages", 4096),
        "image_height": data.get("Height_CaptureImages", 4096),
        "overlap_x": data.get("OVerLapX_EdgePath"),
        "overlap_y": data.get("OVerLapY_EdgePath"),
    }


class RasterSource(object):
    """Doc crop tu raster Gerber ma khong nap ca anh 40418x32364 vao RAM."""

    def __init__(self, path):
        self.image = Image.open(path)
        self.width, self.height = self.image.size

    def crop_mono8(self, x, y, w, h):
        x0 = max(0, min(self.width - 1, int(x)))
        y0 = max(0, min(self.height - 1, int(y)))
        x1 = min(self.width, x0 + int(w))
        y1 = min(self.height, y0 + int(h))
        patch = self.image.crop((x0, y0, x1, y1)).convert("L")
        arr = np.asarray(patch, dtype=np.uint8)
        if arr.shape[0] != h or arr.shape[1] != w:
            padded = np.full((int(h), int(w)), 255, dtype=np.uint8)
            padded[:arr.shape[0], :arr.shape[1]] = arr
            return padded
        return arr


def captured_path(folder, order_index, extension=".bmp"):
    return os.path.join(folder, str(order_index) + extension)


def load_captured(folder, order_index, extension=".bmp"):
    path = captured_path(folder, order_index, extension)
    img = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
    if img is None:
        raise IOError("Khong doc duoc " + path)
    return img


def direct_pair(payload, raster, images_folder, order_index, step_override=None, extension=".bmp",
                pitch_correction_px_per_step_x=0.0, pitch_correction_px_per_step_y=0.0):
    """Reference = crop raster tai vet chan danh nghia cua anh chup, CUNG kich thuoc anh chup.

    step_override cho phep so sanh buoc luoi khac nhau (vd 4096 vs 4031.5) ma khong phai
    sinh lai payload -- dung de nhin tan mat anh huong cua sai buoc len ECC.

    pitch_correction_px_per_step_x/y (mac dinh 0.0 = khong doi hanh vi) la THU NGHIEM sandbox:
    cong them column*x + row*y vao goc crop, dung so do duoc tu pitch_diagnostics.measure_pitch()
    de kiem chung gia thuyet sai luoi bang ket qua match that, KHONG phai sua Master.
    """
    tile = next(t for t in payload["tiles"] if t["OrderIndex"] == order_index)
    w, h = payload["image_width"], payload["image_height"]
    if step_override is None:
        x = tile["ExpectedX"] + (tile["Width"] - w) // 2
        y = tile["ExpectedY"] + (tile["Height"] - h) // 2
    else:
        x = int(tile["Column"] * step_override)
        y = int(tile["Row"] * step_override)
    x += tile["Column"] * pitch_correction_px_per_step_x
    y += tile["Row"] * pitch_correction_px_per_step_y
    x, y = int(round(x)), int(round(y))
    reference = raster.crop_mono8(x, y, w, h)
    moving = load_captured(images_folder, order_index, extension)
    return reference, moving, {"reference_origin": (x, y), "tile": tile}


def neighbor_pair(payload, images_folder, anchor_index, target_index, extension=".bmp"):
    return (load_captured(images_folder, anchor_index, extension),
            load_captured(images_folder, target_index, extension),
            {"anchor": anchor_index, "target": target_index})


def index_of(payload, row, column):
    for t in payload["tiles"]:
        if t["Row"] == row and t["Column"] == column:
            return t["OrderIndex"]
    return None
