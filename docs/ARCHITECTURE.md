# Dialvert – Plan & Architecture

Dialvert is a local, offline file converter for Windows 10/11.
Ctrl + right-click a file in Explorer → the action wheel appears → pick a mode with a key →
release over a segment or click it → done. No uploads, no telemetry.

The interaction concept is inspired by [Tangerine for Mac](https://tangerineformac.com/); the implementation is independent.

## 1. Stack choice

| Option | Pro | Contra |
|---|---|---|
| **.NET 10 + WPF** (chosen) | Transparent topmost overlays, trivial Win32 interop (RegisterHotKey, GetAsyncKeyState); native Explorer drag & drop; Magick.NET runs in-process; one self-contained build | Windows only (intended) |
| Tauri (Rust + WebView2) | Small binary, web UI | Key state during an OLE drag is not available in the WebView → Rust glue needed; image processing would have to run externally |
| WinUI 3 | Modern Fluent look | Transparent/borderless overlay windows and drag & drop from an elevated Explorer are awkward; packaging constraints |

Core argument: the central interaction (drag from Explorer, hold a key during the drag, wheel under the
cursor) needs low-level Win32. WPF is the most direct way to get there.

## 2. Interaction design

```
 Explorer ──drag──▶ [drop zone pill, always on top]
                         │ DragEnter
                         ▼
               ┌─ Action wheel (overlay at cursor) ─┐
               │  Mode by key (configurable):        │
               │   1 = Convert                       │
               │   2 = Compress                      │
               │   3 = Tools                         │
               │  Segments = target formats/presets  │
               └──────────────┬──────────────────────┘
                              │ Drop on segment
                              ▼
                     Job queue (progress, cancel, "show in folder")
```

* The wheel picks its segments to match the file type (image / video / audio / document / spreadsheet / subtitle / mixed).
* If a segment needs a program that is not installed (LibreOffice), it stays visible but dimmed
  ("LibreOffice missing"); the hint line in the wheel says what to install.
* Mode keys are polled with `GetAsyncKeyState` during the drag
  (Explorer owns the OLE drag loop, keyboard events don't reach us).
* Main entry point: a global low-level mouse hook (`WH_MOUSE_LL`, own thread). A right-click with the wheel
  key held over an Explorer/desktop file view (`SHELLDLL_DefView`) is swallowed; the files come from the shell
  selection (`IShellWindows` → active tab → `IShellView` → `SelectedItems`), and the item under the cursor is
  selected first via UI Automation. Everywhere else the right-click is left untouched.
* Global hotkeys (`RegisterHotKey`): optional drop zone on/off (default `Ctrl+Alt+U`).
* Autostart via `HKCU\…\Run` (`--tray`), repaired to the current path on every start.
* All keys can be changed in the settings with a "press a key" field,
  stored in `%APPDATA%\Dialvert\settings.json`.
* Output goes next to the source file (or a fixed folder) and never overwrites anything
  (`name (1).webp`).

## 3. Layers

```
src/
  Dialvert.Core/          (net10.0, no UI dependency, testable)
    Formats/              file type detection, format catalog
    Jobs/                 IJob, JobRunner (parallel, cancel, progress)
    Engines/
      Image/              ImageEngine  → Magick.NET (in-process)
      Media/              MediaEngine  → ffmpeg/ffprobe (process, -progress pipe)
      Document/           DocumentEngine → PDFium (in-process), Pandoc (process),
                          LibreOffice headless (installed, process), ClosedXML, SRT↔VTT
      (later) Archive/    SharpCompress / System.IO.Compression
    Actions/              ActionCatalog: which wheel segments for which files
    Settings/             settings model + JSON store
  Dialvert.App/           (net10.0-windows, WPF)
    WheelWindow           radial overlay (drag / click / right-click gesture)
    MainWindow            custom title bar; history (persistent) + SettingsView
    ToastWindow           short messages at the cursor / progress in the bottom-right corner
    DropZone              optional floating drop zone
    Interop/              mouse/keyboard hooks, shell selection, hotkeys, autostart
tests/
  Dialvert.Core.Tests/    xUnit, generates test images/videos/documents synthetically
```

### Engine abstraction

```csharp
interface IJob { string Title; Task RunAsync(IProgress<double>, CancellationToken); IReadOnlyList<string> Outputs; }
```
Every wheel action creates one job per file (or one combined job, e.g. "images → PDF").
The `JobRunner` limits parallelism (images: CPU cores, video: 1–2).

### Video encoder selection
`ffmpeg -encoders` is read once; H.264 is chosen in this order:
`libx264` (GPL build only) → `h264_nvenc` → `h264_qsv` → `h264_amf` → `h264_mf`
(Windows Media Foundation) → `libopenh264`. Hardware encoders are verified with a
1-frame probe before they are used.

* Container change with compatible codecs → remux (`-c copy`, seconds instead of minutes)
* Compress: preset (High/Medium/Small) or target size → bitrate = size·8/duration − audio
* GIF: `fps,scale,split,palettegen,paletteuse` in one filter graph

### Documents (phase 3)
Every conversion is a short **route** of steps (`DocumentEngine.Route`), e.g.
`MD → DOCX (Pandoc) → PDF (LibreOffice)` or `ODS → XLSX (LibreOffice) → CSV (ClosedXML)`.
Intermediate files live in `%TEMP%`; only the result lands in the output folder (temp file + commit, never overwrite).

| Source | Targets | Tool |
|---|---|---|
| PDF | JPG/PNG (one per page; multi-page into a folder `name\Page 001.png`), TXT, MD | PDFium in-process (own P/Invoke binding, global lock – PDFium is not thread-safe) |
| PDF | DOCX ("layout may differ") | LibreOffice `writer_pdf_import` |
| DOCX/ODT/RTF/HTML/MD/EPUB, TXT | DOCX, MD (GFM, images into `name_media\`), TXT, EPUB | Pandoc; TXT is escaped first so `#`/`*` stay text |
| DOCX/DOC/ODT/RTF/HTML, PPTX/PPT/ODP, XLSX/XLS/ODS/CSV | PDF | LibreOffice (MD/TXT/EPUB via Pandoc → DOCX) |
| DOC/ODT/RTF | DOCX | LibreOffice (keeps the layout) |
| XLSX ↔ CSV/TSV | one file per sheet | ClosedXML in-process |
| XLS/ODS ↔ XLSX/ODS | | LibreOffice |
| SRT ↔ VTT | | own code (timings, b/i/u; `<font>`, ASS codes and VTT voices are simplified) |

* **CSV dialect** follows the Windows list separator so Excel opens the file directly:
  German-style `;` + decimal comma, otherwise `,` + decimal point; UTF-8 with BOM. When reading, the delimiter is guessed;
  numbers are only typed when unambiguous (leading zeros and > 15 digits stay text).
* **LibreOffice** runs with its own profile (`%LOCALAPPDATA%\Dialvert\libreoffice-profile`) so it works next to an
  open LibreOffice instance, and only one conversion at a time. Detection:
  setting `LibreOfficePath` → registry (`SOFTWARE\LibreOffice\UNO\InstallPath`) → Program Files → `PATH`.
* Scanned PDFs without a text layer → clear error message (no OCR).

### Image compression
* Percentage / preset → quality
* Target size → binary search over quality (lossy formats); for PNG
  additionally color reduction; downscaling as the last step

## 4. Engines, licenses, bundling

| Engine | Purpose | License | Bundling |
|---|---|---|---|
| **FFmpeg** (BtbN LGPL shared build) | video/audio/GIF | LGPL-2.1+ (built without `--enable-gpl`) | Unmodified in `engines\ffmpeg\` (exe + DLLs), run as a separate process. Ships with `LICENSE` and a pointer to the source (FFmpeg tag + BtbN build scripts). No libx264/x265 → H.264 via OpenH264/MF/GPU. |
| FFmpeg GPL build (optional) | better x264/x265 quality | GPL-2/3 | Only as an optional "engine pack" the user downloads, so the app itself does not have to become GPL |
| **Magick.NET** (Q8, AnyCPU) | images | Apache-2.0; ImageMagick license (Apache-style); delegates: libheif/libde265 (LGPL-3), aom/libwebp/libjpeg-turbo/libpng (BSD-style) | NuGet, ends up as `Magick.Native-*.dll` in the app folder. The package's full `Notice.txt` is shipped in `licenses\`. |
| **Pandoc** 3.12.1 | DOCX/MD/EPUB/TXT | GPL-2.0+ | Unmodified `pandoc.exe` (official release zip) in `engines\pandoc\`, separate process ("mere aggregation"); `COPYRIGHT`/`COPYING` + link to the source tag in `licenses\`. |
| LibreOffice headless | Office/slides/spreadsheets→PDF, ODS↔XLSX, PDF→DOCX | MPL-2.0 (+ LGPL-3 parts) | **Not bundled** (~350 MB). An installed version is detected (`soffice.com`/`.exe`); otherwise dimmed segments + a hint to libreoffice.org. |
| **PDFium** | PDF→JPG/PNG/TXT/MD | BSD-3 / Apache-2.0 | NuGet `bblanchon.PDFium.Win32` (current Chromium builds) + own lean P/Invoke binding instead of the barely maintained wrappers (Docnet/PDFtoImage); page bitmaps are encoded by Magick.NET. Deliberately **not** Ghostscript/MuPDF (both AGPL). |
| SharpCompress (phase 4) | ZIP/TAR/GZIP, read RAR | MIT | NuGet, in-process. Alternative 7-Zip (`7za.exe` LGPL-2.1; RAR only with `7z.dll`, the unRAR license forbids creating RAR – fits "read only"). |
| **ClosedXML** 0.105 | XLSX↔CSV/TSV without LibreOffice | MIT (dependency SixLabors.Fonts 1.0.0: Apache-2.0) | NuGet, in-process |

**Patents** (not legal advice): H.264/HEVC/AAC are patent-encumbered. Media
Foundation uses the codecs licensed with Windows. Cisco's OpenH264 patent coverage
only applies to Cisco's own binaries – the BtbN build compiles OpenH264 from
source. HEIC decoding (libde265) is HEVC as well. Check the legal situation before
commercial distribution; alternative: decode HEIC via the Windows
"HEIF Image Extensions" (WIC).

### Packaging
* `dotnet publish -c Release -r win-x64 --self-contained` → `Dialvert.exe` + runtime
* `engines\ffmpeg\bin\*` and `engines\pandoc\pandoc.exe` next to it; `LICENSE.txt`, `THIRD-PARTY-NOTICES.md` + `licenses\`
* Installer: Inno Setup (free) or MSIX; engine packs (GPL FFmpeg, Pandoc)
  can be downloaded by the app on demand with a SHA-256 check.
* Engine lookup: `engines\` next to the exe → settings → `PATH`.

## 5. Roadmap

| Phase | Scope | Status |
|---|---|---|
| **MVP** | Drop zone + action wheel + queue + tray + "Send to"; convert images JPG/PNG/WebP/HEIC (read)/TIFF/AVIF/BMP, compress (preset/percentage/target size), remove metadata, 50 % / 1920 px, images→PDF; convert video MP4/MOV/MKV/WebM/AVI/GIF + audio MP3/M4A/WAV/FLAC/OGG/Opus, compress video (preset/target size), extract audio; configurable keys | ✅ done |
| 1b | Resize / crop with a dialog, edit metadata (EXIF fields) | open |
| 2b | Trim video (start/end dialog with preview) | open |
| **3** | PDF → JPG/PNG/TXT/MD (PDFium); DOCX/MD/TXT/EPUB to each other (Pandoc); Office/slides/spreadsheets → PDF, XLS/ODS ↔ XLSX, PDF → DOCX (installed LibreOffice); XLSX ↔ CSV/TSV (ClosedXML); subtitles SRT ↔ VTT | ✅ |
| 4 | Create/extract archives, read RAR | open |
| 5 | File actions: copy path, move, open terminal, floating shelf (park files and drag them on), Explorer context menu / "Send to" | open |
