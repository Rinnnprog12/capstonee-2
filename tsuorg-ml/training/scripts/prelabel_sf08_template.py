"""
prelabel_sf08_template.py — Template-based SF08 pre-labels.

Uses the human-verified box layout from page 1 (data/raw/images/sf08/
sf08_template_export.json) as a fixed template, then fills each box's text via
Tesseract OCR per page. Produces Label Studio import JSON for review.

Usage:
  python -m training.scripts.prelabel_sf08_template
  python -m training.scripts.prelabel_sf08_template --limit 20
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import uuid
from pathlib import Path

from PIL import Image

from calsv.layer2_ocr.ocr import OcrResult, OcrToken, extract_text

# Gold template boxes (percent) from the analyst's page-1 annotation.
# Order matters only for display. h/y tuned to the standard TSU-SOU-SF-08 layout.
SF08_TEMPLATE: dict[str, dict[str, float]] = {
    "FORM_TITLE":            {"x": 15.48, "y": 14.68, "width": 73.47, "height": 3.31},
    "DATE_PROPOSAL":         {"x": 13.63, "y": 18.89, "width": 13.83, "height": 1.60},
    "ORG_NAME":              {"x": 15.78, "y": 30.38, "width": 17.13, "height": 1.80},
    "OBJECTIVES":            {"x": 12.12, "y": 26.95, "width": 78.18, "height": 17.83},
    "ACTIVITY_TITLE":        {"x": 12.95, "y": 45.61, "width": 17.29, "height": 10.93},
    "ACTIVITY_DATE":         {"x": 32.70, "y": 46.18, "width": 11.25, "height": 10.79},
    "VENUE":                 {"x": 45.52, "y": 46.80, "width": 11.94, "height": 9.57},
    "PARTICIPANTS":          {"x": 58.55, "y": 47.14, "width": 14.39, "height": 9.95},
    "ACTIVITY_MODE":         {"x": 74.52, "y": 47.56, "width": 15.12, "height": 9.51},
    "ORG_OFFICER_SIGNATURE": {"x": 10.55, "y": 61.47, "width": 26.90, "height": 5.22},
    "ADVISER_SIGNATURE":     {"x": 51.39, "y": 59.89, "width": 33.07, "height": 8.64},
    "SAS_SIGNATURE":         {"x":  8.17, "y": 71.10, "width": 33.52, "height": 8.40},
}

# Fields whose text is OCR-filled (signatures often handwritten → leave blank).
OCR_TEXT_FIELDS = {
    "FORM_TITLE", "DATE_PROPOSAL", "ORG_NAME", "OBJECTIVES",
    "ACTIVITY_TITLE", "ACTIVITY_DATE", "VENUE", "PARTICIPANTS", "ACTIVITY_MODE",
}


def _tokens_in_percent_box(
    ocr: OcrResult, box: dict[str, float]
) -> list[OcrToken]:
    w, h = ocr.image_width, ocr.image_height
    x0 = box["x"] / 100 * w
    y0 = box["y"] / 100 * h
    x1 = (box["x"] + box["width"]) / 100 * w
    y1 = (box["y"] + box["height"]) / 100 * h
    picked = []
    for t in ocr.tokens:
        cx = t.x + t.w / 2
        cy = t.y + t.h / 2
        if x0 <= cx <= x1 and y0 <= cy <= y1:
            picked.append(t)
    return picked


def _text_from(tokens: list[OcrToken]) -> str:
    ordered = sorted(tokens, key=lambda t: (t.y, t.x))
    return re.sub(r"\s+", " ", " ".join(t.text for t in ordered)).strip()


def _build_results(ocr: OcrResult) -> tuple[list[dict], list[str]]:
    results: list[dict] = [{
        "id": str(uuid.uuid4())[:10],
        "from_name": "doc_type",
        "to_name": "image",
        "type": "choices",
        "value": {"choices": ["SF08"]},
    }]
    filled: list[str] = []

    for label, box in SF08_TEMPLATE.items():
        rid = str(uuid.uuid4())[:10]
        results.append({
            "id": rid,
            "from_name": "label",
            "to_name": "image",
            "type": "rectanglelabels",
            "value": {**box, "rotation": 0, "labels": [label]},
        })
        text = ""
        if label in OCR_TEXT_FIELDS:
            text = _text_from(_tokens_in_percent_box(ocr, box))
            if text:
                filled.append(label)
        results.append({
            "id": rid,
            "from_name": "transcription",
            "to_name": "image",
            "type": "textarea",
            "value": {"text": [text]},
        })
    return results, filled


def main() -> None:
    ap = argparse.ArgumentParser(description="Template-based SF08 pre-labels")
    ap.add_argument("--images", type=Path, default=Path("data/raw/images/sf08"))
    ap.add_argument("--output", type=Path, default=Path("data/raw/label_studio_sf08_import.json"))
    ap.add_argument("--report", type=Path, default=Path("data/raw/prelabel_report.json"))
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--base-url", default="http://127.0.0.1:9090")
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
    fill_hits = {f: 0 for f in OCR_TEXT_FIELDS}
    total = len(images)

    for i, img_path in enumerate(images, start=1):
        print(f"  [{i}/{total}] {img_path.name}", flush=True)
        ocr = extract_text(img_path.read_bytes())
        results, filled = _build_results(ocr)
        for f in filled:
            fill_hits[f] += 1

        rel = img_path.relative_to(images_dir.parent).as_posix()
        tasks.append({
            "data": {
                "image": f"{args.base_url.rstrip('/')}/{rel}",
                "doc_type_hint": "SF08",
            },
            "predictions": [{
                "model_version": "tsu-sf08-template-v1",
                "score": 0.9,
                "result": results,
            }],
        })
        reports.append({
            "image": str(img_path.resolve()),
            "text_filled": sorted(filled),
            "text_missing": sorted(f for f in OCR_TEXT_FIELDS if f not in filled),
        })

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(tasks, indent=2, ensure_ascii=False), encoding="utf-8")
    report_path.write_text(json.dumps({
        "images_processed": total,
        "template_fields": list(SF08_TEMPLATE.keys()),
        "ocr_fill_rate": {k: f"{v}/{total}" for k, v in fill_hits.items()},
        "reports": reports,
    }, indent=2, ensure_ascii=False), encoding="utf-8")

    print("\n=== SF08 template pre-label complete ===")
    print(f"  All 12 boxes placed on every page (fixed layout).")
    print("  OCR text fill rate:")
    for k, v in fill_hits.items():
        print(f"    {k}: {v}/{total}")
    print(f"\n  Import: {out_path}")
    print("  Keep running: python -m training.scripts.serve_label_images")


if __name__ == "__main__":
    main()
