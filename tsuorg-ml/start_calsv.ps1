# TSU-ORGDOCX — start CALSV engine (FastAPI / LayoutLMv3)
# Run from tsuorg-ml\:  .\start_calsv.ps1
#
# Prerequisites:
#   1. Tesseract 5.x installed (default path C:\Program Files\Tesseract-OCR\)
#   2. .venv present (run setup_training.ps1 once if not)
#   3. Model trained (training/clean-data/ has eval_report.json)
#      If not trained yet → CALSV falls back to rule-based validation.

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
Set-Location $Root

# ── 1. Activate virtual environment ──────────────────────────────────────────
if (-not (Test-Path ".\.venv\Scripts\Activate.ps1")) {
    Write-Host "[ERROR] .venv not found. Run setup_training.ps1 first." -ForegroundColor Red
    exit 1
}
.\.venv\Scripts\Activate.ps1

# ── 2. Check model checkpoint ─────────────────────────────────────────────────
$modelPath = Join-Path $Root "models\layoutlmv3-tsu-v1\best\config.json"
if (Test-Path $modelPath) {
    Write-Host "[OK]  Model checkpoint found: models\layoutlmv3-tsu-v1\best\" -ForegroundColor Green
} else {
    Write-Host "[WARN] Model not found at models\layoutlmv3-tsu-v1\best\" -ForegroundColor Yellow
    Write-Host "       CALSV will run rule-based validation until finetune.py is executed." -ForegroundColor Yellow
}

# ── 3. Check Tesseract ────────────────────────────────────────────────────────
$tessExe = "C:\Program Files\Tesseract-OCR\tesseract.exe"
if (Test-Path $tessExe) {
    $tessVer = & $tessExe --version 2>&1 | Select-Object -First 1
    Write-Host "[OK]  Tesseract: $tessVer" -ForegroundColor Green
} else {
    Write-Host "[WARN] Tesseract not found at default path." -ForegroundColor Yellow
    Write-Host "       Install from https://github.com/UB-Mannheim/tesseract/wiki" -ForegroundColor Yellow
    Write-Host "       Or update CALSV_TESSERACT_CMD in .env" -ForegroundColor Yellow
}

# ── 4. Start FastAPI ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Starting CALSV Engine ===" -ForegroundColor Cyan
Write-Host "  API:    http://localhost:8000"
Write-Host "  Docs:   http://localhost:8000/docs"
Write-Host "  Health: http://localhost:8000/health"
Write-Host ""
Write-Host "Backend must point to:  Calsv:BaseUrl = http://localhost:8000" -ForegroundColor Yellow
Write-Host "Press Ctrl+C to stop."
Write-Host ""

$env:PYTHONUTF8 = "1"
$env:TOKENIZERS_PARALLELISM = "false"
$env:HF_HUB_DISABLE_SYMLINKS_WARNING = "1"

python -m uvicorn app.main:app --host 0.0.0.0 --port 8000 --reload
