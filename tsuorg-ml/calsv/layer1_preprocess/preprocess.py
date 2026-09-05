"""
Layer 1 — OpenCV Image Preprocessing (CALSV-01)

Steps (in order, per paper objectives 1.2.1):
  1. Grayscale conversion
  2. Adaptive thresholding
  3. Deskewing using Hough Line Transform
  4. Noise removal
  5. Border cropping
"""

from __future__ import annotations

import io
import logging
import math
from dataclasses import dataclass, field

import cv2
import numpy as np

logger = logging.getLogger(__name__)


@dataclass
class PreprocessResult:
    image_bytes: bytes           # PNG bytes of cleaned image
    deskew_angle: float = 0.0   # degrees corrected
    steps: list[str] = field(default_factory=list)
    width: int = 0
    height: int = 0


def preprocess_document(image_bytes: bytes) -> PreprocessResult:
    """
    Full CALSV Layer 1 pipeline.
    Input: raw image bytes (PNG/JPEG from file_loader).
    Output: clean PNG bytes ready for Tesseract + LayoutLMv3.
    """
    steps_done: list[str] = []

    # Decode
    if not image_bytes:
        logger.warning("Empty image bytes provided; returning passthrough")
        return PreprocessResult(image_bytes=image_bytes, steps=["empty_input"])

    img = _decode_image(image_bytes)
    if img is None:
        logger.warning("Could not decode image bytes; returning passthrough")
        return PreprocessResult(image_bytes=image_bytes, steps=["decode_failed"])

    # 1. Grayscale
    gray = _to_grayscale(img)
    steps_done.append("grayscale")

    # 2. Adaptive thresholding (used for skew estimation)
    thresh = _adaptive_threshold(gray)
    steps_done.append("adaptive_threshold")

    # 3. Deskew via Hough Line Transform (rotate grayscale using thresh-estimated angle)
    deskewed_gray, angle = _deskew(thresh, gray)
    steps_done.append(f"deskew({angle:.2f}deg)")

    # Re-threshold after rotate so Layer 2 sees the binary page required by CALSV-01.
    binary = _adaptive_threshold(deskewed_gray)

    # 4. Noise removal on the binary page
    denoised = _denoise(binary)
    steps_done.append("denoise")

    # 5. Border cropping (remove empty margins)
    cropped = _crop_border(denoised)
    steps_done.append("crop_border")

    h, w = cropped.shape[:2]
    out_bytes = _encode_png(cropped)

    return PreprocessResult(
        image_bytes=out_bytes,
        deskew_angle=angle,
        steps=steps_done,
        width=w,
        height=h,
    )


# ─── private helpers ────────────────────────────────────────────────────────


def _decode_image(data: bytes) -> np.ndarray | None:
    arr = np.frombuffer(data, dtype=np.uint8)
    img = cv2.imdecode(arr, cv2.IMREAD_COLOR)
    return img


def _to_grayscale(img: np.ndarray) -> np.ndarray:
    if len(img.shape) == 2:
        return img
    return cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)


def _adaptive_threshold(gray: np.ndarray) -> np.ndarray:
    """
    Adaptive Gaussian thresholding — robust against uneven illumination from scanned docs.
    Block size 15, C=10 gives good results for A4 @300 DPI form pages.
    """
    return cv2.adaptiveThreshold(
        gray,
        maxValue=255,
        adaptiveMethod=cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        thresholdType=cv2.THRESH_BINARY,
        blockSize=15,
        C=10,
    )


def _deskew(thresh: np.ndarray, gray: np.ndarray) -> tuple[np.ndarray, float]:
    """
    Estimate skew angle via Hough Line Transform then rotate to correct it.
    Returns (corrected_grayscale_image, angle_degrees).
    """
    angle = _estimate_skew_angle(thresh)

    if abs(angle) < 0.3:
        # Not worth rotating — avoid interpolation artifacts
        return gray, 0.0

    corrected = _rotate_image(gray, angle)
    return corrected, angle


def _estimate_skew_angle(thresh: np.ndarray) -> float:
    """
    Use Hough Probabilistic Line Transform on edges to estimate dominant text-line angle.
    Returns angle in degrees (positive = clockwise tilt).
    """
    edges = cv2.Canny(thresh, 50, 150, apertureSize=3)

    lines = cv2.HoughLinesP(
        edges,
        rho=1,
        theta=np.pi / 180,
        threshold=100,
        minLineLength=100,
        maxLineGap=10,
    )

    if lines is None or len(lines) == 0:
        return 0.0

    angles: list[float] = []
    for line in lines:
        x1, y1, x2, y2 = line[0]
        if x2 == x1:
            continue
        angle = math.degrees(math.atan2(y2 - y1, x2 - x1))
        # Keep only near-horizontal lines (text lines)
        if -45 < angle < 45:
            angles.append(angle)

    if not angles:
        return 0.0

    # Median is more robust than mean against outliers
    median_angle = float(np.median(angles))
    return median_angle


def _rotate_image(img: np.ndarray, angle: float) -> np.ndarray:
    h, w = img.shape[:2]
    center = (w / 2.0, h / 2.0)
    M = cv2.getRotationMatrix2D(center, angle, scale=1.0)
    rotated = cv2.warpAffine(
        img, M, (w, h),
        flags=cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_REPLICATE,
    )
    return rotated


def _denoise(img: np.ndarray) -> np.ndarray:
    """
    fastNlMeansDenoising for grayscale.
    h=10: standard strength — keeps thin characters intact.
    """
    if len(img.shape) == 3:
        return cv2.fastNlMeansDenoisingColored(img, None, h=10, hColor=10,
                                               templateWindowSize=7, searchWindowSize=21)
    return cv2.fastNlMeansDenoising(img, None, h=10,
                                    templateWindowSize=7, searchWindowSize=21)


def _crop_border(img: np.ndarray) -> np.ndarray:
    """
    Remove white/light empty margins around the content using contour bounding rect.
    Falls back to original if nothing useful found.
    """
    # Invert so text is white on black (needed for contour detection)
    inverted = cv2.bitwise_not(img) if len(img.shape) == 2 else img

    gray = inverted if len(inverted.shape) == 2 else cv2.cvtColor(inverted, cv2.COLOR_BGR2GRAY)
    _, binary = cv2.threshold(gray, 10, 255, cv2.THRESH_BINARY)

    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    if not contours:
        return img

    # Bounding box of ALL content contours
    x_min, y_min = img.shape[1], img.shape[0]
    x_max, y_max = 0, 0

    for c in contours:
        x, y, cw, ch = cv2.boundingRect(c)
        x_min = min(x_min, x)
        y_min = min(y_min, y)
        x_max = max(x_max, x + cw)
        y_max = max(y_max, y + ch)

    # Add 5 px padding so we don't clip characters at the edge
    pad = 5
    h, w = img.shape[:2]
    x1 = max(0, x_min - pad)
    y1 = max(0, y_min - pad)
    x2 = min(w, x_max + pad)
    y2 = min(h, y_max + pad)

    cropped = img[y1:y2, x1:x2]
    if cropped.size == 0:
        return img

    return cropped


def _encode_png(img: np.ndarray) -> bytes:
    ok, encoded = cv2.imencode(".png", img)
    if not ok:
        raise RuntimeError("cv2.imencode failed — cannot produce PNG output")
    return encoded.tobytes()
