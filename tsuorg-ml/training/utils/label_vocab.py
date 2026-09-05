"""Token / document label vocabulary + alias loading for dataset prep."""

from __future__ import annotations

from pathlib import Path
from typing import Any

import yaml

# Flat token labels (no BIO). See prepare_dataset / DATASET.md.
TOKEN_LABELS = [
    "O",
    # SF08
    "FORM_TITLE",
    "DATE_PROPOSAL",
    "ORG_NAME",
    "OBJECTIVES",
    "ACTIVITY_TITLE",
    "ACTIVITY_DATE",
    "VENUE",
    "PARTICIPANTS",
    "ACTIVITY_MODE",
    "ORG_OFFICER_SIGNATURE",
    "ADVISER_SIGNATURE",
    "SAS_SIGNATURE",
    "SIGNATURE",
    # Accomplishment Report (TSU-SOU-SF-06)
    "LOGO_HEADER",
    "REPORT_TITLE",
    "SEMESTER",
    "ACADEMIC_YEAR",
    "SUMMARY_TITLE",
    "ACTIVITY_NO",
    "ACTIVITY_NAME",
    "INVOLVEMENT",
    "LEVEL",
    "EXTENT_OF_BENEFITS",
    "NARRATIVE",
    "SECRETARY_SIGNATURE",
    "PRESIDENT_SIGNATURE",
    # Supporting documents
    "HEADER",
    "PHOTO_DOCUMENTATION",
    "PICTURES",
    "CERTIFICATE_TITLE",
    "RECIPIENT",
    "PURPOSE",
    "DATE_AND_VENUE",
    "SIGNATURES",
    "TITLE",
    "DATE",
    "NAME",
    "TEAM",
    # Accreditation (forward-compat)
    "COLLEGE",
    "AY",
    "OFFICER_LIST",
]

LABEL2ID = {l: i for i, l in enumerate(TOKEN_LABELS)}
ID2LABEL = {i: l for l, i in LABEL2ID.items()}
KNOWN_TAGS = {l for l in TOKEN_LABELS if l != "O"}

DOC_CLASSES = {
    "SF08": 0,
    "ACCOMPLISHMENT": 1,
    "ACCREDITATION": 2,
    # Supporting subtypes (forward-compat with label_studio_config_supporting.xml)
    "PHOTO_DOCUMENTATION": 3,
    "CERTIFICATE": 4,
    "ATTENDANCE": 5,
}

DEFAULT_ALIASES_PATH = Path(__file__).resolve().parents[1] / "configs" / "label_aliases.yaml"


def load_label_aliases(path: Path | None = None) -> dict[str, Any]:
    path = path or DEFAULT_ALIASES_PATH
    if not path.exists():
        return {"aliases": {}, "exclude": []}
    raw = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    aliases = {str(k).upper(): str(v).upper() for k, v in (raw.get("aliases") or {}).items()}
    exclude = {str(x).upper() for x in (raw.get("exclude") or [])}
    return {"aliases": aliases, "exclude": exclude}


def resolve_token_label(
    raw_tag: str,
    *,
    aliases: dict[str, str],
    exclude: set[str],
) -> tuple[str | None, str]:
    """
    Map a Label Studio tag to a training label.

    Returns (label_or_None, status) where status is:
      ok | aliased | excluded | unknown | empty
    """
    tag = (raw_tag or "").strip().upper().replace(" ", "_")
    if not tag:
        return None, "empty"
    if tag in exclude:
        return None, "excluded"
    if tag in aliases:
        mapped = aliases[tag]
        if mapped not in KNOWN_TAGS and mapped != "O":
            return None, "unknown"
        return mapped, "aliased"
    if tag in KNOWN_TAGS:
        return tag, "ok"
    if tag == "O":
        return "O", "ok"
    return None, "unknown"
