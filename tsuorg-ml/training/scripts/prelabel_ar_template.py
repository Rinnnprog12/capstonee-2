"""
prelabel_ar_template.py — Template-based Accomplishment Report (SF-06) pre-labels.

Uses fixed percent boxes from data/templates/ar_page_templates.json (cover /
summary / activity), detects page type via OCR keywords, then fills text via
Tesseract. Produces Label Studio import JSON for review.

Usage:
  python -m training.scripts.prelabel_ar_template
  python -m training.scripts.prelabel_ar_template --limit 20
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

ML_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_TEMPLATE = ML_ROOT / "data" / "templates" / "ar_page_templates.json"


def _load_templates(path: Path) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    return data["page_types"]


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


def _detect_page_type(
    ocr: OcrResult,
    img_w: int,
    img_h: int,
    page_types: dict,
) -> str:
    text = (ocr.full_text or "").lower()
    landscape = img_w > img_h

    def hits(keys: list[str]) -> int:
        return sum(1 for k in keys if k in text)

    # Summary table pages are usually landscape and mention involvement/level columns.
    summary = page_types["summary"]
    if landscape and hits(summary.get("require_any", [])) >= 1:
        return "summary"
    if hits(summary.get("detect", [])) >= 1 and hits(summary.get("require_any", [])) >= 1:
        return "summary"

    activity = page_types["activity"]
    if hits(activity.get("detect", [])) >= 2:
        return "activity"
    if "narrative description" in text or "activity no" in text:
        return "activity"

    cover = page_types["cover"]
    if hits(cover.get("detect", [])) >= 1 and not any(
        excl in text for excl in cover.get("exclude", [])
    ):
        return "cover"

    # Fallbacks by aspect ratio when OCR is weak.
    if landscape:
        return "summary"
    if "accomplishment" in text and "semester" in text:
        return "cover"
    return "activity"


def _build_results(
    ocr: OcrResult,
    page_type: str,
    page_types: dict,
) -> tuple[list[dict], list[str], list[str]]:
    spec = page_types[page_type]
    fields: dict[str, dict[str, float]] = spec["fields"]
    ocr_fields = set(spec.get("ocr_text_fields", fields.keys()))

    results: list[dict] = [{
        "id": str(uuid.uuid4())[:10],
        "from_name": "doc_type",
        "to_name": "image",
        "type": "choices",
        "value": {"choices": ["ACCOMPLISHMENT"]},
    }]
    filled: list[str] = []

    for label, box in fields.items():
        rid = str(uuid.uuid4())[:10]
        results.append({
            "id": rid,
            "from_name": "label",
            "to_name": "image",
            "type": "rectanglelabels",
            "value": {**box, "rotation": 0, "labels": [label]},
        })
        text = ""
        if label in ocr_fields:
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
    return results, filled, list(ocr_fields)


def main() -> None:
    ap = argparse.ArgumentParser(description="Template-based AR (SF-06) pre-labels")
    ap.add_argument(
        "--images",
        type=Path,
        default=Path("data/raw/images/accomplishment"),
    )
    ap.add_argument(
        "--template",
        type=Path,
        default=DEFAULT_TEMPLATE,
        help="ar_page_templates.json path",
    )
    ap.add_argument(
        "--output",
        type=Path,
        default=Path("data/raw/label_studio_ar_import.json"),
    )
    ap.add_argument(
        "--report",
        type=Path,
        default=Path("data/raw/prelabel_ar_report.json"),
    )
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--base-url", default="http://127.0.0.1:9090")
    args = ap.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    images_dir = (ML_ROOT / args.images).resolve() if not args.images.is_absolute() else args.images
    template_path = (ML_ROOT / args.template).resolve() if not args.template.is_absolute() else args.template
    out_path = (ML_ROOT / args.output).resolve() if not args.output.is_absolute() else args.output
    report_path = (ML_ROOT / args.report).resolve() if not args.report.is_absolute() else args.report

    if not template_path.is_file():
        print(f"[ERROR] Template not found: {template_path}", file=sys.stderr)
        sys.exit(1)
    if not images_dir.is_dir():
        print(f"[ERROR] Images folder not found: {images_dir}", file=sys.stderr)
        print("  Run: python -m training.scripts.setup_dataset --type accomplishment", file=sys.stderr)
        sys.exit(1)

    page_types = _load_templates(template_path)
    images = sorted(images_dir.glob("*.png"))
    if args.limit > 0:
        images = images[: args.limit]

    tasks: list[dict] = []
    reports: list[dict] = []
    type_counts: dict[str, int] = {"cover": 0, "summary": 0, "activity": 0}
    fill_hits: dict[str, int] = {}
    total = len(images)

    for i, img_path in enumerate(images, start=1):
        print(f"  [{i}/{total}] {img_path.name}", flush=True)
        with Image.open(img_path) as im:
            img_w, img_h = im.size
        ocr = extract_text(img_path.read_bytes())
        page_type = _detect_page_type(ocr, img_w, img_h, page_types)
        type_counts[page_type] = type_counts.get(page_type, 0) + 1

        results, filled, ocr_fields = _build_results(ocr, page_type, page_types)
        for f in filled:
            fill_hits[f] = fill_hits.get(f, 0) + 1
        for f in ocr_fields:
            fill_hits.setdefault(f, 0)

        rel = img_path.relative_to(images_dir.parent).as_posix()
        tasks.append({
            "data": {
                "image": f"{args.base_url.rstrip('/')}/{rel}",
                "doc_type_hint": "ACCOMPLISHMENT",
                "page_type_hint": page_type,
            },
            "predictions": [{
                "model_version": f"tsu-ar-template-v1-{page_type}",
                "score": 0.85,
                "result": results,
            }],
        })
        reports.append({
            "image": str(img_path.resolve()),
            "page_type": page_type,
            "text_filled": sorted(filled),
            "text_missing": sorted(f for f in ocr_fields if f not in filled),
        })

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(tasks, indent=2, ensure_ascii=False), encoding="utf-8")
    report_path.write_text(json.dumps({
        "images_processed": total,
        "page_type_counts": type_counts,
        "template_page_types": list(page_types.keys()),
        "ocr_fill_rate": {k: f"{v}/{total}" for k, v in sorted(fill_hits.items())},
        "reports": reports,
    }, indent=2, ensure_ascii=False), encoding="utf-8")

    print("\n=== AR (SF-06) template pre-label complete ===")
    print(f"  Page types: {type_counts}")
    print("  OCR text fill (non-zero counts):")
    for k, v in sorted(fill_hits.items()):
        if v:
            print(f"    {k}: {v}/{total}")
    print(f"\n  Import: {out_path}")
    print("  Keep running: python -m training.scripts.serve_label_images")


if __name__ == "__main__":
    main()
