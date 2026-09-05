# Start image server for Accomplishment Report (AR / SF-06) labeling.
# Terminal 1: .\start_labeling_ar.ps1
# Terminal 2: label-studio start  (paste data/templates/label_studio_config_ar.xml)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
Set-Location $Root

Write-Host "=== TSU AR (SF-06) Labeling ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Import file: data\raw\label_studio_ar_import.json" -ForegroundColor Green
Write-Host "Config XML:  data\templates\label_studio_config_ar.xml" -ForegroundColor Green
Write-Host ""
Write-Host "Starting image server on http://127.0.0.1:9090 ..." -ForegroundColor Green
Write-Host "In another terminal run: label-studio start" -ForegroundColor Yellow
Write-Host ""

& ".\.venv\Scripts\python.exe" -m training.scripts.serve_label_images
