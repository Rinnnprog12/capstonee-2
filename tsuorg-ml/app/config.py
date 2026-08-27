from __future__ import annotations

from functools import lru_cache
from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict


class CalsvSettings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="CALSV_", env_file=".env", extra="ignore")

    # Model
    model_dir: Path = Path("models")
    model_name: str = "layoutlmv3-tsu-v1"
    confidence_threshold: float = 60.0
    human_review_confidence: float = 0.70   # below this → Requires Human Review

    # ── Tesseract (accuracy-first) ──────────────────────────────────────────
    # Install Tesseract 5.x and use tessdata_best (slow/accurate LSTM models),
    # NOT tessdata_fast. On Windows default install path is used if cmd is empty.
    tesseract_cmd: str = r"C:\Program Files\Tesseract-OCR\tesseract.exe"
    # Point at tessdata_best folder (contains eng.traineddata from tessdata_best repo)
    tessdata_dir: str = r"C:\Program Files\Tesseract-OCR\tessdata_best"
    tesseract_lang: str = "eng"
    # OEM 1 = LSTM neural net only (best accuracy on Tesseract 5)
    tesseract_oem: int = 1
    # PSM 6 = assume a single uniform block of text (forms); 4 = sparse text, 3 = fully auto
    tesseract_psm: int = 6
    # Hint DPI for better glyph sizing (matches Layer 1 target)
    tesseract_dpi: int = 300
    # Whitelist common form chars (letters, digits, punctuation used on SF08)
    tesseract_preserve_spaces: bool = True

    # Processing
    target_dpi: int = 300
    image_size: int = 224                    # LayoutLMv3 patch size
    max_image_pages: int = 1                 # pages to process per document

    # Debugging
    debug: bool = False


@lru_cache(maxsize=1)
def get_settings() -> CalsvSettings:
    return CalsvSettings()
