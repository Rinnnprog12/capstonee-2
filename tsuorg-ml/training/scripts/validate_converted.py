"""
validate_converted.py — Validate LayoutLMv3 JSONL produced by prepare_dataset.

Usage:
  python -m training.scripts.validate_converted --data data/processed/
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


def _load_jsonl(path: Path) -> list[dict[str, Any]]:
    rows = []
    with path.open(encoding="utf-8") as f:
        for i, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError as exc:
                raise ValueError(f"{path}:{i}: {exc}") from exc
    return rows


def validate_record(rec: dict[str, Any], idx: int) -> list[str]:
    errs: list[str] = []
    for key in ("id", "image_path", "words", "boxes", "word_labels", "doc_label"):
        if key not in rec:
            errs.append(f"[{idx}] missing key {key}")
    if errs:
        return errs

    words = rec["words"]
    boxes = rec["boxes"]
    labels = rec["word_labels"]
    if not (len(words) == len(boxes) == len(labels)):
        errs.append(
            f"[{idx}] length mismatch words={len(words)} boxes={len(boxes)} labels={len(labels)}"
        )

    img = Path(rec["image_path"])
    if not img.exists():
        errs.append(f"[{idx}] image missing: {img}")

    for bi, box in enumerate(boxes):
        if not isinstance(box, (list, tuple)) or len(box) != 4:
            errs.append(f"[{idx}] box[{bi}] not length-4")
            continue
        x0, y0, x1, y1 = box
        for v in (x0, y0, x1, y1):
            if not isinstance(v, (int, float)) or v < 0 or v > 1000:
                errs.append(f"[{idx}] box[{bi}] out of 0–1000: {box}")
                break
        if x1 < x0 or y1 < y0:
            errs.append(f"[{idx}] box[{bi}] inverted: {box}")

    if all(l == 0 for l in labels) and labels:
        errs.append(f"[{idx}] all labels are O — possible conversion bug")

    return errs


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True, help="Directory with dataset.jsonl and/or splits")
    ap.add_argument("--max-errors", type=int, default=50)
    args = ap.parse_args()

    data_dir = Path(args.data)
    if not data_dir.exists():
        print(f"[ERROR] Not found: {data_dir}", file=sys.stderr)
        sys.exit(1)

    files = []
    for name in ("dataset.jsonl", "train.jsonl", "val.jsonl", "test.jsonl"):
        p = data_dir / name
        if p.exists():
            files.append(p)
    if not files:
        print("[ERROR] No JSONL files found.", file=sys.stderr)
        sys.exit(1)

    label_map_path = data_dir / "label_map.json"
    label_map = json.loads(label_map_path.read_text()) if label_map_path.exists() else None

    all_errs: list[str] = []
    totals = Counter()
    group_splits: dict[str, set[str]] = defaultdict(set)

    for path in files:
        rows = _load_jsonl(path)
        split_name = path.stem
        print(f"\n=== {path.name}: {len(rows)} records ===")
        o_count = labeled = 0
        sources = Counter()
        for i, rec in enumerate(rows):
            all_errs.extend(validate_record(rec, i))
            sources[rec.get("source", "?")] += 1
            for lab in rec.get("word_labels") or []:
                if lab == 0:
                    o_count += 1
                else:
                    labeled += 1
            gid = rec.get("group_id")
            if gid:
                group_splits[gid].add(split_name)
        print(f"  sources: {dict(sources)}")
        print(f"  tokens labeled={labeled} O={o_count}")
        totals[split_name] = len(rows)

    leakage = {g: sorted(s) for g, s in group_splits.items() if len(s) > 1}
    print("\n=== Split summary ===")
    print(dict(totals))
    print(f"Potential group leakage: {leakage if leakage else 'none'}")
    if label_map:
        print(f"label_map token classes: {len(label_map.get('token_labels', {}))}")
        print(f"match_strategy: {label_map.get('match_strategy')}")

    if all_errs:
        print(f"\n[FAIL] {len(all_errs)} validation errors:")
        for e in all_errs[: args.max_errors]:
            print(" ", e)
        sys.exit(1)

    print("\n[OK] Converted dataset validation passed.")
    sys.exit(0)


if __name__ == "__main__":
    main()
