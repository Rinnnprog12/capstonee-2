"""
validate_annotations.py — Inspect Label Studio JSON exports before conversion.

Does NOT modify clean-dataset/. Reports structure, labels, boxes, image refs.

Usage:
  python -m training.scripts.validate_annotations \\
    --input clean-dataset/accomplishment-report/john-lloyd \\
    --images data/raw/images
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path
from typing import Any

from training.utils.label_vocab import (
    DOC_CLASSES,
    KNOWN_TAGS,
    load_label_aliases,
    resolve_token_label,
)


def _load_tasks(path: Path) -> list[dict[str, Any]]:
    if path.is_dir():
        tasks: list[dict[str, Any]] = []
        for p in sorted(path.rglob("*.json")):
            raw = json.loads(p.read_text(encoding="utf-8"))
            chunk = raw if isinstance(raw, list) else [raw]
            for t in chunk:
                if isinstance(t, dict):
                    t = dict(t)
                    t["_source_file"] = str(p)
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


def _basename_image(ref: str) -> str:
    return Path(str(ref).split("?")[0]).name


def _resolve_image(ref: str, images_dir: Path | None) -> Path | None:
    if not ref or not images_dir:
        return None
    name = _basename_image(ref)
    if not name:
        return None
    candidates = [
        images_dir / name,
        images_dir / Path(ref).name,
    ]
    # Also search one level deep (sf08/, ar/, …)
    if images_dir.exists():
        for hit in images_dir.rglob(name):
            return hit
    for c in candidates:
        if c.exists():
            return c
    return None


def validate(tasks: list[dict[str, Any]], images_dir: Path | None) -> dict[str, Any]:
    aliases_cfg = load_label_aliases()
    aliases = aliases_cfg["aliases"]
    exclude = aliases_cfg["exclude"]

    stats: dict[str, Any] = {
        "total_tasks": len(tasks),
        "annotated_tasks": 0,
        "total_annotations": 0,
        "valid_annotations": 0,
        "missing_labels": 0,
        "unknown_labels": Counter(),
        "aliased_labels": Counter(),
        "excluded_labels": Counter(),
        "invalid_bounding_boxes": 0,
        "skipped_annotations": 0,
        "with_transcription": 0,
        "box_only": 0,
        "doc_types": Counter(),
        "unknown_doc_types": Counter(),
        "field_labels_raw": Counter(),
        "field_labels_resolved": Counter(),
        "image_refs_missing": [],
        "image_refs_found": 0,
        "image_refs_total": 0,
        "result_types": Counter(),
    }

    for task in tasks:
        anns = task.get("annotations") or task.get("completions") or []
        if not anns or not (anns[-1].get("result") or []):
            stats["skipped_annotations"] += 1
            continue
        stats["annotated_tasks"] += 1
        results = anns[-1].get("result") or []

        data = task.get("data") if isinstance(task.get("data"), dict) else {}
        image_ref = str(task.get("image") or data.get("image") or "")
        if image_ref:
            stats["image_refs_total"] += 1
            resolved = _resolve_image(image_ref, images_dir)
            if resolved:
                stats["image_refs_found"] += 1
            else:
                stats["image_refs_missing"].append(
                    {
                        "task_id": task.get("id"),
                        "ref": image_ref,
                        "source": task.get("_source_file"),
                    }
                )

        for r in results:
            typ = r.get("type")
            stats["result_types"][typ] += 1
            v = r.get("value") or {}

            if typ in {"choices", "choice"}:
                choices = v.get("choices") or v.get("choice") or []
                if isinstance(choices, str):
                    choices = [choices]
                for c in choices:
                    cu = str(c).strip().upper()
                    if cu in DOC_CLASSES:
                        stats["doc_types"][cu] += 1
                    else:
                        stats["unknown_doc_types"][cu] += 1
                continue

            if typ == "textarea":
                if v.get("text"):
                    stats["with_transcription"] += 1
                continue

            if typ not in {"rectanglelabels", "labels", "rectangle"}:
                stats["skipped_annotations"] += 1
                continue

            stats["total_annotations"] += 1

            # Bounding box
            try:
                x, y, w, h = float(v["x"]), float(v["y"]), float(v["width"]), float(v["height"])
                if w <= 0 or h <= 0 or x < 0 or y < 0 or x + w > 100.01 or y + h > 100.01:
                    stats["invalid_bounding_boxes"] += 1
                    continue
            except (KeyError, TypeError, ValueError):
                stats["invalid_bounding_boxes"] += 1
                continue

            raw_labels = v.get("rectanglelabels") or v.get("labels") or []
            if not raw_labels:
                stats["missing_labels"] += 1
                continue

            raw = str(raw_labels[0])
            stats["field_labels_raw"][raw] += 1
            resolved, status = resolve_token_label(raw, aliases=aliases, exclude=exclude)
            if status == "empty" or status == "missing":
                stats["missing_labels"] += 1
                continue
            if status == "unknown":
                stats["unknown_labels"][raw] += 1
                continue
            if status == "excluded":
                stats["excluded_labels"][raw] += 1
                continue
            if status == "aliased":
                stats["aliased_labels"][f"{raw}→{resolved}"] += 1

            assert resolved is not None
            stats["field_labels_resolved"][resolved] += 1
            stats["valid_annotations"] += 1
            if not (v.get("text") or False):
                stats["box_only"] += 1

    return stats


def print_report(stats: dict[str, Any], *, max_missing: int = 20) -> None:
    print("=== Label Studio annotation validation ===")
    print(f"Total tasks:              {stats['total_tasks']}")
    print(f"Annotated tasks:          {stats['annotated_tasks']}")
    print(f"Total annotations:        {stats['total_annotations']}")
    print(f"Valid annotations:        {stats['valid_annotations']}")
    print(f"Missing labels:           {stats['missing_labels']}")
    print(f"Unknown labels:           {sum(stats['unknown_labels'].values())}  {dict(stats['unknown_labels'])}")
    print(f"Aliased labels:           {dict(stats['aliased_labels'])}")
    print(f"Excluded labels:          {dict(stats['excluded_labels'])}")
    print(f"Invalid bounding boxes:   {stats['invalid_bounding_boxes']}")
    print(f"Skipped annotations:      {stats['skipped_annotations']}")
    print(f"Box-only (no text):       {stats['box_only']}")
    print(f"With transcription:       {stats['with_transcription']}")
    print(f"Doc types:                {dict(stats['doc_types'])}")
    print(f"Unknown doc types:        {dict(stats['unknown_doc_types'])}")
    print(f"Result types:             {dict(stats['result_types'])}")
    print(f"Resolved field labels:    {dict(stats['field_labels_resolved'])}")
    print(
        f"Images: found={stats['image_refs_found']} / "
        f"total={stats['image_refs_total']}  "
        f"missing={len(stats['image_refs_missing'])}"
    )
    for miss in stats["image_refs_missing"][:max_missing]:
        print(f"  MISSING image task={miss['task_id']} ref={miss['ref']}")
    if len(stats["image_refs_missing"]) > max_missing:
        print(f"  … and {len(stats['image_refs_missing']) - max_missing} more")

    # Vocabulary coverage
    unused = sorted(KNOWN_TAGS - set(stats["field_labels_resolved"]))
    print(f"TOKEN_LABELS unused in this export: {unused[:30]}{'…' if len(unused) > 30 else ''}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True, help="LS JSON file or directory of exports")
    ap.add_argument("--images", default=None, help="Local image root for path resolution")
    ap.add_argument("--json-out", default=None, help="Optional path to write full report JSON")
    ap.add_argument("--fail-on-unknown", action="store_true")
    ap.add_argument("--fail-on-missing-images", action="store_true")
    args = ap.parse_args()

    in_path = Path(args.input)
    if not in_path.exists():
        print(f"[ERROR] Input not found: {in_path}", file=sys.stderr)
        sys.exit(1)

    images_dir = Path(args.images) if args.images else None
    tasks = _load_tasks(in_path)
    stats = validate(tasks, images_dir)
    print_report(stats)

    if args.json_out:
        out = Path(args.json_out)
        out.parent.mkdir(parents=True, exist_ok=True)
        serializable = {
            **stats,
            "unknown_labels": dict(stats["unknown_labels"]),
            "aliased_labels": dict(stats["aliased_labels"]),
            "excluded_labels": dict(stats["excluded_labels"]),
            "doc_types": dict(stats["doc_types"]),
            "unknown_doc_types": dict(stats["unknown_doc_types"]),
            "field_labels_raw": dict(stats["field_labels_raw"]),
            "field_labels_resolved": dict(stats["field_labels_resolved"]),
            "result_types": dict(stats["result_types"]),
        }
        out.write_text(json.dumps(serializable, indent=2), encoding="utf-8")
        print(f"Wrote report → {out}")

    exit_code = 0
    if args.fail_on_unknown and stats["unknown_labels"]:
        exit_code = 2
    if args.fail_on_missing_images and stats["image_refs_missing"]:
        exit_code = 3
    if stats["missing_labels"] or stats["invalid_bounding_boxes"]:
        print("[WARN] Structural annotation issues present.")
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
