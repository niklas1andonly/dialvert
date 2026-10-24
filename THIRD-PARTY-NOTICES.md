# Third-party components in Dialvert

Dialvert's own code is MIT-licensed (see [LICENSE](LICENSE)). It contains no code, graphics or other assets
from other converter apps. The interaction concept is inspired by [Tangerine for Mac](https://tangerineformac.com/);
Dialvert is not affiliated with it.

The following components are bundled with a release build or used at runtime:

| Component | Version | License | How it is used | Source |
|---|---|---|---|---|
| FFmpeg (BtbN build, `lgpl-shared` variant) | 9.0 | LGPL-2.1-or-later | Unmodified `ffmpeg.exe`/`ffprobe.exe` + DLLs in `engines\ffmpeg\bin`, run as a separate process | https://ffmpeg.org/download.html (tag `n9.0`), build scripts: https://github.com/BtbN/FFmpeg-Builds |
| ↳ included libraries | | e.g. libvpx, libaom, SVT-AV1, dav1d, libopus (BSD-style), LAME, libvorbis, OpenH264 (BSD-2) | Part of the FFmpeg build | see the BtbN build scripts |
| Magick.NET-Q8-AnyCPU | 15.0.0 | Apache-2.0 | NuGet, in-process | https://github.com/dlemstra/Magick.NET |
| ↳ ImageMagick | 7.x | ImageMagick License (Apache-2.0 style) | Part of Magick.Native | https://imagemagick.org |
| ↳ libheif / libde265 | | LGPL-3.0 | Part of Magick.Native (HEIC input) | https://github.com/strukturag |
| ↳ libjpeg-turbo, libpng, libwebp, libtiff, aom, zlib, … | | BSD / IJG / zlib style | Part of Magick.Native | see the Magick.NET notice |
| Pandoc | 3.12.1 | GPL-2.0-or-later | Unmodified official `pandoc.exe` (release zip) in `engines\pandoc`, run as a separate process ("mere aggregation") | https://github.com/jgm/pandoc/tree/3.12.1, https://hackage.haskell.org/package/pandoc-3.12.1 |
| PDFium (bblanchon.PDFium.Win32) | 157.0.8100 | BSD-3-Clause (+ Apache-2.0 parts); build scripts MIT | NuGet, `pdfium.dll` in-process (own lean P/Invoke binding) | https://pdfium.googlesource.com/pdfium, builds: https://github.com/bblanchon/pdfium-binaries |
| ClosedXML (+ ClosedXML.Parser, ExcelNumberFormat, RBush.Signed) | 0.105.1 | MIT | NuGet, in-process (XLSX ↔ CSV/TSV) | https://github.com/ClosedXML/ClosedXML |
| ↳ DocumentFormat.OpenXml | 3.1.1 | MIT | Dependency of ClosedXML | https://github.com/dotnet/Open-XML-SDK |
| ↳ SixLabors.Fonts | 1.0.0 | Apache-2.0 | Dependency of ClosedXML (deliberately 1.x; 2.x and later use the "Six Labors Split License") | https://github.com/SixLabors/Fonts |
| .NET runtime / WPF / Windows Forms | 10 | MIT | Self-contained publish | https://github.com/dotnet |

**Not bundled:** LibreOffice (MPL-2.0). Dialvert uses a copy installed by the user
(`soffice.exe`, found via the registry, Program Files or `PATH`) for Office formats ↔ PDF/ODS and PDF → DOCX.
Deliberately not used: Ghostscript and MuPDF (both AGPL).

**License texts:** a release build contains `LICENSE.txt` (Dialvert) and a `licenses\` folder next to `Dialvert.exe`
with the full texts: FFmpeg's LGPL, Pandoc's copyright/GPL plus a pointer to the matching source, the complete
Magick.NET notice (all native libraries), and the texts in [third-party-licenses/](third-party-licenses).
`scripts\publish.ps1` puts them there.

**GPL note on Pandoc:** `pandoc.exe` is a standalone program that Dialvert only invokes on the command line;
it is neither modified nor linked. License and copyright are in `licenses\Pandoc-*`, the matching source code
is at the links above (tag `3.12.1`). The exe can be replaced with your own Pandoc build.

**LGPL note on FFmpeg:** FFmpeg is used unmodified and dynamically (separate process, its own DLLs).
You can replace the DLLs or the exe in `engines\ffmpeg\bin` with your own compatible build,
or set a different FFmpeg path in the settings.

**Patent note (not legal advice):** H.264, HEVC (including HEIC) and AAC are covered by patents in some
countries. Check the legal situation before distributing Dialvert commercially.
