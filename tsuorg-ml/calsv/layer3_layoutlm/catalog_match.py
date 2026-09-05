"""
Match LayoutLM/OCR org + adviser spans to the live TSU org catalog.

The backend sends Organizations / Colleges / adviser names from MySQL.
We canonicalize ORG_NAME (and adviser text) against that list so
"Programmers Den" on an SF08 maps to PD / CCS instead of a raw OCR fragment.
"""

from __future__ import annotations

import re
from difflib import SequenceMatcher
from typing import Any

_MATCH_FLOOR = 0.72


def apply_catalog_match(
    fields: list[dict],
    issues: list[dict],
    full_text: str,
    catalog: dict[str, Any] | None,
) -> tuple[list[dict], list[dict], dict[str, Any]]:
    meta: dict[str, Any] = {}
    if not catalog:
        return fields, issues, meta

    orgs = [_normalize_org(o) for o in (catalog.get("organizations") or []) if o]
    if not orgs:
        return fields, issues, meta

    submitted_acronym = _clean(catalog.get("submitted_acronym") or "")
    submitted_name = catalog.get("submitted_name") or ""
    submitted_college = _clean(catalog.get("submitted_college_code") or "")
    submitted_adviser = catalog.get("submitted_adviser_name") or ""

    org_field = _field(fields, "OrganizationName")
    extracted = (org_field.get("matched_text") if org_field else None) or ""
    hit, score = _best_org(
        extracted, orgs,
        submitted_acronym=submitted_acronym,
        submitted_name=submitted_name,
    )
    if hit is None:
        hit, score = _best_org(
            full_text, orgs,
            whole_document=True,
            submitted_acronym=submitted_acronym,
            submitted_name=submitted_name,
        )

    if hit is None:
        if extracted.strip():
            issues.append({
                "code": "ORG_NOT_IN_CATALOG",
                "description": (
                    f"OCR organization '{extracted.strip()}' does not match a registered "
                    "student organization."
                ),
                "severity": "error",
            })
        return fields, issues, meta

    canonical = f"{hit['name']} ({hit['college_code']})" if hit.get("college_code") else hit["name"]
    if hit.get("acronym"):
        canonical = f"{hit['name']} ({hit['acronym']})"
        if hit.get("college_code"):
            canonical = f"{hit['name']} ({hit['acronym']}, {hit['college_code']})"

    fields = _upsert_field(
        fields,
        "OrganizationName",
        label="PRESENT",
        matched_text=canonical,
        extra={
            "catalog_acronym": hit.get("acronym"),
            "college_code": hit.get("college_code"),
            "college_name": hit.get("college_name"),
        },
    )
    if hit.get("college_code") or hit.get("college_name"):
        fields = _upsert_field(
            fields,
            "College",
            label="PRESENT",
            matched_text=hit.get("college_name") or hit.get("college_code") or "",
            extra={"college_code": hit.get("college_code"), "college_name": hit.get("college_name")},
        )

    meta["catalog_organization"] = {
        "name": hit["name"],
        "acronym": hit.get("acronym"),
        "college_code": hit.get("college_code"),
        "college_name": hit.get("college_name"),
        "adviser_name": hit.get("adviser_name"),
        "score": round(min(score, 1.0), 3),
    }

    if submitted_acronym and _clean(hit.get("acronym") or "") not in {submitted_acronym, ""}:
        if not _names_match(extracted or hit["name"], submitted_name) and not _names_match(
            hit["name"], submitted_name
        ):
            issues.append({
                "code": "ORG_SUBMITTED_MISMATCH",
                "description": (
                    f"Form organization '{hit['name']}' does not match the submitted "
                    f"organization '{submitted_name or submitted_acronym}'."
                ),
                "severity": "error",
            })

    hit_college = _clean(hit.get("college_code") or "")
    if submitted_college and hit_college and submitted_college != hit_college:
        issues.append({
            "code": "COLLEGE_MISMATCH",
            "description": (
                f"Detected college '{hit.get('college_code')}' ({hit.get('college_name')}) "
                f"does not match the submitted organization college '{submitted_college}'."
            ),
            "severity": "warning",
        })

    adviser_field = _field(fields, "AdviserName") or _field(fields, "AdviserSignature")
    adviser_ocr = (adviser_field.get("matched_text") if adviser_field else None) or ""
    adviser_candidates = [n for n in (hit.get("adviser_name"), submitted_adviser) if n]
    if adviser_candidates:
        best_adv, adv_score = (None, 0.0)
        if _looks_like_person_name(adviser_ocr):
            best_adv, adv_score = _best_name(adviser_ocr, adviser_candidates)
        if not best_adv or adv_score < _MATCH_FLOOR:
            for cand in adviser_candidates:
                phrase_score = _adviser_in_text(cand, full_text)
                if phrase_score > adv_score:
                    best_adv, adv_score = cand, phrase_score
        if best_adv and adv_score >= _MATCH_FLOOR:
            meta["catalog_adviser"] = {"name": best_adv, "score": round(adv_score, 3)}
            fields = _upsert_field(
                fields,
                "AdviserName",
                label="PRESENT",
                matched_text=best_adv,
                extra={},
            )
        elif _looks_like_person_name(adviser_ocr) and submitted_adviser and not _names_match(
            adviser_ocr, submitted_adviser
        ):
            issues.append({
                "code": "ADVISER_MISMATCH",
                "description": (
                    f"Adviser on the form ('{adviser_ocr.strip()}') does not match "
                    f"'{submitted_adviser}' for this organization."
                ),
                "severity": "warning",
            })

    return fields, issues, meta


def _normalize_org(raw: Any) -> dict[str, str]:
    if isinstance(raw, dict):
        name = str(raw.get("name") or "").strip()
        acronym = str(raw.get("acronym") or "").strip()
        return {
            "name": name,
            "acronym": acronym,
            "college_code": str(raw.get("college_code") or "").strip(),
            "college_name": str(raw.get("college_name") or "").strip(),
            "adviser_name": str(raw.get("adviser_name") or "").strip(),
            "aliases": [str(a) for a in (raw.get("aliases") or []) if str(a).strip()],
        }
    return {"name": str(raw), "acronym": "", "college_code": "", "college_name": "", "adviser_name": "", "aliases": []}


def _org_needles(org: dict[str, str]) -> list[str]:
    values = [org["name"], org["acronym"], *org.get("aliases", [])]
    extra: list[str] = []
    for v in values:
        extra.append(v.replace("-", " "))
        extra.append(v.replace("'", ""))
        if re.search(r"\bsc\b", v, re.I) or v.upper().endswith("-SC"):
            extra.append(re.sub(r"\bsc\b", "student council", v, flags=re.I))
            extra.append(re.sub(r"-?sc$", " student council", v, flags=re.I))
    return [x for x in values + extra if x.strip()]


def _best_org(
    text: str,
    orgs: list[dict[str, str]],
    *,
    whole_document: bool = False,
    submitted_acronym: str = "",
    submitted_name: str = "",
) -> tuple[dict[str, str] | None, float]:
    hay = _clean(text)
    if not hay:
        return None, 0.0

    best: dict[str, str] | None = None
    best_score = 0.0
    submitted_acronym = _clean(submitted_acronym)
    submitted_name = _clean(submitted_name)
    for org in orgs:
        org_score = 0.0
        for needle in _org_needles(org):
            n = _clean(needle)
            if not n:
                continue
            if whole_document:
                if len(n) < 3 and n != submitted_acronym:
                    continue
                if re.search(rf"\b{re.escape(n)}\b", hay):
                    # Prefer full names over short acronym hits.
                    score = 0.80 + min(len(n), 24) * 0.008
                else:
                    score = 0.0
            else:
                score = _similarity(hay, n)
                if n in hay or hay in n:
                    score = max(score, 0.9)
            if score > org_score:
                org_score = score
        if submitted_acronym and _clean(org.get("acronym") or "") == submitted_acronym:
            org_score += 0.06
        if submitted_name and _names_match(org["name"], submitted_name):
            org_score += 0.06
        if org_score > best_score:
            best, best_score = org, org_score
    if best_score < _MATCH_FLOOR:
        return None, best_score
    return best, best_score


def _best_name(text: str, names: list[str]) -> tuple[str | None, float]:
    hay = _clean(text)
    best = None
    best_score = 0.0
    for name in names:
        score = _similarity(hay, _clean(name))
        if _last_name(name) and _last_name(name) in hay:
            score = max(score, 0.84)
        if score > best_score:
            best, best_score = name, score
    return best, best_score


def _adviser_in_text(name: str, text: str) -> float:
    n = _clean(name)
    hay = _clean(text)
    if not n or not hay:
        return 0.0
    if re.search(rf"\b{re.escape(n)}\b", hay):
        return 0.95
    parts = [p for p in n.split() if p not in {"dr", "prof", "mr", "ms", "mrs"}]
    if len(parts) >= 2:
        first, last = parts[0], parts[-1]
        if re.search(rf"\b{re.escape(first)}\b", hay) and re.search(rf"\b{re.escape(last)}\b", hay):
            return 0.86
    return 0.0


def _looks_like_person_name(text: str) -> bool:
    words = [w for w in _clean(text).split() if w not in {"dr", "prof", "mr", "ms", "mrs", "signed", "signature"}]
    if len(words) < 2:
        return False
    return all(w.isalpha() for w in words[:4])


def _names_match(a: str, b: str) -> bool:
    if not a or not b:
        return False
    return _similarity(_clean(a), _clean(b)) >= _MATCH_FLOOR or _last_name(a) == _last_name(b) != ""


def _last_name(name: str) -> str:
    parts = [p for p in _clean(name).split() if p not in {"dr", "prof", "mr", "ms", "mrs"}]
    return parts[-1] if parts else ""


def _similarity(a: str, b: str) -> float:
    if not a or not b:
        return 0.0
    if a == b:
        return 1.0
    return SequenceMatcher(None, a, b).ratio()


def _clean(text: str) -> str:
    t = text.lower().replace("'", "").replace("'", "")
    t = re.sub(r"[^a-z0-9]+", " ", t)
    return re.sub(r"\s+", " ", t).strip()


def _field(fields: list[dict], name: str) -> dict | None:
    return next((f for f in fields if f.get("name") == name), None)


def _upsert_field(
    fields: list[dict],
    name: str,
    *,
    label: str,
    matched_text: str,
    extra: dict[str, Any],
) -> list[dict]:
    out = []
    found = False
    for f in fields:
        if f.get("name") != name:
            out.append(f)
            continue
        row = dict(f)
        row["label"] = label
        row["matched_text"] = matched_text
        for k, v in extra.items():
            if v:
                row[k] = v
        out.append(row)
        found = True
    if not found:
        row = {"name": name, "label": label, "matched_text": matched_text}
        row.update({k: v for k, v in extra.items() if v})
        out.append(row)
    return out
