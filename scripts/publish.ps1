<#
  Builds a self-contained, ready-to-run folder: dist\Dialvert\
    Dialvert.exe (+ .NET runtime, Magick.NET native, pdfium.dll)
    engines\ffmpeg\bin\*      (LGPL build, unmodified)
    engines\pandoc\pandoc.exe (GPL, unmodified, separate program)
    licenses\                 (license texts of bundled components)
    THIRD-PARTY-NOTICES.md
  LibreOffice is never bundled; an installed copy is detected at runtime.
  Run from the repo root after scripts\fetch-engines.ps1:
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist\Dialvert'
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }

dotnet publish (Join-Path $root 'src\Dialvert.App') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $dist
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$ff = Join-Path $root 'engines\ffmpeg'
if (-not (Test-Path (Join-Path $ff 'bin\ffmpeg.exe'))) { throw 'FFmpeg missing - run scripts\fetch-engines.ps1 first' }
$ffOut = Join-Path $dist 'engines\ffmpeg'
New-Item -ItemType Directory -Force (Join-Path $ffOut 'bin') | Out-Null
# exe + shared DLLs, unmodified; ffplay (media player) is not needed
Copy-Item (Join-Path $ff 'bin\*') (Join-Path $ffOut 'bin') -Exclude 'ffplay.exe'

$pd = Join-Path $root 'engines\pandoc'
if (-not (Test-Path (Join-Path $pd 'pandoc.exe'))) { throw 'Pandoc missing - run scripts\fetch-engines.ps1 first' }
$pdOut = Join-Path $dist 'engines\pandoc'
New-Item -ItemType Directory -Force $pdOut | Out-Null
# The official release zip as-is (exe + COPYRIGHT/COPYING); only the 1 MB HTML manual is left out.
Copy-Item (Join-Path $pd '*') $pdOut -Exclude 'MANUAL.html'

$lic = Join-Path $dist 'licenses'
New-Item -ItemType Directory -Force $lic | Out-Null
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $dist 'LICENSE.txt')
Copy-Item (Join-Path $ff 'LICENSE.txt') (Join-Path $lic 'FFmpeg-LGPL-2.1.txt')

# Magick.NET: the package's Notice.txt holds the full license texts of Magick.NET, ImageMagick and
# every bundled native library (libheif/libde265 LGPL-3.0, aom, libwebp, ...), for exactly this version.
$coreProj = [xml](Get-Content (Join-Path $root 'src\Dialvert.Core\Dialvert.Core.csproj'))
$magickVersion = ($coreProj.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Magick.NET-Q8-AnyCPU').Version
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$magickNotice = Join-Path $nuget "magick.net-q8-anycpu\$magickVersion\Notice.txt"
if (-not (Test-Path $magickNotice)) { throw "Magick.NET notice not found at $magickNotice" }
Copy-Item $magickNotice (Join-Path $lic 'Magick.NET-NOTICE.txt')

# Pandoc (GPL): license, copyright and where the matching source is.
Copy-Item (Join-Path $pd 'COPYRIGHT.txt') (Join-Path $lic 'Pandoc-COPYRIGHT.txt')
Copy-Item (Join-Path $pd 'COPYING.rtf') (Join-Path $lic 'Pandoc-GPL-2.0.rtf')
$pandocVersion = ((& (Join-Path $pd 'pandoc.exe') --version | Select-Object -First 1) -replace '^pandoc\s+', '').Trim()
@"
Pandoc $pandocVersion - GPL-2.0-or-later, shipped unmodified as a separate program (engines\pandoc\pandoc.exe).
Corresponding source: https://github.com/jgm/pandoc/tree/$pandocVersion
  and https://hackage.haskell.org/package/pandoc-$pandocVersion/pandoc-$pandocVersion.tar.gz
"@ | Set-Content (Join-Path $lic 'Pandoc-SOURCE.txt') -Encoding utf8

# PDFium, ClosedXML and its dependencies, SixLabors.Fonts (Apache-2.0), the .NET runtime, WPF and WinForms.
Copy-Item (Join-Path $root 'third-party-licenses\*') $lic
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $dist

$size = (Get-ChildItem $dist -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Published to {0} ({1:N0} MB)" -f $dist, $size)
