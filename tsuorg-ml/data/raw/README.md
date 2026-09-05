# Training data (generated)

Generated PNGs and Label Studio import JSONs stay **local** (gitignored). Templates in
`data/templates/` are tracked.

## SF08 labeling

1. Keep the image server running:

```powershell
cd d:\Development\projects\tsuorg\tsuorg-ml
.\.venv\Scripts\Activate.ps1
python -m training.scripts.serve_label_images
```

2. In another terminal: `label-studio start`

3. Paste `data/templates/label_studio_config.xml` into Labeling Interface.

4. **Import:** `data/raw/label_studio_sf08_import.json`

5. Review/correct boxes, then **Submit**. Export later as `label_studio_export.json`.

## Accomplishment Report (AR / SF-06) labeling

1. Convert PDFs → PNG (AR narrative packages only, or all accomplishment sources):

```powershell
python -m training.scripts.setup_dataset --type accomplishment --category ar_narrative
# optional Artist Circle SF06:
# python -m training.scripts.setup_dataset --type accomplishment --category artist_circle_sf06
```

2. Pre-label with page-type templates (cover / summary / activity):

```powershell
python -m training.scripts.prelabel_ar_template
```

Images are stored per org (same names as `dataset/AR`) to avoid mixing:

```
data/raw/images/accomplishment/
  Org 1 (MAHARLIKA)/
  Org 2 (tsu communicators guild)/
  ...
  Org 9/
```

If PNGs are still flat, re-group + sync to the Label Studio clone:

```powershell
python training/scripts/sync_ar_to_clone.py
```

3. Label Studio project:
   - Paste `data/templates/label_studio_config_ar.xml`
   - Import `data/raw/label_studio_ar_import.json`
   - Keep `python -m training.scripts.serve_label_images` running

4. Per page: confirm `ACCOMPLISHMENT`, fix boxes/text, Submit.

Template boxes live in `data/templates/ar_page_templates.json` (edit percent coords if a layout drifts).

## Supporting documents (photos / certificates / attendance)

Separate Label Studio project (do **not** mix with SF08 or AR narrative labels).

1. Paste `data/templates/label_studio_config_supporting.xml`
2. Per page, set doc type:
   - `PHOTO_DOCUMENTATION` — Header + Photo Documentation (+ Pictures)
   - `CERTIFICATE` — Header, Certificate Title, Recipient, Purpose, date and venue, signatures
   - `ATTENDANCE` — Title, Date, Name, Team, Signatures
3. Draw boxes using the labels from your annotations; type transcription where text is readable
4. Reference percent layouts: `data/templates/supporting_page_templates.json`

| Your annotation | Label Studio value |
|-----------------|--------------------|
| Header | `HEADER` |
| Photo Documentation | `PHOTO_DOCUMENTATION` |
| pictures | `PICTURES` |
| Certificate Title | `CERTIFICATE_TITLE` |
| Recipient | `RECIPIENT` |
| Purpose | `PURPOSE` |
| date and venue | `DATE_AND_VENUE` |
| signatures / Signatures | `SIGNATURES` |
| Title | `TITLE` |
| Date | `DATE` |
| Name | `NAME` |
| (Team column) | `TEAM` |

## Files in this folder

| File | Keep in git? | Purpose |
|------|--------------|---------|
| `images/sf08/` | **No** (gitignore) | SF08 page PNGs |
| `images/accomplishment/Org 1…9/` | **No** (gitignore) | AR page PNGs (same org folders as `dataset/AR`) |
| `label_studio_sf08_import.json` | **No** | SF08 pre-labeled tasks |
| `label_studio_ar_import.json` | **No** | AR pre-labeled tasks |
| `prelabel_*_report.json` | **No** | Detection stats |
| `manifest.jsonl` / `inventory.json` | **No** | Conversion log |
| `README.md` | Yes | This guide |

## Re-run pre-label after template changes

```powershell
python -m training.scripts.prelabel_sf08_template
python -m training.scripts.prelabel_ar_template
```
