"""
Signature presence detection using OpenCV heuristics.

Strategy:
  1. Identify candidate signature zones (lower third of the document —
     where signature lines on SOU forms typically appear).
  2. Within each zone, look for ink-density patterns that differ from
     printed text (irregular, curved strokes with variable thickness).
  3. Return a confidence score [0,1] and a SIGNED / UNSIGNED label.

This is a heuristic detector — it does not identify *whose* signature it is.
LayoutLMv3 fine-tuning will learn layout-aware signature detection;
this module serves as the rule-based pre-check and fallback.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass

import cv2
import numpy as np

logger = logging.getLogger(__name__)

SIGNATURE_CONFIDENCE_THRESHOLD = 0.45   # below this → UNSIGNED


@dataclass
class SignatureDetectionResult:
    label: str          # "SIGNED" or "UNSIGNED"
    confidence: float   # 0.0–1.0
    zones_checked: int
    zones_with_signature: int


def detect_signatures(image_bytes: bytes, num_signature_zones: int = 2) -> SignatureDetectionResult:
    """
    Scan the bottom region of the preprocessed document image for signature marks.

    Args:
        image_bytes: Preprocessed PNG bytes (after Layer 1).
        num_signature_zones: How many signature slots to expect (per document type).

    Returns:
        SignatureDetectionResult with label + confidence.
    """
    img = _decode(image_bytes)
    if img is None:
        return SignatureDetectionResult("UNSIGNED", 0.0, 0, 0)

    h, w = img.shape[:2]
    gray = _ensure_gray(img)

    # Examine bottom 40% of the document — where signature lines are
    zones = _define_signature_zones(w, h, num_signature_zones)
    signed_zones = 0

    for zone in zones:
        roi = _extract_roi(gray, zone)
        if roi is None:
            continue
        score = _signature_score(roi)
        if score >= SIGNATURE_CONFIDENCE_THRESHOLD:
            signed_zones += 1
        logger.debug("Signature zone %s score=%.3f", zone, score)

    total_zones = len(zones)
    overall_conf = signed_zones / total_zones if total_zones > 0 else 0.0
    label = "SIGNED" if signed_zones >= max(1, total_zones // 2) else "UNSIGNED"

    return SignatureDetectionResult(
        label=label,
        confidence=overall_conf,
        zones_checked=total_zones,
        zones_with_signature=signed_zones,
    )


# ─── helpers ────────────────────────────────────────────────────────────────


def _decode(data: bytes) -> np.ndarray | None:
    if not data:
        return None
    arr = np.frombuffer(data, np.uint8)
    if arr.size == 0:
        return None
    return cv2.imdecode(arr, cv2.IMREAD_COLOR)


def _ensure_gray(img: np.ndarray) -> np.ndarray:
    if len(img.shape) == 2:
        return img
    return cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)


def _define_signature_zones(
    w: int, h: int, num_zones: int
) -> list[tuple[int, int, int, int]]:
    """
    Return (x, y, width, height) tuples for expected signature areas.
    Standard SOU forms have 2 signature slots in the lower portion.
    """
    bottom_start = int(h * 0.60)
    zone_h = int(h * 0.15)
    slot_w = w // (num_zones + 1)

    zones = []
    for i in range(num_zones):
        x = slot_w * (i + 1) - slot_w // 2
        zones.append((x, bottom_start, slot_w, zone_h))
    return zones


def _extract_roi(gray: np.ndarray, zone: tuple[int, int, int, int]) -> np.ndarray | None:
    x, y, w, h = zone
    img_h, img_w = gray.shape[:2]
    x1, y1 = max(0, x), max(0, y)
    x2, y2 = min(img_w, x + w), min(img_h, y + h)
    roi = gray[y1:y2, x1:x2]
    return roi if roi.size > 0 else None


def _signature_score(roi: np.ndarray) -> float:
    """
    Heuristic score for whether a signature is present in a ROI.

    Signatures have:
      - More dark pixels than a blank area
      - Irregular/curved contours (high contour count relative to area)
      - Medium ink density (not like printed text blocks which are very dense)
    """
    _, binary = cv2.threshold(roi, 0, 255, cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU)

    total_px = roi.size
    if total_px == 0:
        return 0.0

    ink_px = int(np.sum(binary > 0))
    ink_density = ink_px / total_px

    # Too few pixels → blank area
    if ink_density < 0.01:
        return 0.0

    # Too dense → probably printed text block, not a signature
    if ink_density > 0.40:
        return 0.0

    # Count contours — signatures have medium-many irregular contours
    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    contour_count = len(contours)

    # Normalize: 5–50 contours is the typical signature range
    contour_score = min(1.0, contour_count / 30.0) if contour_count >= 3 else 0.0

    # Check for non-linear contours (circularity < 1 → curved)
    curvature_scores = []
    for c in contours:
        area = cv2.contourArea(c)
        perim = cv2.arcLength(c, closed=True)
        if perim > 0 and area > 0:
            circularity = 4 * np.pi * area / (perim ** 2)
            curvature_scores.append(1.0 - min(1.0, circularity))

    avg_curvature = float(np.mean(curvature_scores)) if curvature_scores else 0.0

    # Final score — weighted combination
    score = 0.4 * min(1.0, ink_density / 0.15) + 0.35 * contour_score + 0.25 * avg_curvature
    return float(np.clip(score, 0.0, 1.0))
