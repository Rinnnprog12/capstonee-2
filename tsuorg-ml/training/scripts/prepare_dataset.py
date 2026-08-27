"""
prepare_dataset.py — Convert Label Studio export → LayoutLMv3-ready JSONL.

Accepted inputs:
  • JSON array (Label Studio "JSON" export) — preferred
  • JSONL (one task object per line)
  • Single JSON object

Per-task fields we look for:
  • image: top-level `image`, or `data.image`
  • doc type: top-level `label` / `document_type`, or `data.label`,
              or Choices result `doc_type` / `label`
  • regions: `annotations[-1].result[]` with rectangle + text

Usage:
  python -m training.scripts.prepare_dataset \\
    --input  data/raw/label_studio_export.json \\
    --images data/raw/images/ \\
    --output data/processed/dataset.jsonl \\
    --split
"""

from __future__ import annotations

import argparse
import json
import os
import random
import re
import sys
from pathlib import Path
from typing import Any


# ─── Document-level class map ────────────────────────────────────────────────

DOC_CLASSES = {
    "SF08":           0,
    "ACCOMPLISHMENT": 1,
    "ACCREDITATION":  2,
}
IDX_TO_CLASS = {v: k for k, v in DOC_CLASSES.items()}

# ─── BIO token-label map for key fields ──────────────────────────────────────

TOKEN_LABELS = [
    "O",
    # SF08
    "B-ACTIVITY_TITLE", "I-ACTIVITY_TITLE",
    "B-ACTIVITY_DATE",  "I-ACTIVITY_DATE",
    "B-VENUE",          "I-VENUE",
    "B-OBJECTIVES",     "I-OBJECTIVES",
    "B-PARTICIPANTS",   "I-PARTICIPANTS",
    "B-SIGNATURE",      "I-SIGNATURE",
    # Accomplishment
    "B-EVENT_NAME",     "I-EVENT_NAME",
    "B-DATE_CONDUCTED", "I-DATE_CONDUCTED",
    "B-OUTCOME",        "I-OUTCOME",
    "B-EXPENSES",       "I-EXPENSES",
    # Accreditation
    "B-ORG_NAME",       "I-ORG_NAME",
    "B-COLLEGE",        "I-COLLEGE",
    "B-AY",             "I-AY",
    "B-OFFICER_LIST",   "I-OFFICER_LIST",
]
LABEL2ID = {l: i for i, l in enumerate(TOKEN_LABELS)}
ID2LABEL = {i: l for l, i in LABEL2ID.items()}
_KNOWN_TAGS = {l[2:] for l in TOKEN_LABELS if l.startswith("B-")}


def _normalize_box(box: dict[str, float], img_w: int, img_h: int) -> list[int]:
    """Convert Label Studio percent coords to LayoutLMv3 0-1000 integer coords."""
    x0 = int(box["x"] / 100 * 1000)
    y0 = int(box["y"] / 100 * 1000)
    x1 = int((box["x"] + box["width"]) / 100 * 1000)
    y1 = int((box["y"] + box["height"]) / 100 * 1000)
    return [
        max(0, min(1000, x0)),
        max(0, min(1000, y0)),
        max(0, min(1000, x1)),
        max(0, min(1000, y1)),
    ]


def _label_from_tag(tag: str) -> str:
    tag = tag.upper().replace(" ", "_").replace("-", "_")
    return f"B-{tag}" if tag in _KNOWN_TAGS else "O"


def _as_text(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, list):
        return " ".join(str(x).strip() for x in value if str(x).strip()).strip()
    return str(value).strip()


def _load_raw_tasks(path: Path) -> list[dict[str, Any]]:
    text = path.read_text(encoding="utf-8").strip()
    if not text:
        return []

    # JSONL: first non-empty line is `{...}`
    if path.suffix.lower() in {".jsonl", ".ndjson"} or (
        not text.startswith("[") and text.startswith("{") and "\n{" in text
    ):
        tasks: list[dict[str, Any]] = []
        for line in text.splitlines():
            line = line.strip()
            if not line:
                continue
            tasks.append(json.loads(line))
        return tasks

    raw = json.loads(text)
    if isinstance(raw, dict):
        return [raw]
    if isinstance(raw, list):
        return [r for r in raw if isinstance(r, dict)]
    raise ValueError(f"Unsupported export shape in {path}")


def _resolve_doc_label(record: dict[str, Any]) -> str:
    data = record.get("data") if isinstance(record.get("data"), dict) else {}
    candidates = [
        record.get("label"),
        record.get("document_type"),
        data.get("label"),
        data.get("document_type"),
        data.get("doc_type"),
    ]
    for c in candidates:
        if isinstance(c, str) and c.strip():
            return c.strip().upper()

    # Choices annotation (from_name doc_type / label)
    anns = record.get("annotations") or record.get("completions") or []
    if anns:
        for item in anns[-1].get("result", []):
            if item.get("type") not in {"choices", "choice"}:
                continue
            name = str(item.get("from_name", "")).lower()
            if name not in {"doc_type", "label", "document_type", "doctype"}:
                continue
            choices = item.get("value", {}).get("choices") or item.get("value", {}).get("choice") or []
            if isinstance(choices, str):
                return choices.strip().upper()
            if choices:
                return str(choices[0]).strip().upper()
    return ""


def _resolve_image_ref(record: dict[str, Any]) -> str:
    data = record.get("data") if isinstance(record.get("data"), dict) else {}
    ref = record.get("image") or data.get("image") or data.get("ocr") or ""
    return str(ref or "")


def _iter_regions(record: dict[str, Any]) -> list[dict[str, Any]]:
    anns = record.get("annotations") or record.get("completions") or []
    if not anns:
        # JSON-MIN flat shape
        if "annotations" not in record and record.get("label") and record.get("bbox"):
            return []
        # Some exports put regions at top-level "result"
        if isinstance(record.get("result"), list):
            return record["result"]
        return []
    return list(anns[-1].get("result") or [])


def convert_record(record: dict[str, Any], images_dir: Path) -> dict[str, Any] | None:
    """Convert a single Label Studio record to a training example."""
    doc_id = str(record.get("id", id(record)))
    image_ref = _resolve_image_ref(record)
    doc_label_str = _resolve_doc_label(record)
    doc_label = DOC_CLASSES.get(doc_label_str, -1)

    if doc_label == -1:
        print(f"[WARN] Unknown doc label '{doc_label_str}' for id={doc_id}, skipping.")
        return None

    # Resolve image path
    if image_ref.startswith("data:image"):
        import base64
        b64 = re.sub(r"^data:image/\w+;base64,", "", image_ref)
        img_bytes = base64.b64decode(b64)
        img_path = images_dir / f"{doc_id}.png"
        img_path.write_bytes(img_bytes)
    else:
        # Label Studio local paths often look like /data/upload/1/xyz.png
        basename = os.path.basename(image_ref.split("?")[0])
        img_path = images_dir / basename
        if not img_path.exists() and image_ref:
            # Try absolute / relative path as-is
            alt = Path(image_ref)
            if alt.exists():
                img_path = alt

    if not img_path.exists():
        print(f"[WARN] Image not found: {img_path} (ref={image_ref!r}), skipping.")
        return None

    from PIL import Image as PILImage
    with PILImage.open(img_path) as im:
        img_w, img_h = im.size

    words, boxes, word_labels = [], [], []
    pending_text_by_id: dict[str, str] = {}

    regions = _iter_regions(record)

    # First pass: region transcriptions keyed by id
    for ann in regions:
        if ann.get("type") == "textarea" and ann.get("from_name") == "transcription":
            rid = str(ann.get("id") or ann.get("region_id") or "")
            pending_text_by_id[rid] = _as_text(ann.get("value", {}).get("text"))

    for ann in regions:
        typ = ann.get("type")
        if typ in {"choices", "choice", "textarea"}:
            continue

        v = ann.get("value", {})
        if not all(k in v for k in ("x", "y", "width", "height")):
            continue

        # Tag from labels rectangle or from_name
        labels = v.get("labels") or []
        tag = labels[0] if labels else ann.get("from_name", "O")
        label = _label_from_tag(str(tag))

        rid = str(ann.get("id") or "")
        txt = _as_text(v.get("text")) or pending_text_by_id.get(rid, "")
        if not txt:
            continue

        box = _normalize_box(v, img_w, img_h)
        tokens = txt.split()
        for i, tok in enumerate(tokens):
            words.append(tok)
            boxes.append(box)
            if i == 0:
                word_labels.append(LABEL2ID.get(label, 0))
            else:
                iob = label.replace("B-", "I-") if label.startswith("B-") else "O"
                word_labels.append(LABEL2ID.get(iob, 0))

    if not words:
        print(f"[WARN] No annotations for id={doc_id}, skipping.")
        return None

    return {
        "id":          doc_id,
        "image_path":  str(img_path.resolve()),
        "words":       words,
        "boxes":       boxes,
        "word_labels": word_labels,
        "doc_label":   doc_label,
        "source":      doc_label_str.lower(),
    }


def write_splits(records: list[dict], out_dir: Path, train: float, val: float) -> None:
    random.shuffle(records)
    n = len(records)
    n_tr = int(n * train)
    n_val = int(n * val)

    splits = {
        "train": records[:n_tr],
        "val":   records[n_tr: n_tr + n_val],
        "test":  records[n_tr + n_val:],
    }
    for name, recs in splits.items():
        path = out_dir / f"{name}.jsonl"
        with path.open("w", encoding="utf-8") as f:
            for r in recs:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
        print(f"  {name}: {len(recs)} records → {path}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--input",  required=True, help="Label Studio JSON / JSONL export")
    ap.add_argument("--images", default="data/raw/images", help="Image directory")
    ap.add_argument("--output", default="data/processed/dataset.jsonl")
    ap.add_argument("--split",  action="store_true", help="Also write train/val/test splits")
    ap.add_argument("--train",  type=float, default=0.70)
    ap.add_argument("--val",    type=float, default=0.15)
    ap.add_argument("--seed",   type=int, default=42)
    args = ap.parse_args()

    random.seed(args.seed)

    in_path  = Path(args.input)
    out_path = Path(args.output)
    img_dir  = Path(args.images)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    img_dir.mkdir(parents=True, exist_ok=True)

    if not in_path.exists():
        print(f"[ERROR] Input not found: {in_path}", file=sys.stderr)
        sys.exit(1)

    raw = _load_raw_tasks(in_path)
    records = []
    for rec in raw:
        ex = convert_record(rec, img_dir)
        if ex:
            records.append(ex)

    print(f"\nConverted {len(records)} / {len(raw)} records.")

    with out_path.open("w", encoding="utf-8") as f:
        for r in records:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
    print(f"Saved → {out_path}")

    if args.split:
        print("Writing train/val/test splits…")
        write_splits(records, out_path.parent, args.train, args.val)

    from collections import Counter
    dist = Counter(r["source"] for r in records)
    print("\nLabel distribution:", dict(dist))

    maps = {"doc_classes": DOC_CLASSES, "token_labels": LABEL2ID, "id2label": ID2LABEL}
    (out_path.parent / "label_map.json").write_text(
        json.dumps(maps, indent=2, ensure_ascii=False), encoding="utf-8")
    print("Label maps →", out_path.parent / "label_map.json")


if __name__ == "__main__":
    main()
