# TSU-ORGDOCX ML — CALSV Engine

Python microservice: OpenCV → **Tesseract 5.x + tessdata_best** → LayoutLMv3.

## 1. Install Tesseract (best accuracy)

1. Download **Tesseract 5.x** Windows installer (UB Mannheim):  
   https://github.com/UB-Mannheim/tesseract/wiki  
   Example: `tesseract-ocr-w64-setup-5.5.x.exe`
2. Install to `C:\Program Files\Tesseract-OCR\`
3. Install **tessdata_best** (highest quality LSTM models — slower but more accurate than default/`tessdata_fast`):

```powershell
# Create folder
New-Item -ItemType Directory -Force "C:\Program Files\Tesseract-OCR\tessdata_best"

# Download eng.traineddata from tessdata_best (run in PowerShell as Admin if needed)
Invoke-WebRequest `
  -Uri "https://github.com/tesseract-ocr/tessdata_best/raw/main/eng.traineddata" `
  -OutFile "C:\Program Files\Tesseract-OCR\tessdata_best\eng.traineddata"
```

4. Verify:

```powershell
& "C:\Program Files\Tesseract-OCR\tesseract.exe" --version
# Should show tesseract 5.x
```

5. Copy env template:

```powershell
cd d:\Development\projects\tsuorg\tsuorg-ml
copy .env.example .env
```

## 2. Run ML service

```powershell
cd d:\Development\projects\tsuorg\tsuorg-ml
python -m venv .venv
.\.venv\Scripts\activate
pip install -r requirements.txt
uvicorn app.main:app --reload --port 8000
```

- Health: `GET http://localhost:8000/health`
- Validate: `POST http://localhost:8000/v1/validate`

OCR config defaults (accuracy-first):
| Setting | Value | Why |
|---------|-------|-----|
| Version | **5.x** | LSTM neural OCR |
| Traineddata | **tessdata_best** | Best accuracy (vs fast/default) |
| OEM | **1** | LSTM only |
| PSM | **6** | Uniform form text block |
| DPI hint | **300** | Matches Layer 1 preprocess |

See `CLAUDE.md` and `/docs/06-CALSV-ENGINE.md`.

## 3. Datasets & training

Drop labeled samples under `data/raw/` — full instructions in **[DATASET.md](DATASET.md)**.

```powershell
python -m training.scripts.prepare_dataset --input data/raw/label_studio_export.json --images data/raw/images --output data/processed/dataset.jsonl --split
python -m training.scripts.finetune --config training/configs/layoutlmv3_base.yaml --data data/processed/
```

Without a trained checkpoint, `/v1/validate` still returns **Tesseract OCR text** + rule-based validation (wizard OCR step shows `ocr.full_text`).
