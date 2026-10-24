<#
  Downloads the external engines Dialvert drives into ./engines.
  FFmpeg (video/audio) and Pandoc (documents). LibreOffice is never bundled -
  Dialvert uses an installed copy if present. Run from the repo root:
    powershell -ExecutionPolicy Bypass -File scripts\fetch-engines.ps1
#>
param(
    [string]$FfmpegAsset = 'ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip',
    [string]$PandocVersion = '3.12.1'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root    = Split-Path -Parent $PSScriptRoot
$engines = Join-Path $root 'engines'
$dl      = Join-Path $engines '_download'
New-Item -ItemType Directory -Force $dl | Out-Null

# --- FFmpeg (LGPL, shared build from BtbN/FFmpeg-Builds) -------------------
$ffDir = Join-Path $engines 'ffmpeg'
if (Test-Path (Join-Path $ffDir 'bin\ffmpeg.exe')) {
    Write-Host 'FFmpeg already present.'
} else {
    $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$FfmpegAsset"
    $zip = Join-Path $dl $FfmpegAsset
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $zip
    $tmp = Join-Path $dl 'ffmpeg-unpacked'
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    Expand-Archive $zip -DestinationPath $tmp
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (Test-Path $ffDir) { Remove-Item -Recurse -Force $ffDir }
    Move-Item $inner.FullName $ffDir
    Remove-Item -Recurse -Force $tmp
    Remove-Item -Force $zip
    Write-Host "FFmpeg installed to $ffDir"
}

& (Join-Path $ffDir 'bin\ffmpeg.exe') -hide_banner -version | Select-Object -First 1

# --- Pandoc (GPL-2.0+, official release zip, used unmodified as a separate process) ---
$pdDir = Join-Path $engines 'pandoc'
if (Test-Path (Join-Path $pdDir 'pandoc.exe')) {
    Write-Host 'Pandoc already present.'
} else {
    $asset = "pandoc-$PandocVersion-windows-x86_64.zip"
    $url = "https://github.com/jgm/pandoc/releases/download/$PandocVersion/$asset"
    $zip = Join-Path $dl $asset
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $zip
    $tmp = Join-Path $dl 'pandoc-unpacked'
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    Expand-Archive $zip -DestinationPath $tmp
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (Test-Path $pdDir) { Remove-Item -Recurse -Force $pdDir }
    Move-Item $inner.FullName $pdDir
    Remove-Item -Recurse -Force $tmp
    Remove-Item -Force $zip
    Write-Host "Pandoc installed to $pdDir"
}

& (Join-Path $pdDir 'pandoc.exe') --version | Select-Object -First 1
