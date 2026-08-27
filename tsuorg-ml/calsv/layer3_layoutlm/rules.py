"""
Document-specific field and attachment rules for TSU SOU documents.

Used by Layer 3 as:
  1. Expected field list per document type → validates PRESENT / MISSING / ILLEGIBLE
  2. Attachment requirement list → ATTACHMENT_PRESENT / ATTACHMENT_MISSING
  3. Conditional rules (e.g. ParentConsent only when minors involved)
  4. Cross-field consistency checks (dates, org name)
  5. Format compliance (date format, numeric fields)

These rules represent institutional domain knowledge from the paper (Scope §4.3.1).
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum


class ConditionType(str, Enum):
    ALWAYS = "always"
    IF_MINORS = "if_minors"          # SF08 with minors → ParentConsent required
    IF_OFFCAMPUS = "if_offcampus"    # off-campus activity → additional venue doc


@dataclass
class FieldRule:
    key: str                             # machine key (used in CALSV output)
    display_name: str                    # human-readable
    mandatory: bool = True
    condition: ConditionType = ConditionType.ALWAYS
    # Keywords that must appear near this field in OCR output
    keywords: list[str] = field(default_factory=list)
    # If non-empty, at least one must appear
    aliases: list[str] = field(default_factory=list)


@dataclass
class AttachmentRule:
    key: str
    display_name: str
    mandatory: bool = True
    condition: ConditionType = ConditionType.ALWAYS


@dataclass
class DocumentTypeRule:
    code: str                            # SF08 | ACCOMPLISHMENT | ACCREDITATION
    display_name: str
    fields: list[FieldRule] = field(default_factory=list)
    attachments: list[AttachmentRule] = field(default_factory=list)
    # Keys from another doc type that must be cross-referenced
    cross_ref_keys: list[str] = field(default_factory=list)


# ─── Rule Definitions ───────────────────────────────────────────────────────

SF08_RULES = DocumentTypeRule(
    code="SF08",
    display_name="Request to Conduct an Activity (SF08)",
    fields=[
        FieldRule("ActivityTitle",         "Activity Title",           keywords=["activity", "event", "title", "name"]),
        FieldRule("ActivityDate",          "Activity Date",            keywords=["date", "day", "schedule"]),
        FieldRule("ActivityVenue",         "Activity Venue",           keywords=["venue", "place", "location"]),
        FieldRule("ActivityObjectives",    "Activity Objectives",      keywords=["objectives", "goals", "purpose"]),
        FieldRule("ExpectedParticipants",  "Expected No. of Participants", keywords=["participants", "attendees", "expected"]),
        FieldRule("OrganizationName",      "Organization Name",        keywords=["organization", "org", "club"]),
        FieldRule("OfficerSignature",      "Officer-in-Charge Signature", keywords=["signature", "signed", "officer"]),
        FieldRule("AdviserSignature",      "Adviser Signature",        keywords=["adviser", "advisor", "faculty"]),
        FieldRule("ActivityType",          "Type of Activity",         keywords=["type", "category", "classification"], mandatory=False),
        FieldRule("MinorsInvolved",        "Minors Involved (Y/N)",    keywords=["minor", "below 18", "guardian"], mandatory=False),
    ],
    attachments=[
        AttachmentRule("ActivityProposal", "Activity Proposal",         mandatory=True),
        AttachmentRule("ProgramMatrix",    "Program of Activities / Matrix", mandatory=True),
        AttachmentRule("VenueApproval",    "Venue Approval / Permit",   mandatory=True),
        AttachmentRule("EndorsementLetter","Endorsement / Supporting Letter", mandatory=True),
        AttachmentRule("ParentConsent",    "Parent Consent Form",
                       mandatory=True,
                       condition=ConditionType.IF_MINORS),
    ],
)

ACCOMPLISHMENT_RULES = DocumentTypeRule(
    code="ACCOMPLISHMENT",
    display_name="Accomplishment Report",
    fields=[
        FieldRule("EventTitle",        "Event / Activity Title",     keywords=["activity", "event", "title"]),
        FieldRule("EventDate",         "Date of Activity",           keywords=["date", "conducted", "held"]),
        FieldRule("EventVenue",        "Venue",                      keywords=["venue", "place", "held at"]),
        FieldRule("Objectives",        "Objectives",                 keywords=["objectives", "goals"]),
        FieldRule("Accomplishments",   "Accomplishments / Summary",  keywords=["accomplishment", "result", "outcome", "summary"]),
        FieldRule("AttendeeCount",     "Actual No. of Participants", keywords=["participants", "attendees", "attended", "actual"]),
        FieldRule("OfficerSignature",  "Officer-in-Charge Signature", keywords=["signature", "signed", "officer"]),
        FieldRule("AdviserSignature",  "Adviser Signature",          keywords=["adviser", "advisor", "noted"]),
        FieldRule("BudgetSummary",     "Budget Summary",             keywords=["budget", "expense", "total", "amount"], mandatory=False),
    ],
    attachments=[
        AttachmentRule("ActivityPhotos",       "Activity Documentation / Photos", mandatory=True),
        AttachmentRule("AttendanceSheet",      "Attendance Sheet(s)",             mandatory=True),
        AttachmentRule("FinancialLiquidation", "Financial Liquidation Report",    mandatory=True),
        AttachmentRule("ApprovedSF08",         "Approved SF08 Form",              mandatory=True),
        AttachmentRule("Certificates",         "Certificates (if any)",           mandatory=False),
        AttachmentRule("EvidenceOfImpl",       "Evidence of Implementation",      mandatory=False),
    ],
    cross_ref_keys=["SF08"],   # EventTitle/Date should match originating SF08
)

ACCREDITATION_RULES = DocumentTypeRule(
    code="ACCREDITATION",
    display_name="Accreditation / Application Form",
    fields=[
        FieldRule("OrganizationName",    "Organization Name",           keywords=["organization", "org", "club", "society"]),
        FieldRule("College",             "College / Department",        keywords=["college", "department", "school"]),
        FieldRule("OfficerList",         "List of Officers",            keywords=["officers", "president", "secretary", "treasurer"]),
        FieldRule("MemberCount",         "Number of Members",           keywords=["members", "membership", "total"]),
        FieldRule("OrganizationObjectives", "Organizational Objectives/Mission", keywords=["mission", "objectives", "vision", "purpose"]),
        FieldRule("AdviserName",         "Faculty Adviser",             keywords=["adviser", "advisor", "faculty"]),
        FieldRule("AdviserSignature",    "Adviser Signature",           keywords=["signature", "adviser", "signed"]),
        FieldRule("PresidentSignature",  "President Signature",         keywords=["president", "signature", "signed"]),
        FieldRule("AcademicYear",        "Academic Year",               keywords=["academic year", "A.Y.", "school year"]),
    ],
    attachments=[
        AttachmentRule("Constitution",        "Constitution and By-Laws",             mandatory=True),
        AttachmentRule("OrgProfile",          "Organization Profile",                 mandatory=True),
        AttachmentRule("OfficerListDoc",      "List of Officers (formal document)",   mandatory=True),
        AttachmentRule("MembershipList",      "Membership List",                      mandatory=True),
        AttachmentRule("AnnualPlan",          "General Annual Plan of Activities",    mandatory=True),
        AttachmentRule("AdviserEndorsement",  "Adviser Endorsement Letter",           mandatory=True),
    ],
)

# Registry by document type code
RULES_REGISTRY: dict[str, DocumentTypeRule] = {
    "SF08":           SF08_RULES,
    "ACCOMPLISHMENT": ACCOMPLISHMENT_RULES,
    "ACCREDITATION":  ACCREDITATION_RULES,
}


def get_rules(document_type: str) -> DocumentTypeRule | None:
    return RULES_REGISTRY.get(document_type.upper())


def get_mandatory_fields(document_type: str) -> list[FieldRule]:
    rules = get_rules(document_type)
    if rules is None:
        return []
    return [f for f in rules.fields if f.mandatory]


def get_mandatory_attachments(document_type: str) -> list[AttachmentRule]:
    rules = get_rules(document_type)
    if rules is None:
        return []
    return [a for a in rules.attachments if a.mandatory]
