# setup-ffmpeg.ps1
# Downloads and installs FFmpeg shared libraries for DaroEngine2.
# Run this once to enable MOV/ProRes/Animation codec support in the video player.
#
# Usage: powershell -ExecutionPolicy Bypass -File setup-ffmpeg.ps1

$ErrorActionPreference = "Stop"

$TargetDir = Join-Path $PSScriptRoot "ThirdParty\ffmpeg"
$TempZip = Join-Path $env:TEMP "ffmpeg-shared.zip"
$TempDir = Join-Path $env:TEMP "ffmpeg-extract"

# FFmpeg 7.1 shared build from BtbN (GitHub, reliable, LGPL)
$Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n7.1-latest-win64-lgpl-shared-7.1.zip"

Write-Host "=== DaroEngine2 FFmpeg Setup ===" -ForegroundColor Cyan
Write-Host "Downloading FFmpeg shared build..."

# Download
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $Url -OutFile $TempZip -UseBasicParsing

Write-Host "Extracting..."
if (Test-Path $TempDir) { Remove-Item $TempDir -Recurse -Force }
Expand-Archive -Path $TempZip -DestinationPath $TempDir

# Find the extracted folder (name varies by version)
$ExtractedDir = Get-ChildItem -Path $TempDir -Directory | Select-Object -First 1

if (-not $ExtractedDir) {
    Write-Error "Failed to find extracted FFmpeg directory"
    exit 1
}

Write-Host "Installing to $TargetDir..."

# Create target directories
New-Item -ItemType Directory -Force -Path "$TargetDir\include" | Out-Null
New-Item -ItemType Directory -Force -Path "$TargetDir\lib" | Out-Null
New-Item -ItemType Directory -Force -Path "$TargetDir\bin" | Out-Null

# Copy headers (include/libav*)
if (Test-Path "$($ExtractedDir.FullName)\include") {
    Copy-Item -Path "$($ExtractedDir.FullName)\include\*" -Destination "$TargetDir\include" -Recurse -Force
    Write-Host "  Headers copied" -ForegroundColor Green
}

# Copy import libraries (lib/*.lib)
if (Test-Path "$($ExtractedDir.FullName)\lib") {
    Copy-Item -Path "$($ExtractedDir.FullName)\lib\*.lib" -Destination "$TargetDir\lib" -Force
    # Also copy .def files if present
    Get-ChildItem "$($ExtractedDir.FullName)\lib\*.def" -ErrorAction SilentlyContinue | Copy-Item -Destination "$TargetDir\lib" -Force
    Write-Host "  Import libraries copied" -ForegroundColor Green
}

# Copy DLLs (bin/*.dll)
if (Test-Path "$($ExtractedDir.FullName)\bin") {
    Copy-Item -Path "$($ExtractedDir.FullName)\bin\*.dll" -Destination "$TargetDir\bin" -Force
    Write-Host "  DLLs copied" -ForegroundColor Green
}

# Cleanup
Remove-Item $TempZip -Force -ErrorAction SilentlyContinue
Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue

# Verify
$headers = Test-Path "$TargetDir\include\libavformat\avformat.h"
$libs = (Get-ChildItem "$TargetDir\lib\*.lib" -ErrorAction SilentlyContinue).Count
$dlls = (Get-ChildItem "$TargetDir\bin\*.dll" -ErrorAction SilentlyContinue).Count

Write-Host ""
Write-Host "=== Verification ===" -ForegroundColor Cyan
Write-Host "  Headers: $(if ($headers) { 'OK' } else { 'MISSING' })"
Write-Host "  Import libs: $libs files"
Write-Host "  DLLs: $dlls files"

if ($headers -and $libs -gt 0 -and $dlls -gt 0) {
    Write-Host ""
    Write-Host "FFmpeg installed successfully!" -ForegroundColor Green
    Write-Host "Rebuild DaroEngine2 to enable FFmpeg video support." -ForegroundColor Yellow
    Write-Host "DLLs will be auto-copied to output by the post-build step." -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Error "FFmpeg installation incomplete. Check the output above."
}
