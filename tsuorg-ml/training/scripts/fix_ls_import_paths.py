"""
fix_ls_import_paths.py — Rewrite image paths for Label Studio local file serving.

Browsers cannot open Windows absolute paths (D:\\...). Label Studio needs:
  /data/local-files/?d=sf08/filename.png

Start Label Studio with:
  $env:LABEL_STUDIO_LOCAL_FILES_SERVING_ENABLED="true"
  $env:LABEL_STUDIO_LOCAL_FILES_DOCUMENT_ROOT="D:\\Development\\projects\\tsuorg\\tsuorg-ml\\data\\raw\\images"
  label-studio start

Usage:
  python -m training.scripts.fix_ls_import_paths
  python -m training.scripts.fix_ls_import_paths --input data/raw/label_studio_sf08_prelabel.json
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def to_local_files_url(abs_path: str, images_root: Path) -> str:
    p = Path(abs_path.replace("\\", "/"))
    try:
        rel = p.resolve().relative_to(images_root.resolve())
    except ValueError:
        # try stripping to sf08/... if path contains it
        parts = p.parts
        for folder in ("sf08", "accomplishment", "accreditation"):
            if folder in parts:
                idx = parts.index(folder)
                rel = Path(*parts[idx:])
                break
        else:
            rel = Path(p.name)
    return f"/data/local-files/?d={rel.as_posix()}"


def to_http_url(abs_path: str, images_root: Path, base_url: str) -> str:
    p = Path(abs_path.replace("\\", "/"))
    try:
        rel = p.resolve().relative_to(images_root.resolve())
    except ValueError:
        parts = p.parts
        for folder in ("sf08", "accomplishment", "accreditation"):
            if folder in parts:
                idx = parts.index(folder)
                rel = Path(*parts[idx:])
                break
        else:
            rel = Path(p.name)
    return f"{base_url.rstrip('/')}/{rel.as_posix()}"


def fix_task(task: dict, images_root: Path, mode: str, base_url: str) -> dict:
    data = task.get("data", {})
    img = data.get("image", "")
    if not img:
        return task
    if mode == "http":
        if img.startswith("http://") or img.startswith("https://"):
            return task
        # resolve from local-files or absolute path
        if img.startswith("/data/local-files/?d="):
            rel = img.split("?d=", 1)[1]
            data["image"] = f"{base_url.rstrip('/')}/{rel}"
        else:
            data["image"] = to_http_url(img, images_root, base_url)
    elif not img.startswith("/data/local-files/"):
        data["image"] = to_local_files_url(img, images_root)
    return task


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", type=Path, default=Path("data/raw/label_studio_sf08_prelabel.json"))
    ap.add_argument("--output", type=Path, default=Path("data/raw/label_studio_sf08_import.json"))
    ap.add_argument("--images-root", type=Path, default=Path("data/raw/images"))
    ap.add_argument(
        "--mode",
        choices=("local", "http"),
        default="local",
        help="local = /data/local-files/ URLs; http = http://127.0.0.1:9090/... (recommended on Windows)",
    )
    ap.add_argument("--base-url", default="http://127.0.0.1:9090", help="Used with --mode http")
    args = ap.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    ml_root = Path(__file__).resolve().parents[2]
    in_path = (ml_root / args.input).resolve()
    out_path = (ml_root / args.output).resolve()
    images_root = (ml_root / args.images_root).resolve()

    if args.mode == "http" and args.output == Path("data/raw/label_studio_sf08_import.json"):
        out_path = (ml_root / Path("data/raw/label_studio_sf08_import_http.json")).resolve()

    tasks = json.loads(in_path.read_text(encoding="utf-8"))
    fixed = [fix_task(t, images_root, args.mode, args.base_url) for t in tasks]
    out_path.write_text(json.dumps(fixed, indent=2, ensure_ascii=False), encoding="utf-8")

    sample = fixed[0]["data"]["image"] if fixed else "(empty)"
    print(f"Fixed {len(fixed)} tasks -> {out_path}")
    print(f"Sample image path: {sample}")
    if args.mode == "http":
        print("\n1) Start image server (keep running):")
        print("     python -m training.scripts.serve_label_images")
        print("2) Import into Label Studio:", out_path.name)
        print("3) label-studio start  (separate terminal)")
    else:
        print("\nRestart Label Studio with local files enabled:")
        print('  $env:LABEL_STUDIO_LOCAL_FILES_SERVING_ENABLED="true"')
        print(f'  $env:LABEL_STUDIO_LOCAL_FILES_DOCUMENT_ROOT="{images_root}"')
        print("  label-studio start")
        print("\nThen import:", out_path.name)


if __name__ == "__main__":
    main()
