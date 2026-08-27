from __future__ import annotations

from typing import Any
from uuid import UUID

from pydantic import BaseModel, Field


class HealthResponse(BaseModel):
    status: str
    service: str
    model_loaded: bool = False
    model_version: str = "not-loaded"


class AttachmentIn(BaseModel):
    type: str
    file_url: str


class ValidateOptions(BaseModel):
    confidence_threshold: float = 60.0
    return_tokens: bool = False


class ValidateRequest(BaseModel):
    job_id: UUID
    document_id: UUID
    document_type: str          # SF08 | ACCOMPLISHMENT | ACCREDITATION
    primary_file_url: str
    attachments: list[AttachmentIn] = Field(default_factory=list)
    options: ValidateOptions = Field(default_factory=ValidateOptions)


class FieldResult(BaseModel):
    name: str
    label: str                  # PRESENT | MISSING | ILLEGIBLE | CONDITIONALLY_REQUIRED | SIGNED | UNSIGNED
    ocr_confidence: float | None = None
    matched_text: str | None = None
    is_conditional: bool | None = None
    condition: str | None = None


class AttachmentResult(BaseModel):
    type: str
    label: str                  # ATTACHMENT_PRESENT | ATTACHMENT_MISSING | CONDITIONALLY_REQUIRED
    is_conditional: bool | None = None
    condition: str | None = None


class ConsistencyIssueOut(BaseModel):
    code: str
    description: str
    severity: str = "warning"   # warning | error


class PreprocessingInfo(BaseModel):
    deskew_angle: float = 0.0
    steps: list[str] = Field(default_factory=list)
    page_count: int = 1


class OcrTokenOut(BaseModel):
    text: str
    confidence: float
    bbox: list[int] = Field(default_factory=list)
    low_confidence: bool = False


class OcrInfo(BaseModel):
    """Layer 2 payload persisted by the backend into OCRResult / OCRErrorLog."""
    full_text: str = ""
    avg_confidence: float = 0.0          # 0–100 scale (Tesseract)
    token_count: int = 0
    low_confidence_count: int = 0
    engine: str = "tesseract5-lstm-best"
    tokens: list[OcrTokenOut] = Field(default_factory=list)


class ValidateResponse(BaseModel):
    job_id: UUID
    document_class: str         # Valid Submission | Incomplete Submission | Attachment Missing | Structurally Invalid | Requires Human Review
    confidence: float
    requires_human_review: bool
    fields: list[FieldResult] = Field(default_factory=list)
    attachments: list[AttachmentResult] = Field(default_factory=list)
    consistency_issues: list[ConsistencyIssueOut] = Field(default_factory=list)
    preprocessing: PreprocessingInfo = Field(default_factory=PreprocessingInfo)
    ocr: OcrInfo | None = None
    model_version: str = "layoutlmv3-tsu-rule-based-v1"
    inference_path: str = "rule_based"
    meta: dict[str, Any] = Field(default_factory=dict)
