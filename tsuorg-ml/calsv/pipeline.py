"""
CALSV orchestration pipeline — Layer 1 → 2 → 3.

Called by FastAPI /v1/validate.
"""

from __future__ import annotations

import logging

from app.config import get_settings
from app.schemas import (
    AttachmentResult,
    ConsistencyIssueOut,
    FieldResult,
    OcrInfo,
    OcrTokenOut,
    PreprocessingInfo,
    ValidateRequest,
    ValidateResponse,
)
from calsv.layer1_preprocess.preprocess import preprocess_document
from calsv.layer2_ocr.ocr import OcrResult, extract_text
from calsv.layer3_layoutlm.infer import infer_layoutlm
from calsv.utils.file_loader import LoadedFile, load_file

logger = logging.getLogger(__name__)


def run_calsv(request: ValidateRequest) -> ValidateResponse:
    """
    Full CALSV pipeline for a submitted document bundle.

    Step 1 — Download primary document from signed URL.
    Step 2 — Layer 1: OpenCV preprocess.
    Step 3 — Layer 2: Tesseract OCR.
    Step 4 — Layer 3: LayoutLMv3 / rule-based validation.
    Step 5 — Assemble response.
    """
    settings = get_settings()

    # ── Step 1: File download & normalisation ──────────────────────────────
    logger.info("CALSV job %s — document_type=%s", request.job_id, request.document_type)

    try:
        loaded: LoadedFile = load_file(
            request.primary_file_url,
            target_dpi=settings.target_dpi,
        )
        raw_image_bytes = loaded.image_bytes
        page_count = loaded.page_count
    except Exception as exc:
        logger.error("File download failed for job %s: %s", request.job_id, exc)
        # Return a human-review result so the submission is not silently lost
        return _error_response(request, str(exc))

    # ── Step 2: Layer 1 — OpenCV preprocess ──────────────────────────────
    try:
        pre = preprocess_document(raw_image_bytes)
        logger.debug("Preprocess done: angle=%.2f steps=%s", pre.deskew_angle, pre.steps)
    except Exception as exc:
        logger.error("Preprocess error for job %s: %s", request.job_id, exc)
        pre_image = raw_image_bytes
        preprocess_steps = ["failed"]
        deskew_angle = 0.0
    else:
        pre_image = pre.image_bytes
        preprocess_steps = pre.steps
        deskew_angle = pre.deskew_angle

    # ── Step 3: Layer 2 — Tesseract OCR ──────────────────────────────────
    try:
        ocr: OcrResult = extract_text(
            pre_image,
            confidence_threshold=request.options.confidence_threshold,
        )
        logger.debug(
            "OCR done: %d tokens, %d low-confidence",
            ocr.total_token_count,
            ocr.low_confidence_count,
        )
    except Exception as exc:
        logger.error("OCR error for job %s: %s", request.job_id, exc)
        ocr = OcrResult()

    # ── Step 4: Layer 3 — LayoutLMv3 / rule-based validation ─────────────
    try:
        ai = infer_layoutlm(
            document_type=request.document_type,
            tokens=ocr.tokens,
            image_bytes=pre_image,
            attachments=[a.type for a in request.attachments],
            ocr_result=ocr,
        )
    except Exception as exc:
        logger.error("Layer 3 error for job %s: %s", request.job_id, exc)
        return _error_response(request, f"Layer 3 failure: {exc}")

    # ── Step 5: Assemble response ─────────────────────────────────────────
    # Always return OCR summary so the backend can persist OCRResult.
    # Full token list is included when options.return_tokens=true (wizard preview).
    ocr_payload = _build_ocr_info(ocr, include_tokens=request.options.return_tokens)

    return ValidateResponse(
        job_id=request.job_id,
        document_class=ai.document_class,
        confidence=ai.confidence,
        requires_human_review=ai.requires_human_review,
        fields=[FieldResult(**f) for f in ai.fields],
        attachments=[AttachmentResult(**a) for a in ai.attachments],
        consistency_issues=[ConsistencyIssueOut(**c) for c in ai.consistency_issues],
        preprocessing=PreprocessingInfo(
            deskew_angle=deskew_angle,
            steps=preprocess_steps,
            page_count=page_count,
        ),
        ocr=ocr_payload,
        model_version=ai.model_version,
        inference_path=ai.inference_path,
        meta={
            "ocr_token_count": ocr.total_token_count,
            "low_confidence_token_count": ocr.low_confidence_count,
            "ocr_avg_confidence": ocr_payload.avg_confidence,
        },
    )


def _build_ocr_info(ocr: OcrResult, *, include_tokens: bool) -> OcrInfo:
    scored = [t.confidence for t in ocr.tokens if t.confidence >= 0]
    avg = (sum(scored) / len(scored)) if scored else 0.0

    tokens_out: list[OcrTokenOut] = []
    if include_tokens:
        # Cap payload size for large scans (backend also caps OCRErrorLog at 100).
        for t in ocr.tokens[:2000]:
            tokens_out.append(
                OcrTokenOut(
                    text=t.text,
                    confidence=t.confidence,
                    bbox=list(t.bbox),
                    low_confidence=t.low_confidence,
                )
            )

    return OcrInfo(
        full_text=ocr.full_text,
        avg_confidence=round(avg, 2),
        token_count=ocr.total_token_count,
        low_confidence_count=ocr.low_confidence_count,
        engine=ocr.engine,
        tokens=tokens_out,
    )


def _error_response(request: ValidateRequest, detail: str) -> ValidateResponse:
    return ValidateResponse(
        job_id=request.job_id,
        document_class="Requires Human Review",
        confidence=0.0,
        requires_human_review=True,
        fields=[],
        attachments=[],
        consistency_issues=[],
        preprocessing=PreprocessingInfo(),
        model_version="error",
        inference_path="error",
        meta={"error": detail},
    )
