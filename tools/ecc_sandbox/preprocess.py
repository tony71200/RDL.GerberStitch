"""Port 1:1 cua CapturedImagePreprocessor de xuat o Findings §8.3.

Thu tu co y: san phang illumination TRUOC khi tang tuong phan, vi CLAHE tren nen
chua phang se khuech dai chinh bong do.
"""
import cv2
import numpy as np


def increase_contrast(mono8, contrast_percent):
    """Tuong duong ModalityAwarePreprocessor.IncreaseContrast.
    100% => tra ve nguyen ban (dung nhu C# return som o dong 156)."""
    if abs(contrast_percent - 100.0) < 1e-9:
        return mono8.copy()
    alpha = contrast_percent / 100.0
    beta = 128.0 * (1.0 - alpha)
    return cv2.convertScaleAbs(mono8, alpha=alpha, beta=beta)


def flatten_and_enhance(mono8, bg_sigma=51.0, clip_limit=3.0, clahe_tile=16):
    """Buoc 1+2. AN TOAN cho ECC va phase correlation -- giu gradient lien tuc.

    bg_sigma phai LON hon nhieu lan be rong trace, neu khong se xoa luon ca trace.
    """
    if mono8 is None or mono8.size == 0:
        raise ValueError("mono8 rong")
    # 1. Chia cho chinh nen da blur manh => khu bong do va nen xam khong deu.
    bg = cv2.GaussianBlur(mono8, (0, 0), bg_sigma)
    src = mono8.astype(np.float32)
    den = bg.astype(np.float32)
    den[den < 1.0] = 1.0
    flat = np.clip(src / den * 255.0, 0, 255).astype(np.uint8)

    # 2. Tang tuong phan CUC BO. ConvertTo tuyen tinh toan cuc khong xu ly duoc nen khong deu.
    clahe = cv2.createCLAHE(clipLimit=clip_limit, tileGridSize=(clahe_tile, clahe_tile))
    return clahe.apply(flat)


def to_binary_traces(enhanced_mono8, block_size=51, c=-5.0,
                     close_kernel=5, close_iterations=2):
    """Buoc 3+4. CHI dung cho chamfer / Hausdorff / ICP / skeleton.

    KHONG duoc dua ket qua nay vao ECC: anh nhi phan co gradient bang 0 o moi noi
    tru bien 1 pixel, ECC se khong hoi tu (Findings §8.2).
    """
    if block_size % 2 == 0:
        block_size += 1
    binary = cv2.adaptiveThreshold(enhanced_mono8, 255,
                                   cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
                                   cv2.THRESH_BINARY, block_size, c)
    if close_kernel <= 1 or close_iterations <= 0:
        return binary
    k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (close_kernel, close_kernel))
    return cv2.morphologyEx(binary, cv2.MORPH_CLOSE, k, iterations=close_iterations)


def build_variants(mono8, cfg, flatten=True, binarize=False):
    """Tra ve dict cac buoc trung gian de UI hien tung cua so."""
    out = {"raw": mono8}
    stage = increase_contrast(mono8, cfg["Contrast"])
    out["contrast"] = stage
    if flatten:
        stage = flatten_and_enhance(stage, cfg["BackgroundSigma"],
                                    cfg["ClaheClipLimit"], cfg["ClaheTile"])
        out["flattened"] = stage
    if binarize:
        out["binary"] = to_binary_traces(stage, cfg["AdaptiveBlockSize"], cfg["AdaptiveC"],
                                         cfg["CloseKernel"], cfg["CloseIterations"])
    out["final"] = stage
    return out
