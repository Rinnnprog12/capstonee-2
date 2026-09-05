# Start image server + Label Studio for SF08 labeling (Windows-friendly).
# Terminal 1: .\start_labeling.ps1
# Or run serve + label-studio in two terminals manually.

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
Set-Location $Root

Write-Host "=== TSU SF08 Labeling ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Generating HTTP import file..." -ForegroundColor Yellow
& ".\.venv\Scripts\python.exe" -m training.scripts.fix_ls_import_paths --mode http

Write-Host ""
Write-Host "Starting image server on http://127.0.0.1:9090 ..." -ForegroundColor Green
Write-Host "Import: data\raw\label_studio_sf08_import_http.json" -ForegroundColor Green
Write-Host ""
Write-Host "In another terminal run: label-studio start" -ForegroundColor Yellow
Write-Host ""

& ".\.venv\Scripts\python.exe" -m training.scripts.serve_label_images
