# setup.ps1 — Optional ffmpeg fetch for Contoso Studio
#
# Downloads tools/ffmpeg.exe (gyan.dev essentials build, ~100 MB).
# All other binaries (YOLOS, Whisper, Depth-Anything, ViT) auto-download
# on first use via Services/ModelDownloader.cs.
#
# Run from the repo root:  pwsh -File setup.ps1
# Re-running is safe — already-present files are skipped.

[CmdletBinding()]
param(
    [switch]$Force,
    [string]$FfmpegUrl  = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$repoRoot = $PSScriptRoot
Write-Host "Setting up Contoso Studio in $repoRoot" -ForegroundColor Cyan
Write-Host ''

# ── ffmpeg.exe ────────────────────────────────────────────────────
$toolsDir = Join-Path $repoRoot 'tools'
$ffmpeg   = Join-Path $toolsDir 'ffmpeg.exe'
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

if ((Test-Path $ffmpeg) -and -not $Force) {
    $size = (Get-Item $ffmpeg).Length / 1MB
    Write-Host "[ok]   ffmpeg.exe already present ($([math]::Round($size,1)) MB)" -ForegroundColor Green
} else {
    $tmpZip = Join-Path $env:TEMP "ffmpeg-essentials.zip"
    $tmpDir = Join-Path $env:TEMP "ffmpeg-essentials-extract"
    Write-Host "[get]  ffmpeg essentials build → $ffmpeg" -ForegroundColor Yellow
    Write-Host "       from: $FfmpegUrl"
    Invoke-WebRequest -Uri $FfmpegUrl -OutFile $tmpZip -UseBasicParsing
    if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
    Expand-Archive -Path $tmpZip -DestinationPath $tmpDir
    $extracted = Get-ChildItem -Path $tmpDir -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if (-not $extracted) { throw "Could not find ffmpeg.exe inside the downloaded zip." }
    Copy-Item -Path $extracted.FullName -Destination $ffmpeg -Force
    Remove-Item $tmpZip -Force
    Remove-Item $tmpDir -Recurse -Force
    $size = (Get-Item $ffmpeg).Length / 1MB
    Write-Host "[ok]   ffmpeg.exe installed ($([math]::Round($size,1)) MB)" -ForegroundColor Green
}

Write-Host ''
Write-Host "Other models auto-download on first use to:" -ForegroundColor Gray
Write-Host "  $env:LOCALAPPDATA\Contoso Studio\models\" -ForegroundColor Gray
Write-Host "  (YOLOS ~110 MB, Whisper ~77-768 MB, Depth-Anything ~100 MB, ViT ~88 MB)" -ForegroundColor Gray
Write-Host ''
Write-Host "Run with:  dotnet run -p:Platform=x64" -ForegroundColor Cyan
