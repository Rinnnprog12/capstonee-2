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
    # Keywords that must appear near this field in OCR output (used for presence detection)
    keywords: list[str] = field(default_factory=list)
    # If non-empty, at least one must appear
    aliases: list[str] = field(default_factory=list)
    # Multi-word label patterns as they appear verbatim on the physical form (used for value extraction).
    # Matched case-insensitively against the raw OCR text; the text AFTER the matching label + separator
    # is captured as the field value.  Leave empty for fields where keyword matching is sufficient.
    value_patterns: list[str] = field(default_factory=list)


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
        FieldRule("ActivityTitle",         "Activity Title",
                  keywords=["activity", "event", "title", "name"],
                  value_patterns=["ACTIVITY TITLE", "NAME OF ACTIVITY", "TITLE OF ACTIVITY", "EVENT TITLE"]),
        FieldRule("ActivityDate",          "Activity Date",
                  keywords=["date", "day", "schedule"],
                  value_patterns=["ACTIVITY DATE", "DATE OF ACTIVITY", "DATE AND TIME"]),
        FieldRule("ActivityVenue",         "Activity Venue",
                  keywords=["venue", "place", "location"],
                  value_patterns=["ACTIVITY VENUE", "VENUE OF ACTIVITY", "PLACE OF ACTIVITY"]),
        FieldRule("ActivityObjectives",    "Activity Objectives",      keywords=["objectives", "goals", "purpose"]),
        FieldRule("ExpectedParticipants",  "Expected No. of Participants",
                  keywords=["participants", "attendees", "expected"],
                  value_patterns=["EXPECTED PARTICIPANTS", "EXPECTED NO", "NO. OF PARTICIPANTS",
                                  "NUMBER OF PARTICIPANTS", "EXPECTED NUMBER"]),
        FieldRule("OrganizationName",      "Organization Name",        keywords=["organization", "org", "club"]),
        FieldRule("OfficerSignature",      "Officer-in-Charge Signature", keywords=["signature", "signed", "officer", "president", "sincerely"]),
        FieldRule("AdviserSignature",      "Adviser Signature",        keywords=["adviser", "advisor", "faculty", "dean"]),
        FieldRule("SasSignature",          "SAS / SOU Approval Signature", keywords=["approved", "director", "student affairs", "unit head", "asst. director"]),
        FieldRule("ActivityType",          "Type of Activity",         keywords=["type", "category", "classification"], mandatory=False),
        FieldRule("MinorsInvolved",        "Minors Involved (Y/N)",    keywords=["minor", "below 18", "guardian"], mandatory=False),
    ],
    attachments=[
        AttachmentRule("ActivityProposal", "Activity Proposal",         mandatory=True),
        # Supporting files are often already inside the SF08 packet. CALSV
        # OCRs the primary form only; these slots stay optional extras.
        AttachmentRule("ProgramMatrix",    "Program of Activities / Matrix", mandatory=False),
        AttachmentRule("VenueApproval",    "Venue Approval / Permit",   mandatory=False),
        AttachmentRule("EndorsementLetter","Endorsement / Supporting Letter", mandatory=False),
        AttachmentRule("ParentConsent",    "Parent Consent Form",
                       mandatory=False,
                       condition=ConditionType.IF_MINORS),
    ],
)

ACCOMPLISHMENT_RULES = DocumentTypeRule(
    code="ACCOMPLISHMENT",
    display_name="Accomplishment Report",
    fields=[
        FieldRule("OrganizationName",  "Organization Name",          keywords=["organization", "society", "circle", "guild"]),
        FieldRule("ReportTitle",       "Report Title",               keywords=["accomplishment report"]),
        FieldRule("Semester",          "Semester",                   keywords=["semester", "1st", "2nd"]),
        FieldRule("AcademicYear",      "Academic Year",              keywords=["a.y.", "academic year", "ay"]),
        FieldRule("EventTitle",        "Name of Activity",           keywords=["name of activity", "activity", "event"]),
        FieldRule("ActivityDate",      "Date of Activity",           keywords=["date", "conducted", "held"]),
        FieldRule("ActivityVenue",     "Venue",                      keywords=["venue", "place", "held at"]),
        FieldRule("Involvement",       "Involvement",                keywords=["involvement", "organizer", "participant"]),
        FieldRule("ActivityLevel",     "Level",                      keywords=["level", "international", "national", "regional", "local"]),
        FieldRule("ExtentOfBenefits",  "Extent of Benefits",         keywords=["extent", "benefits", "departments", "community"]),
        FieldRule("Accomplishments",   "Narrative / Objectives",     keywords=["narrative", "objective", "accomplishment", "summary"]),
        FieldRule("OfficerSignature",  "Secretary / Prepared by",    keywords=["prepared by", "secretary", "signature"]),
        FieldRule("PresidentSignature","President / Reviewed by",    keywords=["reviewed by", "president", "governor"]),
        FieldRule("AdviserSignature",  "Adviser Signature",          keywords=["approved", "adviser", "advisor"]),
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
