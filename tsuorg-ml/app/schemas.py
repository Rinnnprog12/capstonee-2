from __future__ import annotations

from typing import Any
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


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


class CatalogOrg(BaseModel):
    name: str
    acronym: str = ""
    aliases: list[str] = Field(default_factory=list)
    college_code: str | None = None
    college_name: str | None = None
    adviser_name: str | None = None


class CatalogIn(BaseModel):
    submitted_name: str | None = None
    submitted_acronym: str | None = None
    submitted_college_code: str | None = None
    submitted_adviser_name: str | None = None
    organizations: list[CatalogOrg] = Field(default_factory=list)


class ValidateRequest(BaseModel):
    job_id: UUID
    document_id: UUID
    document_type: str          # SF08 | ACCOMPLISHMENT | ACCREDITATION
    primary_file_url: str
    attachments: list[AttachmentIn] = Field(default_factory=list)
    options: ValidateOptions = Field(default_factory=ValidateOptions)
    catalog: CatalogIn | None = None


class FieldResult(BaseModel):
    model_config = ConfigDict(extra="ignore")
    name: str
    label: str                  # PRESENT | MISSING | ILLEGIBLE | CONDITIONALLY_REQUIRED | SIGNED | UNSIGNED
    ocr_confidence: float | None = None
    matched_text: str | None = None
    is_conditional: bool | None = None
    condition: str | None = None
    catalog_acronym: str | None = None
    college_code: str | None = None
    college_name: str | None = None


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
    avg_confidence: float = 0.0          # 0–1 unit (Tesseract 0–100 is converted in pipeline)
    token_count: int = 0
    low_confidence_count: int = 0
    engine: str = "tesseract5-lstm-best"
    tokens: list[OcrTokenOut] = Field(default_factory=list)


class AttachmentOcrResult(BaseModel):
    """OCR results for a single attachment."""
    attachment_type: str
    full_text: str = ""
    avg_confidence: float = 0.0
    token_count: int = 0
    low_confidence_count: int = 0
    engine: str = "tesseract5-lstm-best"
    status: str = "completed"  # completed | failed
    error: str | None = None


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
    attachment_ocr_results: list[AttachmentOcrResult] = Field(default_factory=list)  # NEW: per-attachment OCR
    model_version: str = "layoutlmv3-tsu-rule-based-v1"
    inference_path: str = "rule_based"
    meta: dict[str, Any] = Field(default_factory=dict)
