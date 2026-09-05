# TSU-ORGDOCX — training data setup (Windows)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

Write-Host "=== TSU-ORGDOCX ML training setup ===" -ForegroundColor Cyan

if (-not (Test-Path ".venv\Scripts\Activate.ps1")) {
    Write-Host "Creating venv..."
    python -m venv .venv
}

.\.venv\Scripts\Activate.ps1
pip install -q -r requirements.txt

Write-Host "`nConverting dataset/ -> data/raw/images/ ..." -ForegroundColor Cyan
python -m training.scripts.setup_dataset @args

Write-Host "`nDone. Next steps:" -ForegroundColor Green
Write-Host "  1. pip install label-studio && label-studio start"
Write-Host "  2. Import data/raw/label_studio_import.json"
Write-Host "  3. See data/raw/README.md"
