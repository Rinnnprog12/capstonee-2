"""
Layer 2 — Confidence-Aware OCR Error Flagging (CALSV-02, CALSV-03)

Accuracy-first Tesseract 5.x setup:
  • OEM 1 — LSTM neural net only
  • tessdata_best — highest-quality traineddata (not tessdata_fast)
  • PSM 6 — uniform text block (university forms)
  • Per-word confidence → LOW_CONFIDENCE flags for Layer 3
"""

from __future__ import annotations

import io
import logging
import os
from dataclasses import dataclass, field
from pathlib import Path

import pytesseract
from PIL import Image

from app.config import get_settings

logger = logging.getLogger(__name__)

_configured = False


def _configure_tesseract() -> None:
    """Point pytesseract at Tesseract 5 + tessdata_best (once per process)."""
    global _configured
    if _configured:
        return

    settings = get_settings()

    cmd = settings.tesseract_cmd.strip()
    if cmd and Path(cmd).exists():
        pytesseract.pytesseract.tesseract_cmd = cmd
    elif cmd and cmd != "tesseract":
        # Path may still be on PATH under a different install — try anyway
        pytesseract.pytesseract.tesseract_cmd = cmd

    tessdata = settings.tessdata_dir.strip()
    if tessdata and Path(tessdata).is_dir():
        # Prefer tessdata_best over the default tessdata/ folder
        os.environ["TESSDATA_PREFIX"] = str(Path(tessdata).resolve())
        logger.info("Using tessdata_best at %s", tessdata)
    else:
        logger.warning(
            "CALSV_TESSDATA_DIR '%s' not found. "
            "Install tessdata_best (eng.traineddata) for highest OCR accuracy. "
            "Falling back to Tesseract default tessdata.",
            tessdata,
        )

    try:
        ver = pytesseract.get_tesseract_version()
        logger.info("Tesseract version: %s (OEM=%s PSM=%s)", ver, settings.tesseract_oem, settings.tesseract_psm)
        if ver.major < 5:
            logger.warning(
                "Tesseract %s detected — paper requires v5.x for best LSTM accuracy. "
                "Upgrade from https://github.com/UB-Mannheim/tesseract/wiki",
                ver,
            )
    except Exception as exc:
        logger.warning("Could not read Tesseract version: %s", exc)

    _configured = True


def _build_tesseract_config(settings) -> str:
    """Accuracy-oriented Tesseract CLI config string.

    Note: tessdata path is handled via TESSDATA_PREFIX env-var set in
    _configure_tesseract() rather than --tessdata-dir so that Windows paths
    containing spaces are not mishandled by pytesseract's command builder.
    """
    parts = [
        f"--oem {settings.tesseract_oem}",
        f"--psm {settings.tesseract_psm}",
        f"-c tessedit_pageseg_mode={settings.tesseract_psm}",
        f"-c user_defined_dpi={settings.tesseract_dpi}",
    ]
    if settings.tesseract_preserve_spaces:
        parts.append("-c preserve_interword_spaces=1")

    return " ".join(parts)


@dataclass
class OcrToken:
    text: str
    confidence: float           # 0–100; -1 if Tesseract returned no confidence
    bbox: list[int]             # [left, top, width, height] in pixels
    block_num: int = 0
    line_num: int = 0
    word_num: int = 0
    low_confidence: bool = False

    @property
    def x(self) -> int:
        return self.bbox[0]

    @property
    def y(self) -> int:
        return self.bbox[1]

    @property
    def w(self) -> int:
        return self.bbox[2]

    @property
    def h(self) -> int:
        return self.bbox[3]

    def normalized_bbox(self, img_w: int, img_h: int) -> list[int]:
        """LayoutLMv3 expects bbox in [0,1000] range."""
        return [
            int(self.x / img_w * 1000),
            int(self.y / img_h * 1000),
            int((self.x + self.w) / img_w * 1000),
            int((self.y + self.h) / img_h * 1000),
        ]


@dataclass
class OcrResult:
    tokens: list[OcrToken] = field(default_factory=list)
    full_text: str = ""
    image_width: int = 0
    image_height: int = 0
    low_confidence_count: int = 0
    total_token_count: int = 0
    engine: str = "tesseract5-lstm-best"


def extract_text(image_bytes: bytes, confidence_threshold: float | None = None) -> OcrResult:
    """
    Full Tesseract OCR pass on a preprocessed image.

    Returns all word tokens with per-word confidence scores.
    Any token with confidence < threshold (or conf == -1 from Tesseract) is
    flagged as LOW_CONFIDENCE, which Layer 3 uses as an ILLEGIBLE signal.
    """
    _configure_tesseract()
    settings = get_settings()
    threshold = confidence_threshold if confidence_threshold is not None else settings.confidence_threshold
    config = _build_tesseract_config(settings)

    try:
        img = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    except Exception as exc:
        logger.error("Cannot open image for OCR: %s", exc)
        return OcrResult()

    img_w, img_h = img.size

    try:
        data = pytesseract.image_to_data(
            img,
            config=config,
            output_type=pytesseract.Output.DICT,
            lang=settings.tesseract_lang,
        )
    except pytesseract.TesseractNotFoundError:
        logger.error(
            "Tesseract binary not found. "
            "Install Tesseract 5.x (UB Mannheim) and set CALSV_TESSERACT_CMD / CALSV_TESSDATA_DIR."
        )
        return OcrResult(image_width=img_w, image_height=img_h)
    except Exception as exc:
        logger.error("Tesseract OCR failed: %s", exc)
        return OcrResult(image_width=img_w, image_height=img_h)

    tokens: list[OcrToken] = []
    n = len(data["text"])

    for i in range(n):
        raw_text = data["text"][i]
        if not raw_text or not raw_text.strip():
            continue

        conf_raw = data["conf"][i]
        conf = float(conf_raw)          # -1 when Tesseract is uncertain
        is_low = conf < threshold or conf == -1

        token = OcrToken(
            text=raw_text.strip(),
            confidence=conf,
            bbox=[
                data["left"][i],
                data["top"][i],
                data["width"][i],
                data["height"][i],
            ],
            block_num=int(data.get("block_num", [0] * n)[i]),
            line_num=int(data.get("line_num", [0] * n)[i]),
            word_num=int(data.get("word_num", [0] * n)[i]),
            low_confidence=is_low,
        )
        tokens.append(token)

    full_text = " ".join(t.text for t in tokens)
    low_count = sum(1 for t in tokens if t.low_confidence)

    if low_count > 0:
        logger.debug(
            "%d / %d tokens flagged LOW_CONFIDENCE (threshold=%.1f)",
            low_count, len(tokens), threshold,
        )

    return OcrResult(
        tokens=tokens,
        full_text=full_text,
        image_width=img_w,
        image_height=img_h,
        low_confidence_count=low_count,
        total_token_count=len(tokens),
        engine="tesseract5-lstm-best",
    )


def build_text_map(ocr: OcrResult) -> dict[str, list[OcrToken]]:
    """
    Group tokens by line for cross-field consistency checks in Layer 3.
    Returns {block_line_key: [tokens...]}.
    """
    lines: dict[str, list[OcrToken]] = {}
    for t in ocr.tokens:
        key = f"{t.block_num}_{t.line_num}"
        lines.setdefault(key, []).append(t)
    return lines
