"""
prepare_dataset.py — Convert Label Studio export → LayoutLMv3-ready JSONL.

Pipeline:
  Label Studio JSON (rectanglelabels, box-only)
    → resolve images under --images
    → full-page Tesseract OCR (words + per-token boxes)
    → match OCR tokens to annotation regions
    → labeled tokens + background O tokens
    → optional group-aware train/val/test split

Usage:
  python -m training.scripts.prepare_dataset \\
    --input  clean-dataset/accomplishment-report/john-lloyd \\
    --images data/raw/images \\
    --output data/processed/dataset.jsonl \\
    --split --limit 20
"""

from __future__ import annotations

import argparse
import json
import os
import random
import re
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from training.utils.label_vocab import (
    DOC_CLASSES,
    ID2LABEL,
    LABEL2ID,
    load_label_aliases,
    resolve_token_label,
)

# Matching: token center must lie inside the annotation percent-box.
# Documented strategy — containment by center point (robust for form cells).
MATCH_STRATEGY = "center_inside_percent_box"


@dataclass
class ConvertStats:
    images_processed: int = 0
    images_missing: list[str] = field(default_factory=list)
    annotations_processed: int = 0
    ocr_regions: int = 0
    ocr_regions_with_text: int = 0
    ocr_regions_with_no_text: int = 0
    ocr_failures: int = 0
    tokens_generated: int = 0
    tokens_labeled: int = 0
    tokens_o: int = 0
    missing_labels: int = 0
    unknown_labels: Counter = field(default_factory=Counter)
    aliased_labels: Counter = field(default_factory=Counter)
    invalid_boxes: int = 0
    skipped_tasks: int = 0
    converted: int = 0


def _load_raw_tasks(path: Path) -> list[dict[str, Any]]:
    if path.is_dir():
        tasks: list[dict[str, Any]] = []
        for p in sorted(path.rglob("*.json")):
            raw = json.loads(p.read_text(encoding="utf-8"))
            chunk = raw if isinstance(raw, list) else [raw]
            for t in chunk:
                if isinstance(t, dict):
                    t = dict(t)
                    t["_source_file"] = str(p.resolve())
                    tasks.append(t)
        return tasks

    text = path.read_text(encoding="utf-8").strip()
    if not text:
        return []
    if path.suffix.lower() in {".jsonl", ".ndjson"}:
        return [json.loads(line) for line in text.splitlines() if line.strip()]
    raw = json.loads(text)
    if isinstance(raw, dict):
        return [raw]
    return [r for r in raw if isinstance(r, dict)]


def _resolve_doc_label(record: dict[str, Any]) -> str:
    data = record.get("data") if isinstance(record.get("data"), dict) else {}
    for c in (
        record.get("label"),
        record.get("document_type"),
        data.get("label"),
        data.get("document_type"),
        data.get("doc_type"),
    ):
        if isinstance(c, str) and c.strip():
            return c.strip().upper()

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
    return str(record.get("image") or data.get("image") or data.get("ocr") or "")


def _iter_regions(record: dict[str, Any]) -> list[dict[str, Any]]:
    anns = record.get("annotations") or record.get("completions") or []
    if not anns:
        if isinstance(record.get("result"), list):
            return record["result"]
        return []
    return list(anns[-1].get("result") or [])


def resolve_image_path(image_ref: str, images_dir: Path) -> Path | None:
    """
    Resolve Label Studio paths like /data/upload/8/uuid-file.jpg
    to a local file under images_dir (basename search, recursive).
    """
    if not image_ref:
        return None
    if image_ref.startswith("data:image"):
        return None  # handled separately

    # Absolute / relative as-is
    direct = Path(image_ref)
    if direct.exists():
        return direct

    basename = os.path.basename(image_ref.split("?")[0])
    if not basename:
        return None

    candidate = images_dir / basename
    if candidate.exists():
        return candidate

    if images_dir.exists():
        for hit in images_dir.rglob(basename):
            return hit
    return None


def _percent_box_valid(v: dict[str, Any]) -> bool:
    try:
        x, y, w, h = float(v["x"]), float(v["y"]), float(v["width"]), float(v["height"])
    except (KeyError, TypeError, ValueError):
        return False
    if w <= 0 or h <= 0:
        return False
    if x < -0.01 or y < -0.01:
        return False
    if x + w > 100.5 or y + h > 100.5:
        return False
    return True


def _token_center_inside(token: Any, box: dict[str, float], img_w: int, img_h: int) -> bool:
    """MATCH_STRATEGY = center_inside_percent_box."""
    cx = token.x + token.w / 2
    cy = token.y + token.h / 2
    x0 = box["x"] / 100 * img_w
    y0 = box["y"] / 100 * img_h
    x1 = (box["x"] + box["width"]) / 100 * img_w
    y1 = (box["y"] + box["height"]) / 100 * img_h
    return x0 <= cx <= x1 and y0 <= cy <= y1


def _group_id(record: dict[str, Any]) -> str:
    """
    Group pages from the same source export / document instance together
    to reduce train/test leakage.
    """
    src = record.get("_source_file")
    if src:
        return Path(src).stem
    fu = record.get("file_upload") or ""
    # Strip page suffixes when present
    stem = Path(str(fu)).stem
    stem = re.sub(r"_Page_\d+.*$", "", stem, flags=re.IGNORECASE)
    stem = re.sub(r"_page[_\-]?\d+.*$", "", stem, flags=re.IGNORECASE)
    if stem:
        return stem
    return str(record.get("project", "unknown"))


def convert_record(
    record: dict[str, Any],
    images_dir: Path,
    *,
    aliases: dict[str, str],
    exclude: set[str],
    include_background_o: bool,
    stats: ConvertStats,
) -> dict[str, Any] | None:
    doc_id = str(record.get("id", id(record)))
    image_ref = _resolve_image_ref(record)
    doc_label_str = _resolve_doc_label(record)
    doc_label = DOC_CLASSES.get(doc_label_str, -1)

    if doc_label == -1:
        print(f"[WARN] Unknown doc label '{doc_label_str}' for id={doc_id}, skipping.")
        stats.skipped_tasks += 1
        return None

    # Resolve / materialize image
    if image_ref.startswith("data:image"):
        import base64

        b64 = re.sub(r"^data:image/\w+;base64,", "", image_ref)
        img_bytes = base64.b64decode(b64)
        img_path = images_dir / f"{doc_id}.png"
        images_dir.mkdir(parents=True, exist_ok=True)
        img_path.write_bytes(img_bytes)
    else:
        img_path = resolve_image_path(image_ref, images_dir)
        if img_path is None:
            stats.images_missing.append(f"id={doc_id} ref={image_ref}")
            return None

    from PIL import Image as PILImage

    with PILImage.open(img_path) as im:
        img_w, img_h = im.size
        img_bytes = img_path.read_bytes()

    # Full-page OCR
    try:
        from calsv.layer2_ocr.ocr import extract_text

        ocr = extract_text(img_bytes)
    except Exception as exc:
        print(f"[ERROR] OCR failed for {img_path}: {exc}")
        stats.ocr_failures += 1
        return None

    if not ocr.tokens:
        stats.ocr_failures += 1
        print(f"[WARN] OCR returned 0 tokens for {img_path}")
        # Still allow empty? No — not training-ready
        return None

    stats.images_processed += 1

    # Collect annotation regions with resolved labels
    regions: list[tuple[dict[str, float], str]] = []
    pending_text_by_id: dict[str, str] = {}

    for ann in _iter_regions(record):
        if ann.get("type") == "textarea" and ann.get("from_name") == "transcription":
            rid = str(ann.get("id") or ann.get("region_id") or "")
            txt = ann.get("value", {}).get("text")
            if isinstance(txt, list):
                pending_text_by_id[rid] = " ".join(str(x) for x in txt)
            elif txt:
                pending_text_by_id[rid] = str(txt)

    for ann in _iter_regions(record):
        typ = ann.get("type")
        if typ in {"choices", "choice", "textarea"}:
            continue
        if typ not in {"rectanglelabels", "labels", "rectangle", None}:
            continue

        v = ann.get("value") or {}
        if not all(k in v for k in ("x", "y", "width", "height")):
            continue
        if not _percent_box_valid(v):
            stats.invalid_boxes += 1
            continue

        raw_labels = v.get("rectanglelabels") or v.get("labels") or []
        if not raw_labels:
            # Do NOT fall back to from_name ("label") — that silently becomes O
            stats.missing_labels += 1
            print(f"[WARN] Missing rectanglelabels/labels for task={doc_id} ann={ann.get('id')}")
            continue

        raw_tag = str(raw_labels[0])
        resolved, status = resolve_token_label(raw_tag, aliases=aliases, exclude=exclude)
        if status == "unknown":
            stats.unknown_labels[raw_tag] += 1
            print(f"[WARN] Unknown label '{raw_tag}' task={doc_id} — not converted to O")
            continue
        if status in {"excluded", "empty"}:
            continue
        if status == "aliased":
            stats.aliased_labels[f"{raw_tag}→{resolved}"] += 1

        if resolved is None or resolved == "O":
            continue

        regions.append((v, resolved))
        stats.annotations_processed += 1
        stats.ocr_regions += 1

    # Assign each OCR token to at most one region (first match by annotation order)
    token_labels: list[int] = [LABEL2ID["O"]] * len(ocr.tokens)
    region_hit_counts = [0] * len(regions)

    for ti, token in enumerate(ocr.tokens):
        for ri, (box, label) in enumerate(regions):
            if _token_center_inside(token, box, img_w, img_h):
                token_labels[ti] = LABEL2ID[label]
                region_hit_counts[ri] += 1
                break

    for ri, hits in enumerate(region_hit_counts):
        if hits > 0:
            stats.ocr_regions_with_text += 1
        else:
            stats.ocr_regions_with_no_text += 1

    words: list[str] = []
    boxes: list[list[int]] = []
    word_labels: list[int] = []
    confidences: list[float] = []

    for token, lid in zip(ocr.tokens, token_labels):
        word = token.text.strip()
        if not word:
            continue
        if not include_background_o and lid == LABEL2ID["O"]:
            continue
        words.append(word)
        boxes.append(token.normalized_bbox(img_w, img_h))
        word_labels.append(lid)
        confidences.append(float(token.confidence))

    stats.tokens_generated += len(words)
    stats.tokens_labeled += sum(1 for x in word_labels if x != LABEL2ID["O"])
    stats.tokens_o += sum(1 for x in word_labels if x == LABEL2ID["O"])

    if not words:
        print(f"[WARN] No tokens after OCR match for id={doc_id}, skipping.")
        stats.skipped_tasks += 1
        return None

    # Signature regions with zero OCR: inject placeholder with region box (last resort)
    for box, label in regions:
        if label.endswith("SIGNATURE") or label == "SIGNATURES":
            # already counted in region_hit; if no OCR inside, add placeholder
            pass

    for (box, label), hits in zip(regions, region_hit_counts):
        if hits > 0:
            continue
        if label not in {
            "ORG_OFFICER_SIGNATURE",
            "ADVISER_SIGNATURE",
            "SAS_SIGNATURE",
            "SIGNATURE",
            "SECRETARY_SIGNATURE",
            "PRESIDENT_SIGNATURE",
            "SIGNATURES",
        }:
            continue
        # Region percent → 0-1000 box
        x0 = int(box["x"] / 100 * 1000)
        y0 = int(box["y"] / 100 * 1000)
        x1 = int((box["x"] + box["width"]) / 100 * 1000)
        y1 = int((box["y"] + box["height"]) / 100 * 1000)
        words.append("[SIGNATURE]")
        boxes.append([
            max(0, min(1000, x0)),
            max(0, min(1000, y0)),
            max(0, min(1000, x1)),
            max(0, min(1000, y1)),
        ])
        word_labels.append(LABEL2ID[label])
        confidences.append(-1.0)
        stats.tokens_generated += 1
        stats.tokens_labeled += 1

    stats.converted += 1
    return {
        "id": doc_id,
        "image_path": str(img_path.resolve()),
        "words": words,
        "boxes": boxes,
        "word_labels": word_labels,
        "word_confidences": confidences,
        "doc_label": doc_label,
        "source": doc_label_str.lower(),
        "group_id": _group_id(record),
        "match_strategy": MATCH_STRATEGY,
        "include_background_o": include_background_o,
    }


def write_group_aware_splits(
    records: list[dict],
    out_dir: Path,
    train: float,
    val: float,
    seed: int,
) -> dict[str, Any]:
    """
    Assign whole groups (source export / document instance) to one split.
    Approximates train/val/test ratios while preventing cross-split leakage.
    """
    rng = random.Random(seed)
    groups: dict[str, list[dict]] = defaultdict(list)
    for r in records:
        groups[r.get("group_id") or r["id"]].append(r)

    group_ids = list(groups.keys())
    rng.shuffle(group_ids)

    # Greedy fill by page count
    targets = {
        "train": train * len(records),
        "val": val * len(records),
        "test": (1.0 - train - val) * len(records),
    }
    splits: dict[str, list[dict]] = {"train": [], "val": [], "test": []}
    counts = {"train": 0, "val": 0, "test": 0}

    for gid in group_ids:
        pages = groups[gid]
        # Prefer the split farthest below its target
        deficits = {k: targets[k] - counts[k] for k in splits}
        choice = max(deficits, key=deficits.get)
        splits[choice].extend(pages)
        counts[choice] += len(pages)

    report = {
        "train": len(splits["train"]),
        "val": len(splits["val"]),
        "test": len(splits["test"]),
        "groups": {k: len({r.get("group_id") for r in v}) for k, v in splits.items()},
        "class_distribution": {},
        "potential_leakage": [],
    }

    for name, recs in splits.items():
        path = out_dir / f"{name}.jsonl"
        with path.open("w", encoding="utf-8") as f:
            for r in recs:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
        print(f"  {name}: {len(recs)} records → {path}")
        report["class_distribution"][name] = dict(Counter(r["source"] for r in recs))

    # Leakage check: same group_id in multiple splits
    membership: dict[str, set[str]] = defaultdict(set)
    for name, recs in splits.items():
        for r in recs:
            membership[r.get("group_id", "")].add(name)
    for gid, places in membership.items():
        if len(places) > 1:
            report["potential_leakage"].append({"group_id": gid, "splits": sorted(places)})

    if report["potential_leakage"]:
        print("[ERROR] Group leakage detected:", report["potential_leakage"])
    else:
        print("  Group leakage: none")

    print("  Class distribution:", report["class_distribution"])
    print("  Groups per split:", report["groups"])
    return report


def print_stats(stats: ConvertStats) -> None:
    print("\n=== Conversion / OCR report ===")
    print(f"Images processed:           {stats.images_processed}")
    print(f"Images missing:             {len(stats.images_missing)}")
    print(f"Annotations processed:      {stats.annotations_processed}")
    print(f"OCR regions:                {stats.ocr_regions}")
    print(f"OCR regions with text:      {stats.ocr_regions_with_text}")
    print(f"OCR regions with no text:   {stats.ocr_regions_with_no_text}")
    print(f"OCR failures:               {stats.ocr_failures}")
    print(f"Tokens generated:           {stats.tokens_generated}")
    print(f"Tokens labeled (non-O):     {stats.tokens_labeled}")
    print(f"Tokens O (background):      {stats.tokens_o}")
    print(f"Missing labels:             {stats.missing_labels}")
    print(f"Unknown labels:             {dict(stats.unknown_labels)}")
    print(f"Aliased labels:             {dict(stats.aliased_labels)}")
    print(f"Invalid bounding boxes:     {stats.invalid_boxes}")
    print(f"Skipped tasks:              {stats.skipped_tasks}")
    print(f"Converted records:          {stats.converted}")
    print(f"Match strategy:             {MATCH_STRATEGY}")
    for miss in stats.images_missing[:30]:
        print(f"  MISSING {miss}")
    if len(stats.images_missing) > 30:
        print(f"  … and {len(stats.images_missing) - 30} more")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True, help="LS JSON file or directory")
    ap.add_argument(
        "--images",
        default="data/raw/images",
        help="Local image root (resolves /data/upload/... by basename)",
    )
    ap.add_argument("--output", default="data/processed/dataset.jsonl")
    ap.add_argument("--split", action="store_true")
    ap.add_argument("--train", type=float, default=0.70)
    ap.add_argument("--val", type=float, default=0.15)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--limit", type=int, default=0, help="Convert at most N tasks (small sample)")
    ap.add_argument(
        "--include-background-o",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="Include unmatched full-page OCR tokens as O (default: true)",
    )
    ap.add_argument(
        "--strict-images",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="Exit non-zero if any required image is missing (default: true)",
    )
    ap.add_argument("--aliases", default=None, help="Path to label_aliases.yaml")
    args = ap.parse_args()

    random.seed(args.seed)

    in_path = Path(args.input)
    out_path = Path(args.output)
    img_dir = Path(args.images)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    img_dir.mkdir(parents=True, exist_ok=True)

    if not in_path.exists():
        print(f"[ERROR] Input not found: {in_path}", file=sys.stderr)
        sys.exit(1)

    aliases_cfg = load_label_aliases(Path(args.aliases) if args.aliases else None)
    aliases = aliases_cfg["aliases"]
    exclude = aliases_cfg["exclude"]

    raw = _load_raw_tasks(in_path)
    if args.limit and args.limit > 0:
        raw = raw[: args.limit]
        print(f"[INFO] Limiting to first {len(raw)} tasks (--limit)")

    stats = ConvertStats()
    records: list[dict] = []
    for rec in raw:
        ex = convert_record(
            rec,
            img_dir,
            aliases=aliases,
            exclude=exclude,
            include_background_o=args.include_background_o,
            stats=stats,
        )
        if ex:
            records.append(ex)

    print(f"\nConverted {len(records)} / {len(raw)} records.")
    print_stats(stats)

    if args.strict_images and stats.images_missing:
        print(
            "\n[ERROR] Missing images — refusing to write an incomplete dataset. "
            "Copy Label Studio media into --images (basename must match) and re-run.",
            file=sys.stderr,
        )
        report_path = out_path.parent / "conversion_report.json"
        report_path.write_text(
            json.dumps(
                {
                    "converted": len(records),
                    "missing_images": stats.images_missing,
                    "unknown_labels": dict(stats.unknown_labels),
                    "status": "failed_missing_images",
                },
                indent=2,
            ),
            encoding="utf-8",
        )
        print(f"Wrote failure report → {report_path}")
        sys.exit(3)

    if stats.unknown_labels:
        print(
            "\n[ERROR] Unknown labels present — update training/configs/label_aliases.yaml "
            "or TOKEN_LABELS. Refusing to write dataset.",
            file=sys.stderr,
        )
        sys.exit(2)

    if not records:
        print("[ERROR] No records converted.", file=sys.stderr)
        sys.exit(1)

    with out_path.open("w", encoding="utf-8") as f:
        for r in records:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
    print(f"Saved → {out_path}")

    split_report = None
    if args.split:
        print("Writing group-aware train/val/test splits…")
        split_report = write_group_aware_splits(
            records, out_path.parent, args.train, args.val, args.seed
        )

    maps = {
        "doc_classes": DOC_CLASSES,
        "token_labels": LABEL2ID,
        "id2label": {str(k): v for k, v in ID2LABEL.items()},
        "match_strategy": MATCH_STRATEGY,
        "aliases": aliases,
    }
    (out_path.parent / "label_map.json").write_text(
        json.dumps(maps, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    print("Label maps →", out_path.parent / "label_map.json")

    report = {
        "converted": len(records),
        "input": str(in_path),
        "images": str(img_dir),
        "include_background_o": args.include_background_o,
        "images_processed": stats.images_processed,
        "tokens_generated": stats.tokens_generated,
        "tokens_labeled": stats.tokens_labeled,
        "tokens_o": stats.tokens_o,
        "ocr_regions_with_text": stats.ocr_regions_with_text,
        "ocr_regions_with_no_text": stats.ocr_regions_with_no_text,
        "aliased_labels": dict(stats.aliased_labels),
        "split": split_report,
        "label_distribution": dict(Counter(r["source"] for r in records)),
        "status": "ok",
    }
    report_path = out_path.parent / "conversion_report.json"
    report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"Report → {report_path}")


if __name__ == "__main__":
    main()
