"""
Reorganize flat accomplishment PNGs into Org folders (mirror dataset/AR),
rewrite Label Studio AR import paths, and sync into tsuorg-ml-clone.
"""

from __future__ import annotations

import json
import shutil
import urllib.parse
from pathlib import Path

SRC_ML = Path(r"d:\Development\projects\tsuorg\tsuorg-ml")
CLONE_ML = Path(r"d:\Development\projects\tsuorg-clone\tsuorg-ml-clone")

SRC_IMAGES = SRC_ML / "data" / "raw" / "images" / "accomplishment"
SRC_IMPORT = SRC_ML / "data" / "raw" / "label_studio_ar_import.json"
SRC_PRELABEL = SRC_ML / "data" / "raw" / "prelabel_ar_report.json"
SRC_AR_XML = SRC_ML / "data" / "templates" / "label_studio_config_ar.xml"
SRC_AR_TMPL = SRC_ML / "data" / "templates" / "ar_page_templates.json"

# filename stem slug (middle segment) -> Org folder name matching dataset/AR
PREFIX_TO_ORG = {
    "ar-narative-maharlikaa": "Org 1 (MAHARLIKA)",
    "ar-narative-tsu-communicators-guild": "Org 2 (tsu communicators guild)",
    "ar-narrative-asfe-coed": "Org 3 (ASFE CoED)",
    "ar-educ": "Org 4",
    "ar-cba": "Org 5",
    "econ-ar": "Org 6",
    "cass-ar": "Org 7",
    "coese-ar": "Org 8",
    "maharlika-ar": "Org 9",
}


def org_for_filename(name: str) -> str | None:
    # ar-narrative__<slug>__p01.png
    parts = name.split("__")
    if len(parts) < 3:
        return None
    slug = parts[1]
    return PREFIX_TO_ORG.get(slug)


def reorganize_source() -> dict[str, int]:
    counts: dict[str, int] = {org: 0 for org in PREFIX_TO_ORG.values()}
    unknown: list[str] = []

    pngs = sorted(SRC_IMAGES.glob("*.png"))
    if not pngs:
        # maybe already organized
        existing_orgs = [p for p in SRC_IMAGES.iterdir() if p.is_dir()]
        if existing_orgs:
            print(f"Already organized under {len(existing_orgs)} org folders.")
            for d in sorted(existing_orgs):
                n = len(list(d.glob("*.png")))
                counts[d.name] = n
                print(f"  {d.name}: {n}")
            return counts
        raise SystemExit(f"No PNGs found in {SRC_IMAGES}")

    for png in pngs:
        org = org_for_filename(png.name)
        if not org:
            unknown.append(png.name)
            continue
        dest_dir = SRC_IMAGES / org
        dest_dir.mkdir(parents=True, exist_ok=True)
        dest = dest_dir / png.name
        if dest.exists():
            png.unlink()  # duplicate flat copy
        else:
            shutil.move(str(png), str(dest))
        counts[org] += 1

    if unknown:
        print("WARNING unknown files:", unknown[:10])
    return counts


def rewrite_import_paths(import_path: Path, port: int = 9090) -> int:
    tasks = json.loads(import_path.read_text(encoding="utf-8"))
    updated = 0
    for task in tasks:
        data = task.get("data") or {}
        img = data.get("image") or ""
        # extract filename from any URL / path form
        name = Path(urllib.parse.unquote(img.split("?")[-1] if "?d=" in img else img)).name
        # also handle .../accomplishment/Org.../file.png
        if "/" in img.replace("\\", "/"):
            name = img.replace("\\", "/").rstrip("/").split("/")[-1]
            name = urllib.parse.unquote(name)

        org = org_for_filename(name)
        if not org:
            continue

        # Prefer HTTP paths for Windows Label Studio (same as current AR import)
        rel = f"accomplishment/{org}/{name}"
        # URL-encode path segments but keep slashes
        encoded = "/".join(urllib.parse.quote(seg, safe="") for seg in rel.split("/"))
        new_url = f"http://127.0.0.1:{port}/{encoded}"
        if data.get("image") != new_url:
            data["image"] = new_url
            updated += 1
        # optional org hint for labeling UI / filtering
        data["org_hint"] = org

    import_path.write_text(json.dumps(tasks, indent=2, ensure_ascii=False), encoding="utf-8")
    return updated


def sync_to_clone(port: int = 9091) -> None:
    clone_images = CLONE_ML / "data" / "raw" / "images" / "accomplishment"
    clone_images.mkdir(parents=True, exist_ok=True)

    # clear previous flat/empty contents except keep structure we copy
    for child in clone_images.iterdir():
        if child.name == ".gitkeep":
            continue
        if child.is_file():
            child.unlink()
        elif child.is_dir():
            shutil.rmtree(child)

    # copy org folders
    for org_dir in sorted(p for p in SRC_IMAGES.iterdir() if p.is_dir()):
        dest = clone_images / org_dir.name
        print(f"Copying {org_dir.name} ...")
        shutil.copytree(org_dir, dest)

    # import JSON (rewrite to clone port 9091)
    clone_import = CLONE_ML / "data" / "raw" / "label_studio_ar_import.json"
    shutil.copy2(SRC_IMPORT, clone_import)
    n = rewrite_import_paths(clone_import, port=port)
    print(f"Clone import rewritten ({n} paths) -> port {port}")

    # templates
    clone_templates = CLONE_ML / "data" / "templates"
    clone_templates.mkdir(parents=True, exist_ok=True)
    shutil.copy2(SRC_AR_XML, clone_templates / "label_studio_config_ar.xml")
    if SRC_AR_TMPL.exists():
        shutil.copy2(SRC_AR_TMPL, clone_templates / "ar_page_templates.json")

    # prelabel report (optional reference)
    if SRC_PRELABEL.exists():
        shutil.copy2(SRC_PRELABEL, CLONE_ML / "data" / "raw" / "prelabel_ar_report.json")

    # lightweight dataset/AR mirror (folder stubs + note) so structure matches source kit
    clone_dataset_ar = CLONE_ML / "dataset" / "AR"
    src_dataset_ar = SRC_ML / "dataset" / "AR"
    if src_dataset_ar.exists():
        clone_dataset_ar.mkdir(parents=True, exist_ok=True)
        for org in sorted(p for p in src_dataset_ar.iterdir() if p.is_dir()):
            (clone_dataset_ar / org.name).mkdir(parents=True, exist_ok=True)
            # copy only small pointer README, not huge PDFs (images are what LS needs)
            note = clone_dataset_ar / org.name / "README.txt"
            note.write_text(
                f"Source PDFs live in tsuorg-ml/dataset/AR/{org.name}/\n"
                f"Labeled page images: data/raw/images/accomplishment/{org.name}/\n",
                encoding="utf-8",
            )


def write_clone_ar_readme() -> None:
    readme = CLONE_ML / "data" / "raw" / "README.md"
    text = r"""# Training images for Label Studio clone

Keep `.\start_labeling.ps1` running (http://127.0.0.1:9091) while labeling.

## SF08

Import:

```
data/raw/label_studio_sf08_import.json
```

Labeling XML: `data/templates/label_studio_config.xml`

Images: `data/raw/images/sf08/`

## Accomplishment Report (AR)

Import:

```
data/raw/label_studio_ar_import.json
```

Labeling XML: `data/templates/label_studio_config_ar.xml`

Images (organized like `dataset/AR`):

```
data/raw/images/accomplishment/
  Org 1 (MAHARLIKA)/
  Org 2 (tsu communicators guild)/
  Org 3 (ASFE CoED)/
  Org 4/
  Org 5/
  Org 6/
  Org 7/
  Org 8/
  Org 9/
```

Create a **separate Label Studio project** for AR (do not mix with SF08).
"""
    readme.write_text(text, encoding="utf-8")

    # append AR section to main README if missing
    main = CLONE_ML / "README.md"
    main_text = main.read_text(encoding="utf-8")
    if "Accomplishment Report" not in main_text:
        main_text = main_text.rstrip() + """

## Accomplishment Report (AR) labeling

Same SETUP / START as SF08. Then create a **new Label Studio project**:

1. **Settings → Labeling Interface → Code** — paste `data\\templates\\label_studio_config_ar.xml`
2. **Import** `data\\raw\\label_studio_ar_import.json`
3. Keep the image server window open (serves `data\\raw\\images\\accomplishment\\Org …\\`)

AR page images are grouped by org (same names as `dataset\\AR`) to avoid mixing SF08 / orgs.
"""
        main.write_text(main_text + "\n", encoding="utf-8")


def main() -> None:
    print("=== Reorganize source accomplishment images ===")
    counts = reorganize_source()
    total = sum(counts.values())
    print(f"Total PNGs in org folders: {total}")
    for org, n in counts.items():
        print(f"  {org}: {n}")

    print("\n=== Rewrite source AR import JSON (port 9090) ===")
    n = rewrite_import_paths(SRC_IMPORT, port=9090)
    print(f"Updated {n} image paths in {SRC_IMPORT.name}")
    sample = json.loads(SRC_IMPORT.read_text(encoding="utf-8"))[0]["data"]["image"]
    print(f"Sample: {sample}")

    print("\n=== Sync to tsuorg-ml-clone ===")
    sync_to_clone(port=9091)
    write_clone_ar_readme()

    # verify clone counts
    clone_root = CLONE_ML / "data" / "raw" / "images" / "accomplishment"
    clone_pngs = list(clone_root.rglob("*.png"))
    print(f"\nClone accomplishment PNGs: {len(clone_pngs)}")
    print("Done.")


if __name__ == "__main__":
    main()
