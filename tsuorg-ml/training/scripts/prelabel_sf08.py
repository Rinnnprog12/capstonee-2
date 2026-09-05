"""
prelabel_sf08.py — OCR + layout heuristics for TSU-SOU-SF-08 pre-annotations.

Reads PNGs from data/raw/images/sf08/, runs Tesseract, detects the standard
ACTIVITY | DATE | VENUE table plus body text and signature blocks, then writes
Label Studio import JSON with predictions for human review.

Usage:
  python -m training.scripts.prelabel_sf08
  python -m training.scripts.prelabel_sf08 --limit 20
  python -m training.scripts.prelabel_sf08 --images data/raw/images/sf08
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import uuid
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image

from calsv.layer2_ocr.ocr import OcrResult, OcrToken, extract_text

SF08_FIELDS = (
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
)

TABLE_HEADERS = {
    "ACTIVITY_TITLE": ("activity",),
    "ACTIVITY_DATE": ("date",),
    "VENUE": ("venue", "platform"),
    "PARTICIPANTS": ("participants", "participant"),
}


@dataclass
class Line:
    tokens: list[OcrToken]

    @property
    def text(self) -> str:
        return " ".join(t.text for t in self.tokens)

    @property
    def text_upper(self) -> str:
        return self.text.upper()

    @property
    def y(self) -> int:
        return min(t.y for t in self.tokens) if self.tokens else 0

    @property
    def x_min(self) -> int:
        return min(t.x for t in self.tokens) if self.tokens else 0

    @property
    def x_max(self) -> int:
        return max(t.x + t.w for t in self.tokens) if self.tokens else 0

    def bbox(self) -> tuple[int, int, int, int]:
        if not self.tokens:
            return (0, 0, 0, 0)
        x0 = min(t.x for t in self.tokens)
        y0 = min(t.y for t in self.tokens)
        x1 = max(t.x + t.w for t in self.tokens)
        y1 = max(t.y + t.h for t in self.tokens)
        return (x0, y0, x1, y1)


@dataclass
class FieldBox:
    label: str
    text: str
    bbox: tuple[int, int, int, int]  # x0, y0, x1, y1


@dataclass
class PrelabelReport:
    image: str
    fields_found: list[str] = field(default_factory=list)
    fields_missing: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)


def _group_lines(ocr: OcrResult) -> list[Line]:
    buckets: dict[tuple[int, int], list[OcrToken]] = {}
    for tok in ocr.tokens:
        buckets.setdefault((tok.block_num, tok.line_num), []).append(tok)
    lines = [Line(tokens=sorted(ts, key=lambda t: t.x)) for ts in buckets.values()]
    lines.sort(key=lambda ln: (ln.y, ln.x_min))
    return lines


def _merge_bbox(boxes: list[tuple[int, int, int, int]]) -> tuple[int, int, int, int]:
    if not boxes:
        return (0, 0, 0, 0)
    x0 = min(b[0] for b in boxes)
    y0 = min(b[1] for b in boxes)
    x1 = max(b[2] for b in boxes)
    y1 = max(b[3] for b in boxes)
    return (x0, y0, x1, y1)


def _tokens_in_x_range(tokens: list[OcrToken], x_start: int, x_end: int) -> list[OcrToken]:
    picked: list[OcrToken] = []
    for t in tokens:
        cx = t.x + t.w / 2
        if x_start <= cx < x_end:
            picked.append(t)
    return picked


def _tokens_bbox(tokens: list[OcrToken]) -> tuple[int, int, int, int]:
    if not tokens:
        return (0, 0, 0, 0)
    x0 = min(t.x for t in tokens)
    y0 = min(t.y for t in tokens)
    x1 = max(t.x + t.w for t in tokens)
    y1 = max(t.y + t.h for t in tokens)
    return (x0, y0, x1, y1)


def _text_from_tokens(tokens: list[OcrToken]) -> str:
    return " ".join(t.text for t in sorted(tokens, key=lambda t: (t.y, t.x))).strip()


# TSU-SOU-SF-08 table column x-ratios (empirical from 2480px scans)
TABLE_COLS = {
    "ACTIVITY_TITLE": (0.10, 0.33),
    "ACTIVITY_DATE": (0.33, 0.46),
    "VENUE": (0.46, 0.59),
    "PARTICIPANTS": (0.59, 0.78),
    "ACTIVITY_MODE": (0.78, 0.96),
}


def _find_table_anchor(lines: list[Line]) -> tuple[int, int] | None:
    """Return (header_bottom_y, header_line_index) when table headers appear."""
    for i, ln in enumerate(lines):
        upper = ln.text_upper
        if ln.y < 900:  # skip letterhead
            continue
        if any(k in upper for k in ("VENUE/PLAT", "VENUE/PLATFORM", "VENUE/PLATFO")):
            bottom = ln.y + max((t.h for t in ln.tokens), default=40)
            if i + 1 < len(lines) and lines[i + 1].y - ln.y < 120:
                nxt = lines[i + 1].text_upper
                if any(k in nxt for k in ("PARTICIPANT", "FACE", "ONLINE", "FORM")):
                    bottom = lines[i + 1].y + max((t.h for t in lines[i + 1].tokens), default=40)
            return bottom, i
        if "PARTICIPANTS" in upper and ("FACE" in upper or "ONLINE" in upper or "VENUE" in upper):
            bottom = ln.y + max((t.h for t in ln.tokens), default=40)
            return bottom, i
    return None


def _extract_table_fields(lines: list[Line], img_w: int) -> tuple[list[FieldBox], str | None]:
    anchor = _find_table_anchor(lines)
    if not anchor:
        return [], "table header not found"

    header_bottom, header_idx = anchor
    stop_words = ("SINCERELY", "RESPECTFULLY", "YOURS", "APPROVED")
    value_tokens: list[OcrToken] = []
    for ln in lines[header_idx + 1 :]:
        if ln.y < header_bottom + 5:
            continue
        if ln.y > header_bottom + 480:
            break
        if any(w in ln.text_upper for w in stop_words):
            break
        if any(w in ln.text_upper for w in ("PRESIDENT", "ADVISER", "ADVISOR")) and ln.y > header_bottom + 200:
            break
        value_tokens.extend(ln.tokens)

    if not value_tokens:
        return [], "table value row not found"

    fields: list[FieldBox] = []
    for label, (x0_ratio, x1_ratio) in TABLE_COLS.items():
        x0, x1 = int(img_w * x0_ratio), int(img_w * x1_ratio)
        col_tokens = _tokens_in_x_range(value_tokens, x0, x1)
        text = _text_from_tokens(col_tokens)
        if text:
            fields.append(FieldBox(label, text, _tokens_bbox(col_tokens)))

    # Date regex fallback across full value zone
    if not any(f.label == "ACTIVITY_DATE" for f in fields):
        blob = _text_from_tokens(value_tokens)
        m = re.search(
            r"(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}"
            r"|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4}"
            r"|\d{1,2}\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{4}"
            r"|July\s+\d{1,2},?\s+\d{4}\s+to\s+[A-Za-z]+\s+\d{1,2},?\s+\d{4})",
            blob,
            re.I,
        )
        if m:
            x0, x1 = int(img_w * TABLE_COLS["ACTIVITY_DATE"][0]), int(img_w * TABLE_COLS["ACTIVITY_DATE"][1])
            fields.append(FieldBox("ACTIVITY_DATE", m.group(1), (x0, value_tokens[0].y, x1, value_tokens[-1].y + 40)))

    return fields, None


def _extract_form_title(lines: list[Line]) -> FieldBox | None:
    hits: list[Line] = []
    for ln in lines:
        if ln.y > 900:
            break
        upper = ln.text_upper
        if "REQUEST FOR THE CONDUCT" in upper or "STUDENT ACTIVITY" in upper:
            hits.append(ln)
    if not hits:
        return None
    text = " ".join(ln.text for ln in hits)
    text = re.sub(r"\s+", " ", text).strip()
    return FieldBox("FORM_TITLE", text[:400], _merge_bbox([ln.bbox() for ln in hits]))


def _extract_date_proposal(lines: list[Line]) -> FieldBox | None:
    for ln in lines:
        if ln.y > 1100:
            break
        m = re.search(
            r"(?:Date\s*:?\s*)?(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}"
            r"|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4})",
            ln.text,
            re.I,
        )
        if m and ("DATE" in ln.text_upper or re.match(r"\d{1,2}[/-]\d{1,2}", m.group(1))):
            return FieldBox("DATE_PROPOSAL", m.group(1), ln.bbox())
    return None


def _extract_org_name(lines: list[Line]) -> FieldBox | None:
    """Org name often appears as 'The <Org> respectfully requests...'."""
    for ln in lines:
        if ln.y < 700:
            continue
        m = re.search(
            r"(?:The\s+)?(.+?)\s+respectfully\s+requests",
            ln.text,
            re.I,
        )
        if m:
            name = re.sub(r"^The\s+", "", m.group(1).strip(), flags=re.I)
            if 3 <= len(name) <= 80:
                return FieldBox("ORG_NAME", name, ln.bbox())
    return None


def _extract_objectives(lines: list[Line]) -> FieldBox | None:
    """Body paragraph between salutation and ACTIVITY table header."""
    start_idx = None
    end_idx = None
    for i, ln in enumerate(lines):
        upper = ln.text_upper
        if start_idx is None and (
            upper.startswith("SIR:") or "GREETINGS" in upper or upper.startswith("DEAR ")
        ):
            start_idx = i + 1
            continue
        if start_idx is not None and "DATE" in upper and (
            "VENUE" in upper or "PLATFORM" in upper or "PLAT" in upper
        ):
            end_idx = i
            break

    if start_idx is None:
        return None

    body_lines = lines[start_idx:end_idx] if end_idx else lines[start_idx:]
    # Drop very short / signature-ish tail lines near table
    trimmed: list[Line] = []
    for ln in body_lines:
        if len(ln.text.strip()) < 8:
            continue
        if any(k in ln.text_upper for k in ("PRESIDENT", "ADVISER", "APPROVED:", "DEAN")):
            break
        trimmed.append(ln)

    if not trimmed:
        return None

    text = " ".join(ln.text for ln in trimmed).strip()
    text = re.sub(r"\s+", " ", text)
    if len(text) < 20:
        return None

    boxes = [ln.bbox() for ln in trimmed]
    return FieldBox("OBJECTIVES", text[:2000], _merge_bbox(boxes))


def _extract_signatures(lines: list[Line], img_h: int) -> list[FieldBox]:
    """Split President / Adviser / SAS (Approved) signature blocks."""
    bottom_y = img_h * 0.55
    buckets: dict[str, list[Line]] = {
        "ORG_OFFICER_SIGNATURE": [],
        "ADVISER_SIGNATURE": [],
        "SAS_SIGNATURE": [],
    }

    for ln in lines:
        if ln.y < bottom_y:
            continue
        upper = ln.text_upper
        if any(k in upper for k in ("APPROVED", "DIRECTOR", "STUDENT AFFAIRS")):
            buckets["SAS_SIGNATURE"].append(ln)
        elif any(k in upper for k in ("ADVISER", "ADVISOR", "DEAN")):
            buckets["ADVISER_SIGNATURE"].append(ln)
        elif any(k in upper for k in ("PRESIDENT", "SINCERELY", "YOURS")):
            buckets["ORG_OFFICER_SIGNATURE"].append(ln)

    fields: list[FieldBox] = []
    for label, lns in buckets.items():
        if not lns:
            continue
        boxes = []
        texts = []
        for ln in lns:
            x0, y0, x1, y1 = ln.bbox()
            pad = max(8, int((y1 - y0) * 2))
            boxes.append((x0, max(0, y0 - pad), x1, min(img_h, y1 + pad)))
            texts.append(ln.text)
        fields.append(FieldBox(label, " | ".join(texts)[:500], _merge_bbox(boxes)))
    return fields


def prelabel_image(image_path: Path) -> tuple[list[FieldBox], PrelabelReport]:
    img_bytes = image_path.read_bytes()
    ocr = extract_text(img_bytes)
    report = PrelabelReport(image=str(image_path.resolve()))

    if not ocr.tokens:
        report.notes.append("OCR returned no tokens")
        report.fields_missing = list(SF08_FIELDS)
        return [], report

    lines = _group_lines(ocr)
    fields: list[FieldBox] = []

    table_fields, table_note = _extract_table_fields(lines, ocr.image_width)
    if table_note:
        report.notes.append(table_note)
    fields.extend(table_fields)

    for extra in (
        _extract_form_title(lines),
        _extract_date_proposal(lines),
        _extract_org_name(lines),
        _extract_objectives(lines),
    ):
        if extra:
            fields.append(extra)

    fields.extend(_extract_signatures(lines, ocr.image_height))

    found = {f.label for f in fields}
    report.fields_found = sorted(found)
    report.fields_missing = [f for f in SF08_FIELDS if f not in found]
    return fields, report


def _to_ls_percent(bbox: tuple[int, int, int, int], img_w: int, img_h: int) -> dict:
    x0, y0, x1, y1 = bbox
    pad = 2
    x0 = max(0, x0 - pad)
    y0 = max(0, y0 - pad)
    x1 = min(img_w, x1 + pad)
    y1 = min(img_h, y1 + pad)
    return {
        "x": 100.0 * x0 / img_w,
        "y": 100.0 * y0 / img_h,
        "width": 100.0 * max(1, x1 - x0) / img_w,
        "height": 100.0 * max(1, y1 - y0) / img_h,
        "rotation": 0,
    }


def _build_ls_results(fields: list[FieldBox], img_w: int, img_h: int) -> list[dict]:
    results: list[dict] = []
    results.append({
        "id": str(uuid.uuid4())[:10],
        "from_name": "doc_type",
        "to_name": "image",
        "type": "choices",
        "value": {"choices": ["SF08"]},
    })

    for fb in fields:
        rid = str(uuid.uuid4())[:10]
        box = _to_ls_percent(fb.bbox, img_w, img_h)
        results.append({
            "id": rid,
            "from_name": "label",
            "to_name": "image",
            "type": "rectanglelabels",
            "value": {**box, "rectanglelabels": [fb.label]},
        })
        results.append({
            "id": rid,
            "from_name": "transcription",
            "to_name": "image",
            "type": "textarea",
            "value": {"text": [fb.text]},
        })
    return results


def main() -> None:
    ap = argparse.ArgumentParser(description="Pre-label SF08 images for Label Studio review")
    ap.add_argument("--images", type=Path, default=Path("data/raw/images/sf08"))
    ap.add_argument("--output", type=Path, default=Path("data/raw/label_studio_sf08_import.json"))
    ap.add_argument("--report", type=Path, default=Path("data/raw/prelabel_report.json"))
    ap.add_argument("--limit", type=int, default=0, help="Max images (0 = all)")
    ap.add_argument(
        "--base-url",
        default="http://127.0.0.1:9090",
        help="HTTP image server URL prefix (run: python -m training.scripts.serve_label_images)",
    )
    args = ap.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    ml_root = Path(__file__).resolve().parents[2]
    images_dir = (ml_root / args.images).resolve()
    out_path = (ml_root / args.output).resolve()
    report_path = (ml_root / args.report).resolve()

    if not images_dir.is_dir():
        print(f"[ERROR] Images folder not found: {images_dir}", file=sys.stderr)
        sys.exit(1)

    images = sorted(images_dir.glob("*.png"))
    if args.limit > 0:
        images = images[: args.limit]

    tasks: list[dict] = []
    reports: list[dict] = []
    field_hits = {f: 0 for f in SF08_FIELDS}

    total = len(images)
    for i, img_path in enumerate(images, start=1):
        print(f"  [{i}/{total}] {img_path.name}", flush=True)
        fields, report = prelabel_image(img_path)

        with Image.open(img_path) as im:
            img_w, img_h = im.size

        rel = img_path.relative_to(images_dir.parent).as_posix()
        results = _build_ls_results(fields, img_w, img_h)
        tasks.append({
            "data": {
                "image": f"{args.base_url.rstrip('/')}/{rel}",
                "doc_type_hint": "SF08",
            },
            "predictions": [{
                "model_version": "tsu-sf08-prelabel-v2",
                "score": 0.75,
                "result": results,
            }],
        })

        for f in report.fields_found:
            field_hits[f] += 1
        reports.append({
            "image": report.image,
            "fields_found": report.fields_found,
            "fields_missing": report.fields_missing,
            "notes": report.notes,
        })

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(tasks, indent=2, ensure_ascii=False), encoding="utf-8")
    summary = {
        "images_processed": total,
        "field_detection_rate": {
            k: f"{v}/{total} ({100 * v / max(1, total):.0f}%)"
            for k, v in field_hits.items()
        },
        "reports": reports,
    }
    report_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")

    print("\n=== SF08 pre-label complete ===")
    print(f"  Tasks:   {out_path}")
    print(f"  Report:  {report_path}")
    print("  Detection rates:")
    for k, v in field_hits.items():
        print(f"    {k}: {v}/{total}")
    print("\nImport this file into Label Studio (keep the image server running):")
    print(f"  {out_path}")
    print("  python -m training.scripts.serve_label_images")


if __name__ == "__main__":
    main()
