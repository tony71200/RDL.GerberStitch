"""Doc cau hinh tu align_stitch.ini, fallback ve DUNG mac dinh cua C#."""
import configparser
import os

# Mac dinh lay tu code C# that -- KHONG phai mac dinh cua OpenCV.
DEFAULTS = {
    # EccOptions (Configuration/AlignStitchStageOptions.cs:90-99)
    "EccMotionModel": "Euclidean",   # Translation | Euclidean | Affine
    "EccPyramidLevels": 3,
    "EccMaxIterations": 80,
    "EccEpsilon": 1e-5,
    "EccMinCorrelation": 0.13,
    "EccGaussFiltSize": 5,           # mac dinh cua OpenCvSharp Cv2.FindTransformECC
    # MatcherOptions.cs:21-23
    "MaxAbsRotationDeg": 0.1,
    "MinScale": 0.90,
    "MaxScale": 1.10,
    "MaxTranslationPixels": 300.0,
    # AlignmentPreprocessingOptions.Contrast -- 100 nghia la KHONG lam gi
    "Contrast": 100.0,
    # Findings §8.3 -- gia tri de xuat, PHAI hieu chinh tren anh that (§8.4)
    "BackgroundSigma": 51.0,
    "ClaheClipLimit": 3.0,
    "ClaheTile": 16,
    "AdaptiveBlockSize": 51,
    "AdaptiveC": -5.0,
    "CloseKernel": 5,
    "CloseIterations": 2,
}

_INI_MAP = {
    "eccmincorrelation": ("EccMinCorrelation", float),
    "maxtranslationpixels": ("MaxTranslationPixels", float),
    "maxabsrotationdeg": ("MaxAbsRotationDeg", float),
}


def load(ini_path=None):
    """Tra ve dict cau hinh. Key thieu trong ini giu nguyen mac dinh C#."""
    cfg = dict(DEFAULTS)
    if not ini_path or not os.path.exists(ini_path):
        return cfg
    parser = configparser.ConfigParser()
    # File ini cua repo co dong comment bat dau bang ';' -- configparser xu ly duoc.
    parser.read(ini_path, encoding="utf-8")
    if not parser.has_section("GerberAlignStitch"):
        return cfg
    for raw_key, raw_value in parser.items("GerberAlignStitch"):
        mapped = _INI_MAP.get(raw_key.lower())
        if mapped is None:
            continue
        name, caster = mapped
        try:
            cfg[name] = caster(raw_value)
        except ValueError:
            pass
    return cfg
