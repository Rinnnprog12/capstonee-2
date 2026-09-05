# Start Label Studio with local image serving for SF08 labeling.
# Run from tsuorg-ml/:  .\start_label_studio.ps1

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
Set-Location $Root

$imagesRoot = (Join-Path $Root "data\raw\images") -replace '\\', '\\'

# Label Studio reads .env from its data directory on Windows
$lsDataDir = Join-Path $env:LOCALAPPDATA "label-studio\label-studio"
New-Item -ItemType Directory -Force -Path $lsDataDir | Out-Null
$envFile = Join-Path $lsDataDir ".env"
$absImages = (Join-Path $Root "data\raw\images")
@"
LABEL_STUDIO_LOCAL_FILES_SERVING_ENABLED=true
LABEL_STUDIO_LOCAL_FILES_DOCUMENT_ROOT=$absImages
"@ | Set-Content -Path $envFile -Encoding UTF8

# Also set in current process (must be same terminal as label-studio)
$env:LABEL_STUDIO_LOCAL_FILES_SERVING_ENABLED = "true"
$env:LABEL_STUDIO_LOCAL_FILES_DOCUMENT_ROOT = $absImages

Write-Host "=== Label Studio (local files enabled) ===" -ForegroundColor Cyan
Write-Host "  .env written: $envFile"
Write-Host "  DOCUMENT_ROOT: $absImages"
Write-Host ""
Write-Host "After project opens, ALSO do once in UI:" -ForegroundColor Yellow
Write-Host "  Settings -> Cloud Storage -> Add Source Storage -> Local files"
Write-Host "  Absolute local path: $absImages"
Write-Host "  -> Save -> Sync Storage"
Write-Host ""
Write-Host "Import file (if not yet):" -ForegroundColor Green
Write-Host "  data\raw\label_studio_sf08_import.json"
Write-Host ""

if (Test-Path ".\.venv\Scripts\label-studio.exe") {
    & ".\.venv\Scripts\label-studio.exe" start
} else {
    label-studio start
}
