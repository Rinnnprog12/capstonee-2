"""
Layer 3 — LayoutLMv3 Semantic Completeness Validation (CALSV-04, CALSV-05)

Validates (per paper objectives 1.2.3):
  1.2.3.1  Document type classification
  1.2.3.2  Field completeness validation
  1.2.3.3  Format compliance validation
  1.2.3.4  Conditional field requirements inference
  1.2.3.5  Missing attachments detection
  1.2.3.6  Signature presence (delegated to signature_detector)
  1.2.3.7  Cross-field consistency validation

Two execution paths:
  A) Fine-tuned LayoutLMv3 available → neural inference + rule post-processing
  B) Model not trained yet → rule-based-only validation (keyword matching + OCR text analysis)
"""

from __future__ import annotations

import io
import logging
import re
from dataclasses import dataclass, field
from pathlib import Path

from app.config import get_settings
from calsv.layer2_ocr.ocr import OcrResult, OcrToken
from calsv.layer3_layoutlm.model_loader import (
    DOC_TYPE_CLASSES,
    ensure_loaded,
    get_model,
    get_model_version,
    get_processor,
    get_seq_head,
    is_model_loaded,
)
from calsv.layer3_layoutlm.rules import (
    RULES_REGISTRY,
    AttachmentRule,
    ConditionType,
    DocumentTypeRule,
    FieldRule,
    get_rules,
)
from calsv.layer3_layoutlm.signature_detector import detect_signatures

logger = logging.getLogger(__name__)

# Document-level output classes (paper §4.3.3.2.4.4)
DOC_CLASS_VALID         = "Valid Submission"
DOC_CLASS_INCOMPLETE    = "Incomplete Submission"
DOC_CLASS_ATTACH_MISS   = "Attachment Missing"
DOC_CLASS_STRUCTURAL    = "Structurally Invalid"
DOC_CLASS_HUMAN_REVIEW  = "Requires Human Review"

# Field labels (paper Table 15)
LABEL_PRESENT              = "PRESENT"
LABEL_MISSING              = "MISSING"
LABEL_ILLEGIBLE            = "ILLEGIBLE"
LABEL_CONDITIONALLY_REQ    = "CONDITIONALLY_REQUIRED"
LABEL_SIGNED               = "SIGNED"
LABEL_UNSIGNED             = "UNSIGNED"
LABEL_ATTACH_PRESENT       = "ATTACHMENT_PRESENT"
LABEL_ATTACH_MISSING       = "ATTACHMENT_MISSING"
LABEL_INCOMPLETE_SUBMISSION = "INCOMPLETE_SUBMISSION"


@dataclass
class FieldValidation:
    name: str
    label: str
    ocr_confidence: float | None = None
    matched_text: str | None = None


@dataclass
class AttachmentValidation:
    type: str
    label: str
    is_conditional: bool = False
    condition: str | None = None


@dataclass
class ConsistencyIssue:
    code: str
    description: str
    severity: str = "warning"   # "warning" | "error"


@dataclass
class LayoutLmResult:
    document_class: str
    confidence: float
    requires_human_review: bool
    fields: list[dict]
    attachments: list[dict]
    consistency_issues: list[dict] = field(default_factory=list)
    model_version: str = "layoutlmv3-tsu-rule-based-v1"
    inference_path: str = "rule_based"   # "neural" | "rule_based"


# ─── main entry ─────────────────────────────────────────────────────────────

def infer_layoutlm(
    document_type: str,
    tokens: list[OcrToken],
    image_bytes: bytes,
    attachments: list[str],
    ocr_result: OcrResult | None = None,
) -> LayoutLmResult:
    """
    Run Layer 3 validation.

    Args:
        document_type: One of SF08 / ACCOMPLISHMENT / ACCREDITATION.
        tokens: OcrToken list from Layer 2.
        image_bytes: Preprocessed image (PNG) for signature detection.
        attachments: List of attachment type keys submitted by the user.
        ocr_result: Full OcrResult for image dimensions (used in neural path).
    """
    settings = get_settings()

    # Lazy-load model on first call
    model_dir = settings.model_dir / settings.model_name
    ensure_loaded(model_dir)

    rules = get_rules(document_type)
    if rules is None:
        logger.warning("Unknown document type '%s' — defaulting to human review", document_type)
        return LayoutLmResult(
            document_class=DOC_CLASS_HUMAN_REVIEW,
            confidence=0.0,
            requires_human_review=True,
            fields=[],
            attachments=[],
            model_version=get_model_version(),
        )

    # Choose inference path
    if is_model_loaded():
        return _neural_inference(document_type, tokens, image_bytes, attachments, rules, ocr_result)
    else:
        return _rule_based_inference(document_type, tokens, image_bytes, attachments, rules)


# ─── path A: neural ─────────────────────────────────────────────────────────

def _neural_inference(
    document_type: str,
    tokens: list[OcrToken],
    image_bytes: bytes,
    attachments: list[str],
    rules: DocumentTypeRule,
    ocr_result: OcrResult | None,
) -> LayoutLmResult:
    """
    Run fine-tuned LayoutLMv3 then apply rule post-processing.

    - Token classification head → per-token BIO field labels (assistive)
    - seq_head.pt (if present) → document TYPE (SF08 / Accomplishment / Accreditation)
    - Completeness / attachments / signatures still come from rule post-processing
    """
    try:
        import torch
        from PIL import Image

        processor = get_processor()
        model = get_model()
        seq_head = get_seq_head()
        settings = get_settings()

        img = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        img_w, img_h = img.size

        words = [t.text for t in tokens]
        boxes = [t.normalized_bbox(img_w, img_h) for t in tokens]

        encoding = processor(
            img,
            words,
            boxes=boxes,
            return_tensors="pt",
            truncation=True,
            padding="max_length",
            max_length=512,
        )

        device = next(model.parameters()).device
        encoding = {k: v.to(device) for k, v in encoding.items()}

        with torch.no_grad():
            outputs = model(**encoding, output_hidden_states=True)

        # Document-TYPE classification via dual head (matches finetune.py)
        type_confidence = 0.0
        predicted_type = document_type.upper()
        type_mismatch = False

        if seq_head is not None and outputs.hidden_states is not None:
            cls_hidden = outputs.hidden_states[-1][:, 0, :]
            seq_logits = seq_head(cls_hidden)
            probs = torch.softmax(seq_logits, dim=-1)[0]
            pred_idx = int(torch.argmax(probs).item())
            type_confidence = float(probs[pred_idx].item())
            predicted_type = DOC_TYPE_CLASSES[pred_idx] if pred_idx < len(DOC_TYPE_CLASSES) else predicted_type
            type_mismatch = predicted_type != document_type.upper()
        else:
            # No seq head — rely on rules; token logits are BIO labels, not doc quality classes
            type_confidence = 0.5

        # Field / attachment completeness from rules (authoritative for MVP)
        rule_result = _rule_based_inference(document_type, tokens, image_bytes, attachments, rules)

        if type_mismatch:
            doc_class = DOC_CLASS_STRUCTURAL
            confidence = type_confidence
            requires_human = True
            rule_result.consistency_issues.append({
                "code": "DOC_TYPE_MISMATCH",
                "description": (
                    f"Declared type '{document_type}' but model predicted '{predicted_type}' "
                    f"(confidence {type_confidence:.2f})"
                ),
                "severity": "error",
            })
        else:
            doc_class = rule_result.document_class
            # Blend rule confidence with type confidence when available
            confidence = (
                0.6 * rule_result.confidence + 0.4 * type_confidence
                if seq_head is not None
                else rule_result.confidence
            )
            requires_human = (
                confidence < settings.human_review_confidence
                or rule_result.requires_human_review
                or doc_class == DOC_CLASS_HUMAN_REVIEW
            )

        return LayoutLmResult(
            document_class=doc_class,
            confidence=confidence,
            requires_human_review=requires_human,
            fields=rule_result.fields,
            attachments=rule_result.attachments,
            consistency_issues=rule_result.consistency_issues,
            model_version=get_model_version(),
            inference_path="neural",
        )

    except Exception as exc:
        logger.error("Neural inference failed, falling back to rules: %s", exc)
        result = _rule_based_inference(document_type, tokens, image_bytes, attachments, rules)
        result.inference_path = "rule_based_fallback"
        return result


# ─── path B: rule-based ─────────────────────────────────────────────────────

def _rule_based_inference(
    document_type: str,
    tokens: list[OcrToken],
    image_bytes: bytes,
    attachments: list[str],
    rules: DocumentTypeRule,
) -> LayoutLmResult:
    """
    Keyword-matching rule engine that validates documents without a trained model.
    This is the production-ready fallback used before fine-tuning is complete.
    """
    full_text_lower = " ".join(t.text for t in tokens).lower()
    low_conf_tokens = {t.text.lower() for t in tokens if t.low_confidence}

    # 1.2.3.2 Field completeness validation
    field_results = _validate_fields(rules.fields, full_text_lower, low_conf_tokens)

    # 1.2.3.6 Signature presence
    sig_result = detect_signatures(image_bytes, num_signature_zones=2)
    sig_fields = _build_signature_fields(rules.fields, sig_result)
    field_results.extend(sig_fields)

    # 1.2.3.3 Format compliance
    format_issues = _validate_format_compliance(rules.code, full_text_lower, tokens)

    # 1.2.3.4 Conditional field requirements
    conditional_results = _validate_conditional_fields(rules.fields, full_text_lower)
    field_results.extend(conditional_results)

    # 1.2.3.5 Missing attachments detection
    attachment_results = _validate_attachments(rules.attachments, attachments, full_text_lower)

    # 1.2.3.7 Cross-field consistency
    consistency_issues = _validate_cross_field_consistency(rules.code, full_text_lower, tokens)
    consistency_issues.extend(format_issues)

    # Aggregate document-level class
    doc_class, confidence = _compute_document_class(field_results, attachment_results, consistency_issues)
    requires_human = (
        confidence < get_settings().human_review_confidence
        or any(r["label"] == LABEL_ILLEGIBLE for r in field_results)
    )

    return LayoutLmResult(
        document_class=doc_class,
        confidence=confidence,
        requires_human_review=requires_human,
        fields=[_fv_to_dict(f) for f in field_results],
        attachments=[_av_to_dict(a) for a in attachment_results],
        consistency_issues=[_ci_to_dict(c) for c in consistency_issues],
        model_version=get_model_version(),
        inference_path="rule_based",
    )


# ─── 1.2.3.2 field completeness ─────────────────────────────────────────────

def _validate_fields(
    fields: list[FieldRule],
    full_text_lower: str,
    low_conf_tokens: set[str],
) -> list[FieldValidation]:
    """Keyword-based field presence check."""
    results: list[FieldValidation] = []

    for f in fields:
        if not f.mandatory:
            continue
        if f.condition != ConditionType.ALWAYS:
            continue  # handled in conditional step

        matched, match_text, match_conf = _find_field_in_text(f, full_text_lower, low_conf_tokens)

        if not matched:
            label = LABEL_MISSING
        elif match_conf is not None and match_conf < 0:
            label = LABEL_ILLEGIBLE
        else:
            label = LABEL_PRESENT

        results.append(FieldValidation(
            name=f.key,
            label=label,
            ocr_confidence=match_conf,
            matched_text=match_text,
        ))

    return results


def _find_field_in_text(
    f: FieldRule,
    full_text_lower: str,
    low_conf_tokens: set[str],
) -> tuple[bool, str | None, float | None]:
    """
    Check if any keyword for this field appears in the OCR text.
    Returns (found, matched_keyword, avg_confidence_if_low_conf).
    """
    search_terms = f.keywords + f.aliases + [f.key.lower()]

    for kw in search_terms:
        if kw.lower() in full_text_lower:
            # Check if the keyword region has low confidence tokens
            kw_words = kw.lower().split()
            low_match = any(w in low_conf_tokens for w in kw_words)
            conf = -1.0 if low_match else None
            return True, kw, conf

    return False, None, None


# ─── 1.2.3.6 signature fields ───────────────────────────────────────────────

def _build_signature_fields(
    fields: list[FieldRule],
    sig_result,
) -> list[FieldValidation]:
    sig_fields = [f for f in fields if "signature" in f.key.lower()]
    results: list[FieldValidation] = []

    label = sig_result.label   # SIGNED or UNSIGNED

    for f in sig_fields:
        results.append(FieldValidation(
            name=f.key,
            label=label,
            ocr_confidence=sig_result.confidence * 100,
        ))

    return results


# ─── 1.2.3.3 format compliance ──────────────────────────────────────────────

DATE_PATTERN = re.compile(
    r"\b(\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4}|\w+ \d{1,2},?\s*\d{4})\b"
)
NUMBER_PATTERN = re.compile(r"\b\d+\b")


def _validate_format_compliance(
    doc_type: str,
    full_text_lower: str,
    tokens: list[OcrToken],
) -> list[ConsistencyIssue]:
    issues: list[ConsistencyIssue] = []

    # All doc types: date fields must contain a recognisable date format
    if doc_type in ("SF08", "ACCOMPLISHMENT"):
        if not DATE_PATTERN.search(full_text_lower):
            issues.append(ConsistencyIssue(
                code="FORMAT_NO_DATE",
                description="No recognisable date pattern found in document.",
                severity="error",
            ))

    # SF08 / Accomplishment: participant count must be numeric
    if doc_type in ("SF08", "ACCOMPLISHMENT"):
        participant_context = _extract_context(full_text_lower, ["participants", "attendees", "actual"], window=40)
        if participant_context and not NUMBER_PATTERN.search(participant_context):
            issues.append(ConsistencyIssue(
                code="FORMAT_PARTICIPANT_NOT_NUMERIC",
                description="Expected numeric value near 'participants' field.",
                severity="warning",
            ))

    return issues


def _extract_context(text: str, keywords: list[str], window: int = 60) -> str | None:
    for kw in keywords:
        idx = text.find(kw)
        if idx != -1:
            start = max(0, idx - window)
            end = min(len(text), idx + len(kw) + window)
            return text[start:end]
    return None


# ─── 1.2.3.4 conditional field requirements ─────────────────────────────────

def _validate_conditional_fields(
    fields: list[FieldRule],
    full_text_lower: str,
) -> list[FieldValidation]:
    results: list[FieldValidation] = []
    conditional_fields = [f for f in fields if f.condition != ConditionType.ALWAYS]

    # Infer IF_MINORS from text
    minors_mentioned = any(kw in full_text_lower for kw in ["minor", "below 18", "guardian", "parent"])

    for f in conditional_fields:
        if f.condition == ConditionType.IF_MINORS:
            if minors_mentioned:
                # Condition is triggered — treat as mandatory
                found, match_text, conf = _find_field_in_text(f, full_text_lower, set())
                label = LABEL_PRESENT if found else LABEL_MISSING
            else:
                label = LABEL_CONDITIONALLY_REQ

            results.append(FieldValidation(
                name=f.key,
                label=label,
                matched_text=None,
            ))

    return results


# ─── 1.2.3.5 missing attachments ────────────────────────────────────────────

def _validate_attachments(
    attachment_rules: list[AttachmentRule],
    submitted_attachment_types: list[str],
    full_text_lower: str,
) -> list[AttachmentValidation]:
    submitted_set = {a.upper() for a in submitted_attachment_types}
    results: list[AttachmentValidation] = []

    minors_mentioned = any(kw in full_text_lower for kw in ["minor", "below 18", "guardian", "parent"])

    for rule in attachment_rules:
        # Evaluate condition
        if rule.condition == ConditionType.IF_MINORS and not minors_mentioned:
            results.append(AttachmentValidation(
                type=rule.key,
                label=LABEL_CONDITIONALLY_REQ,
                is_conditional=True,
                condition=rule.condition.value,
            ))
            continue

        is_present = rule.key.upper() in submitted_set

        if is_present:
            label = LABEL_ATTACH_PRESENT
        elif rule.mandatory:
            label = LABEL_ATTACH_MISSING
        else:
            label = LABEL_ATTACH_PRESENT   # optional, not submitted → still OK

        results.append(AttachmentValidation(
            type=rule.key,
            label=label,
            is_conditional=rule.condition != ConditionType.ALWAYS,
            condition=rule.condition.value if rule.condition != ConditionType.ALWAYS else None,
        ))

    return results


# ─── 1.2.3.7 cross-field consistency ────────────────────────────────────────

def _validate_cross_field_consistency(
    doc_type: str,
    full_text_lower: str,
    tokens: list[OcrToken],
) -> list[ConsistencyIssue]:
    issues: list[ConsistencyIssue] = []

    # Accomplishment Report must reference an SF08 number or activity title
    if doc_type == "ACCOMPLISHMENT":
        has_sf08_ref = any(kw in full_text_lower for kw in ["sf08", "sf-08", "sf 08", "form 8", "request to conduct"])
        if not has_sf08_ref:
            issues.append(ConsistencyIssue(
                code="CROSS_DOC_NO_SF08_REF",
                description="Accomplishment Report does not reference originating SF08/activity form.",
                severity="warning",
            ))

    # All types: organisation name should appear consistently
    org_mentions = _count_organization_mentions(full_text_lower)
    if org_mentions == 0:
        issues.append(ConsistencyIssue(
            code="CROSS_FIELD_NO_ORG_NAME",
            description="Organisation name not detected anywhere in the document.",
            severity="error",
        ))

    return issues


def _count_organization_mentions(text: str) -> int:
    org_patterns = ["organization", "org.", "club", "society", "association", "league"]
    return sum(1 for p in org_patterns if p in text)


# ─── document-level classification ──────────────────────────────────────────

def _compute_document_class(
    field_results: list[FieldValidation],
    attachment_results: list[AttachmentValidation],
    consistency_issues: list[ConsistencyIssue],
) -> tuple[str, float]:
    """
    Aggregate individual results into a final document-level class and confidence.
    """
    missing_fields    = sum(1 for f in field_results if f.label == LABEL_MISSING)
    unsigned_fields   = sum(1 for f in field_results if f.label == LABEL_UNSIGNED)
    illegible_fields  = sum(1 for f in field_results if f.label == LABEL_ILLEGIBLE)
    missing_attachments = sum(1 for a in attachment_results if a.label == LABEL_ATTACH_MISSING)
    error_issues      = sum(1 for c in consistency_issues if c.severity == "error")

    total_required    = max(1, sum(
        1 for f in field_results if f.label in (LABEL_PRESENT, LABEL_MISSING, LABEL_SIGNED, LABEL_UNSIGNED)
    ))
    present_count     = sum(1 for f in field_results if f.label in (LABEL_PRESENT, LABEL_SIGNED))

    base_confidence = present_count / total_required

    if missing_fields > 0 or illegible_fields > 0:
        doc_class = DOC_CLASS_INCOMPLETE
        confidence = max(0.1, base_confidence - 0.15 * missing_fields)
    elif missing_attachments > 0:
        doc_class = DOC_CLASS_ATTACH_MISS
        confidence = max(0.1, base_confidence - 0.10 * missing_attachments)
    elif unsigned_fields > 0:
        doc_class = DOC_CLASS_STRUCTURAL
        confidence = max(0.1, base_confidence - 0.20)
    elif error_issues > 0:
        doc_class = DOC_CLASS_INCOMPLETE
        confidence = max(0.1, base_confidence - 0.10 * error_issues)
    else:
        doc_class = DOC_CLASS_VALID
        confidence = min(0.99, base_confidence)

    return doc_class, round(confidence, 4)


# ─── serializers ────────────────────────────────────────────────────────────

def _fv_to_dict(f: FieldValidation) -> dict:
    d = {"name": f.name, "label": f.label}
    if f.ocr_confidence is not None:
        d["ocr_confidence"] = round(f.ocr_confidence, 2)
    if f.matched_text:
        d["matched_text"] = f.matched_text
    return d


def _av_to_dict(a: AttachmentValidation) -> dict:
    d = {"type": a.type, "label": a.label}
    if a.is_conditional:
        d["is_conditional"] = True
    if a.condition:
        d["condition"] = a.condition
    return d


def _ci_to_dict(c: ConsistencyIssue) -> dict:
    return {"code": c.code, "description": c.description, "severity": c.severity}
