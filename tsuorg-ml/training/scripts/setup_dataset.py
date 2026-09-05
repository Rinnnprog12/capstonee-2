"""
setup_dataset.py — Prepare tsuorg-ml/data/raw from tsuorg-ml/dataset/.

Scans PDFs/images under dataset/, classifies by document type, converts pages
to PNG (300 DPI), and writes manifests for Label Studio.

Usage:
  python -m training.scripts.setup_dataset
  python -m training.scripts.setup_dataset --category 220_sf08
  python -m training.scripts.setup_dataset --type sf08 --limit 80
  python -m training.scripts.setup_dataset --dry-run
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from training.utils.pdf_pages import image_to_png_bytes, pdf_to_png_pages, slugify

DOC_TYPES = ("SF08", "ACCOMPLISHMENT", "ACCREDITATION")
TYPE_ALIASES = {
    "sf08": "SF08",
    "accomplishment": "ACCOMPLISHMENT",
    "accreditation": "ACCREDITATION",
}


@dataclass
class SourceFile:
    path: Path
    doc_type: str | None
    category: str
    skip_reason: str = ""


@dataclass
class SetupStats:
    converted_pages: int = 0
    skipped_files: int = 0
    errors: list[str] = field(default_factory=list)
    by_type: Counter = field(default_factory=Counter)
    by_category: Counter = field(default_factory=Counter)


def _norm_rel(path: Path, root: Path) -> str:
    return path.relative_to(root).as_posix().lower()


def classify_file(path: Path, dataset_root: Path) -> SourceFile:
    rel = _norm_rel(path, dataset_root)
    name_l = path.name.lower()
    parent_l = path.parent.name.lower()

    # ── 220 SF08 bulk folder ──────────────────────────────────────────────
    if rel.startswith("220 sf08/"):
        return SourceFile(path, "SF08", "220_sf08")

    # ── AR org packages ───────────────────────────────────────────────────
    if rel.startswith("ar/"):
        if parent_l in ("ar",):
            return SourceFile(path, "ACCOMPLISHMENT", "ar_narrative")
        if parent_l in ("supporting",):
            if "sf08" in name_l:
                return SourceFile(path, "SF08", "ar_supporting_sf08")
            return SourceFile(
                path, None, "ar_supporting_other",
                skip_reason="AR supporting doc (not AR narrative or SF08)",
            )
        return SourceFile(path, None, "ar_other", skip_reason="Unclassified AR path")

    # ── Artist Circle (mixed types under one org) ─────────────────────────
    if rel.startswith("artist circle/"):
        if "sf08(request" in rel or rel.split("/")[1].startswith("sf08"):
            return SourceFile(path, "SF08", "artist_circle_sf08")
        if "sf06" in rel or "accomplishment" in rel:
            if "main file" in rel:
                return SourceFile(path, "ACCOMPLISHMENT", "artist_circle_sf06")
            if "supporting attachment" in rel:
                if "sf08" in name_l or "sf08" in rel:
                    return SourceFile(path, "SF08", "artist_circle_sf06_support_sf08")
                return SourceFile(
                    path, None, "artist_circle_sf06_support",
                    skip_reason="SF06 supporting attachment (photos/docs bundle)",
                )
            return SourceFile(path, "ACCOMPLISHMENT", "artist_circle_sf06")
        if "renewal" in rel or "application" in rel:
            return SourceFile(path, "ACCREDITATION", "artist_circle_accreditation")
        return SourceFile(path, None, "artist_circle_other", skip_reason="Unclassified Artist Circle path")

    return SourceFile(path, None, "unclassified", skip_reason="Path not mapped to a doc type")


def collect_sources(dataset_root: Path) -> list[SourceFile]:
    exts = {".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff"}
    files: list[SourceFile] = []
    for path in sorted(dataset_root.rglob("*")):
        if not path.is_file():
            continue
        if path.suffix.lower() not in exts:
            continue
        files.append(classify_file(path, dataset_root))
    return files


def _type_folder(doc_type: str) -> str:
    return doc_type.lower()


def _output_name(src: SourceFile, page_idx: int, total_pages: int) -> str:
    cat = slugify(src.category, max_len=40)
    stem = slugify(src.path.stem, max_len=50)
    if total_pages == 1:
        return f"{cat}__{stem}.png"
    return f"{cat}__{stem}__p{page_idx + 1:02d}.png"


def convert_source(
    src: SourceFile,
    images_root: Path,
    dpi: int,
    stats: SetupStats,
    dry_run: bool,
) -> list[dict]:
    """Convert one source file; return manifest entries (one per page)."""
    entries: list[dict] = []
    if src.doc_type is None:
        stats.skipped_files += 1
        stats.by_category[src.category] += 1
        return entries

    out_dir = images_root / _type_folder(src.doc_type)
    if not dry_run:
        out_dir.mkdir(parents=True, exist_ok=True)

    try:
        suffix = src.path.suffix.lower()
        if suffix == ".pdf":
            if dry_run:
                page_bytes_list: list[bytes] = [b""]
            else:
                page_bytes_list = pdf_to_png_pages(src.path, dpi=dpi)
        else:
            page_bytes_list = [] if dry_run else [image_to_png_bytes(src.path)]
    except Exception as exc:
        stats.errors.append(f"{src.path}: {exc}")
        return entries

    if dry_run:
        out_name = _output_name(src, 0, 1)
        entries.append(_manifest_entry(src, out_dir / out_name, 1, 1))
        stats.converted_pages += 1
        stats.by_type[src.doc_type] += 1
        stats.by_category[src.category] += 1
        return entries

    for i, png_bytes in enumerate(page_bytes_list):
        out_name = _output_name(src, i, len(page_bytes_list))
        out_path = out_dir / out_name
        if out_path.exists():
            n = 2
            while True:
                alt = out_dir / f"{out_path.stem}__{n}{out_path.suffix}"
                if not alt.exists():
                    out_path = alt
                    break
                n += 1
        out_path.write_bytes(png_bytes)
        entries.append(_manifest_entry(src, out_path, i + 1, len(page_bytes_list)))
        stats.converted_pages += 1

    stats.by_type[src.doc_type] += 1
    stats.by_category[src.category] += 1
    return entries


def _manifest_entry(
    src: SourceFile,
    image_path: Path,
    page: int,
    page_count: int,
) -> dict:
    return {
        "id": f"{slugify(src.category)}__{slugify(src.path.stem)}__p{page:02d}",
        "doc_type": src.doc_type,
        "category": src.category,
        "source_pdf": str(src.path.as_posix()),
        "page": page,
        "page_count": page_count,
        "image": str(image_path.as_posix()),
        "label_studio_data": {
            "image": str(image_path.resolve().as_posix()),
            "doc_type_hint": src.doc_type,
        },
    }


def write_label_studio_import(manifest: list[dict], out_path: Path) -> None:
    """Tasks JSON for Label Studio → Import."""
    tasks = []
    for row in manifest:
        tasks.append({
            "data": {
                "image": row["label_studio_data"]["image"],
                "doc_type_hint": row["doc_type"],
            },
        })
    out_path.write_text(json.dumps(tasks, indent=2, ensure_ascii=False), encoding="utf-8")


def main() -> None:
    ap = argparse.ArgumentParser(description="Prepare training images from dataset/")
    ap.add_argument(
        "--dataset-root",
        type=Path,
        default=Path("dataset"),
        help="Root folder with downloaded SOU PDFs (default: dataset/)",
    )
    ap.add_argument(
        "--output",
        type=Path,
        default=Path("data/raw"),
        help="Output root (images/ + manifests written here)",
    )
    ap.add_argument("--dpi", type=int, default=300, help="PDF render DPI (default: 300)")
    ap.add_argument(
        "--type",
        dest="types",
        action="append",
        choices=["sf08", "accomplishment", "accreditation"],
        help="Only process this type (repeatable). Default: all.",
    )
    ap.add_argument(
        "--limit",
        type=int,
        default=0,
        help="Max source files per type (0 = no limit). Useful for MVP pilot.",
    )
    ap.add_argument(
        "--category",
        dest="categories",
        action="append",
        help="Only process this source category (repeatable). Example: 220_sf08",
    )
    ap.add_argument("--dry-run", action="store_true", help="Classify only; do not write PNGs")
    args = ap.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")

    ml_root = Path(__file__).resolve().parents[2]
    dataset_root = (ml_root / args.dataset_root).resolve()
    output_root = (ml_root / args.output).resolve()
    images_root = output_root / "images"

    if not dataset_root.is_dir():
        print(f"[ERROR] Dataset folder not found: {dataset_root}", file=sys.stderr)
        sys.exit(1)

    allowed_types: set[str] | None = None
    if args.types:
        allowed_types = {TYPE_ALIASES[t] for t in args.types}

    sources = collect_sources(dataset_root)
    stats = SetupStats()

    # Apply filters + per-type limits
    type_counts: Counter = Counter()
    to_process: list[SourceFile] = []
    skipped_manifest: list[dict] = []

    for src in sources:
        if args.categories and src.category not in args.categories:
            continue
        if src.doc_type is None:
            skipped_manifest.append({
                "path": str(src.path.as_posix()),
                "category": src.category,
                "reason": src.skip_reason,
            })
            continue
        if allowed_types and src.doc_type not in allowed_types:
            continue
        if args.limit > 0:
            if type_counts[src.doc_type] >= args.limit:
                continue
            type_counts[src.doc_type] += 1
        to_process.append(src)

    manifest: list[dict] = []
    total = len(to_process)
    for i, src in enumerate(to_process, start=1):
        if not args.dry_run:
            print(f"  [{i}/{total}] {src.path.name}", flush=True)
        manifest.extend(convert_source(src, images_root, args.dpi, stats, args.dry_run))

    if not args.dry_run:
        output_root.mkdir(parents=True, exist_ok=True)
        for sub in ("sf08", "accomplishment", "accreditation", "skipped"):
            (images_root / sub).mkdir(parents=True, exist_ok=True)
        (output_root / "processed").mkdir(parents=True, exist_ok=True)

        manifest_path = output_root / "manifest.jsonl"
        with manifest_path.open("w", encoding="utf-8") as f:
            for row in manifest:
                f.write(json.dumps(row, ensure_ascii=False) + "\n")

        write_label_studio_import(manifest, output_root / "label_studio_import.json")

        inventory = {
            "dataset_root": str(dataset_root),
            "images_root": str(images_root),
            "dpi": args.dpi,
            "converted_pages": stats.converted_pages,
            "skipped_files": len(skipped_manifest),
            "errors": stats.errors,
            "by_doc_type": dict(stats.by_type),
            "by_category": dict(stats.by_category),
            "skipped": skipped_manifest,
        }
        (output_root / "inventory.json").write_text(
            json.dumps(inventory, indent=2, ensure_ascii=False), encoding="utf-8"
        )

    print("\n=== Dataset setup ===")
    print(f"  Dataset:   {dataset_root}")
    print(f"  Output:    {output_root}")
    print(f"  Dry run:   {args.dry_run}")
    print(f"  Sources:   {len(to_process)} files -> {stats.converted_pages} page images")
    print(f"  Skipped:   {len(skipped_manifest)} files (supporting/docs)")
    print(f"  By type:   {dict(stats.by_type)}")
    print(f"  By source: {dict(stats.by_category)}")
    if stats.errors:
        print(f"  Errors:    {len(stats.errors)}")
        for e in stats.errors[:10]:
            print(f"    - {e}")
    if not args.dry_run:
        print(f"\n  Manifest:  {output_root / 'manifest.jsonl'}")
        print(f"  LS import: {output_root / 'label_studio_import.json'}")
        print(f"  Inventory: {output_root / 'inventory.json'}")
        print("\nNext: open Label Studio, import label_studio_import.json, label fields.")
        print("      See data/raw/README.md")


if __name__ == "__main__":
    main()
