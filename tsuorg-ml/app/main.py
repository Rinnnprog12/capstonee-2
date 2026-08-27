from __future__ import annotations

import logging

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from app.config import get_settings
from app.schemas import HealthResponse, ValidateRequest, ValidateResponse
from calsv.layer3_layoutlm.model_loader import ensure_loaded, get_model_version, is_model_loaded
from calsv.pipeline import run_calsv

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(
    title="TSU-ORGDOCX CALSV Engine",
    version="1.0.0",
    description=(
        "Context-Aware Layout-Sensitive Validation Engine — "
        "OpenCV (Layer 1) → Tesseract OCR (Layer 2) → LayoutLMv3 (Layer 3)"
    ),
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.on_event("startup")
async def startup_event() -> None:
    settings = get_settings()
    model_dir = settings.model_dir / settings.model_name
    loaded = ensure_loaded(model_dir)
    if loaded:
        logger.info("LayoutLMv3 model ready: %s", get_model_version())
    else:
        logger.info("No trained model found — CALSV will use rule-based validation.")


@app.get("/health", response_model=HealthResponse)
def health() -> HealthResponse:
    return HealthResponse(
        status="ok",
        service="tsuorg-ml",
        model_loaded=is_model_loaded(),
        model_version=get_model_version(),
    )


@app.post("/v1/validate", response_model=ValidateResponse)
def validate(body: ValidateRequest) -> ValidateResponse:
    """
    Full CALSV pipeline:
      Layer 1: OpenCV image preprocessing
      Layer 2: Tesseract OCR v5 with per-word confidence
      Layer 3: LayoutLMv3 semantic completeness validation (or rule-based fallback)
    """
    return run_calsv(body)
