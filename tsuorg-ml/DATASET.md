# Dataset & Training Guide (CALSV / LayoutLMv3)

Hand off samples here so we can fine-tune field extraction for SF08, Accomplishment, and Accreditation PDFs/images.

## What you should give me

| Item | Target | Notes |
|------|--------|-------|
| Scanned / filled forms | **~220 per type** (660 total) | PDF or PNG/JPG; clear, upright pages preferred |
| Document type label | `SF08` \| `ACCOMPLISHMENT` \| `ACCREDITATION` | One label per sample |
| Field boxes + text | Label Studio export | See labels below |

**Minimum for a first usable model:** ~50–80 per type (still train; expect lower accuracy).

### Folder drop (recommended)

```
tsuorg-ml/data/raw/
  images/                  ← put page images / PDF page exports here
  label_studio_export.json ← Label Studio JSON (array) export
```

PDFs are fine for **runtime OCR**. For **training**, export each page as an image (PNG/JPG) before Label Studio, or convert with:

```powershell
# Example: poppler + pdf2image offline, or export pages from Acrobat
```

## Label Studio labels (token / BIO)

Use these region labels (exact names):

**SF08 (current labeling):** `FORM_TITLE`, `DATE_PROPOSAL`, `ORG_NAME`, `OBJECTIVES`, `ACTIVITY_TITLE`, `ACTIVITY_DATE`, `VENUE`, `PARTICIPANTS`, `ACTIVITY_MODE`, `ORG_OFFICER_SIGNATURE`, `ADVISER_SIGNATURE`, `SAS_SIGNATURE`

| SF08 label | Where on the form | Example text |
|------------|-------------------|--------------|
| `FORM_TITLE` | Centered request title | REQUEST FOR THE CONDUCT OF STUDENT ACTIVITY WITH BUDGETARY ALLOTMENT |
| `DATE_PROPOSAL` | Date line above the letter | 07/12/2026 |
| `ORG_NAME` | Org name in the body | Programmers' Den |
| `OBJECTIVES` | Letter body paragraph | The Programmers' Den respectfully requests... |
| `ACTIVITY_TITLE` | Table: ACTIVITY | Web and App Development Team Recruitment Assessment |
| `ACTIVITY_DATE` | Table: DATE | July 28, 2026 |
| `VENUE` | Table: VENUE/PLATFORM | CCS AVR |
| `PARTICIPANTS` | Table: NO. OF PARTICIPANTS | N/A |
| `ACTIVITY_MODE` | Table: FACE-TO-FACE/ONLINE | Face-to-face / Online / Hybrid |
| `ORG_OFFICER_SIGNATURE` | Left: President / officer | KHARL P. ASUNCION |
| `ADVISER_SIGNATURE` | Right: Dean / Adviser | PROF. JEROME C. LEGASPI |
| `SAS_SIGNATURE` | Bottom: Approved (SAS Director) | Dr. Andie Rafael E. Quiballo |

**Accomplishment Report / SF-06 (TSU-SOU-SF-06):** page-type templates in `data/templates/ar_page_templates.json`

| AR label | Page | Where | Example |
|----------|------|-------|---------|
| `LOGO_HEADER` | all | University + org logos + header text | TARLAC STATE UNIVERSITY / College of Science / … |
| `ORG_NAME` | cover | Centered org title | CHEMICAL SOCIETY |
| `REPORT_TITLE` | cover | Report title | Accomplishment Report |
| `SEMESTER` | cover | Semester line | 1st Semester |
| `ACADEMIC_YEAR` | cover | A.Y. line | A.Y. 2025-2026 |
| `SUMMARY_TITLE` | summary | Table heading | SUMMARY OF ACTIVITIES (1st SEMESTER …) |
| `ACTIVITY_NO` | summary / activity | Number column or “Activity No. N” | Activity No. 1 |
| `ACTIVITY_NAME` | summary / activity | Name of Activity | Organization Partnership for Aeloria… |
| `ACTIVITY_DATE` | summary / activity | Date | August 4-6, 2025 |
| `VENUE` | summary / activity | Venue | Facebook/Online |
| `INVOLVEMENT` | summary / activity | Organizer / Participant checks | As Participant |
| `LEVEL` | summary / activity | International…Local checks | National |
| `EXTENT_OF_BENEFITS` | summary / activity | Benefits checks | 1 or 2 Departments |
| `NARRATIVE` | activity | Narrative / objective block | (paragraphs) |
| `SECRETARY_SIGNATURE` | summary (sign-off) | Prepared by | Beverly Babe L. Maraña |
| `PRESIDENT_SIGNATURE` | summary (sign-off) | Reviewed by | Rahl Jheron F. Gomez |
| `ADVISER_SIGNATURE` | summary (sign-off) | Approved | Mr. Roosevelt T. Tabag Jr. |

**Supporting documents** (AR attachments — photo / certificate / attendance):  
`data/templates/label_studio_config_supporting.xml` + `data/templates/supporting_page_templates.json`

| Supporting label | Doc subtype | Where | Example |
|------------------|-------------|-------|---------|
| `HEADER` | photo / certificate | Logos + university / college / org / A.Y. | TARLAC STATE UNIVERSITY … |
| `PHOTO_DOCUMENTATION` | photo (with narrative) | Title + description paragraph | Photo Documentation: The Coaching and Mentoring… |
| `PICTURES` | photo | Stacked activity photos block | (image region — transcription optional) |
| `CERTIFICATE_TITLE` | certificate | Main heading | CERTIFICATE OF PARTICIPATION |
| `RECIPIENT` | certificate | Awardee name | TSU COMMUNICATORS’ GUILD |
| `PURPOSE` | certificate | Citation / reason paragraph | For their dedication and support… |
| `DATE_AND_VENUE` | certificate | Issuance line | Given this 23rd of February, 2026 at… |
| `SIGNATURES` | certificate / attendance | Signature block(s) | names + titles / handwritten marks |
| `TITLE` | attendance | Meeting / activity title | Evaluation and Planning w/ Team Heads… |
| `DATE` | attendance | Date line | 01-27-26 (Tuesday) |
| `NAME` | attendance | Names column | Cabigas, Ariana Lyn G. … |
| `TEAM` | attendance | Team / role column | Perf Arts / Production / … |

**Accreditation (forward-compat):** `COLLEGE`, `AY`, `OFFICER_LIST`

Also set a **document-type** choice per task:
- Main forms: `SF08` | `ACCOMPLISHMENT` | `ACCREDITATION`
- Supporting: `PHOTO_DOCUMENTATION` | `CERTIFICATE` | `ATTENDANCE`

Templates:
- SF08: `data/templates/label_studio_config.xml`
- AR: `data/templates/label_studio_config_ar.xml` + `data/templates/ar_page_templates.json`
- Supporting: `data/templates/label_studio_config_supporting.xml` + `data/templates/supporting_page_templates.json`

## Pipeline once datasets are ready

```powershell
cd d:\Development\projects\tsuorg\tsuorg-ml
.\.venv\Scripts\Activate.ps1   # if using venv

# 0) Validate Label Studio exports BEFORE convert/train
python -m training.scripts.validate_annotations `
  --input clean-dataset/accomplishment-report/john-lloyd `
  --images data/raw/images `
  --fail-on-unknown

# Place page images under data/raw/images/ so basenames match
# /data/upload/<project>/<file> → data/raw/images/**/<file>

# 1) Small-sample convert (full-page OCR + per-token boxes + background O)
python -m training.scripts.prepare_dataset `
  --input  clean-dataset/accomplishment-report/john-lloyd `
  --images data/raw/images `
  --output data/processed/dataset.jsonl `
  --split --limit 20

python -m training.scripts.validate_converted --data data/processed/

# 2) Full convert when sample looks correct
python -m training.scripts.prepare_dataset `
  --input  clean-dataset `
  --images data/raw/images `
  --output data/processed/dataset.jsonl `
  --split

# 3) Fine-tune LayoutLMv3 (GPU preferred) — only after validation passes
python -m training.scripts.finetune `
  --config training/configs/layoutlmv3_base.yaml `
  --data   data/processed/

# Weights land in: models/layoutlmv3-tsu-v1/best/
# Runtime auto-loads that best/ folder.

# 4) Evaluate
python -m training.scripts.evaluate `
  --model  models/layoutlmv3-tsu-v1/best `
  --data   data/processed/test.jsonl `
  --labels data/processed/label_map.json

# 5) Restart ML service so /health shows model_loaded=true
uvicorn app.main:app --reload --port 8000
```

Do **not** modify `clean-dataset/` reference exports.  
Label aliases (`TABLE-*`, `ACTIVITY_PHOTO`): `training/configs/label_aliases.yaml`.  
Historical F1≈1.0 metrics are **not** production-ready — see `training/clean-data/HISTORICAL_METRICS.md`.

## Runtime (no training yet)

Without weights, `/v1/validate` still runs:

1. OpenCV preprocess  
2. **Tesseract 5 + tessdata_best** OCR → `ocr.full_text` (Figure 8 wizard preview)  
3. **Rule-based** LayoutLM fallback for completeness checks  

Install Tesseract 5 + `tessdata_best` (see README.md). Backend: `Calsv:BaseUrl` = `http://localhost:8000`.

**Figure 8 (OCR step)** is wired now: per-uploaded-file status, raw text preview, average confidence, key fields, low-confidence flags. Fine-tuned field accuracy improves after you deliver the labeled dataset.

## Accuracy note

- **OCR text preview** = Tesseract (no LayoutLM train required).  
- **Accurate field highlighting / classification** = needs your labeled dataset + `finetune`.
