using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using Dialvert.Core.Formats;
using static Dialvert.Core.Formats.DocFormat;

namespace Dialvert.Core.Engines.Document;

/// <summary>Which program does a conversion step.</summary>
public enum DocTool { Pdfium, Pandoc, LibreOffice, ClosedXml, Builtin }

public sealed record DocumentOptions(int RenderDpi = 150, int ImageQuality = 88, CsvDialect? Csv = null);

/// <summary>
/// Documents, spreadsheets and subtitles. Each conversion is a short route of steps (e.g. MD → DOCX via
/// Pandoc → PDF via LibreOffice); intermediate files live in a temp folder, only the result lands in the output.
/// </summary>
public sealed partial class DocumentEngine
{
    public readonly record struct Step(DocFormat To, DocTool Tool);

    /// <summary>Order the wheel shows targets in.</summary>
    static readonly DocFormat[] TargetOrder = [Pdf, Docx, Md, Txt, Epub, Jpg, Png, Xlsx, Ods, Csv, Tsv, Srt, Vtt];

    readonly OutputPaths _out;
    readonly DocumentOptions _options;

    public Pandoc? Pandoc { get; }
    public LibreOffice? Office { get; }

    public DocumentEngine(OutputPaths output, Pandoc? pandoc, LibreOffice? office, DocumentOptions? options = null)
    {
        _out = output;
        Pandoc = pandoc;
        Office = office;
        _options = options ?? new DocumentOptions();
    }

    // ------------------------------------------------------------------ routes

    public static Step[]? Route(DocFormat source, DocFormat target) => (source, target) switch
    {
        _ when source == target => null,

        // PDF: render and text in-process (PDFium); Word only through LibreOffice's PDF import.
        (Pdf, Jpg or Png or Txt or Md) => [new(target, DocTool.Pdfium)],
        (Pdf, Docx) => [new(Docx, DocTool.LibreOffice)],

        // Word processing → PDF and legacy formats → DOCX keep their layout only in LibreOffice.
        (Docx or Doc or Odt or Rtf or Html, Pdf) => [new(Pdf, DocTool.LibreOffice)],
        (Md or Txt or Epub, Pdf) => [new(Docx, DocTool.Pandoc), new(Pdf, DocTool.LibreOffice)],
        (Doc or Odt or Rtf, Docx) => [new(Docx, DocTool.LibreOffice)],
        (Doc, Md or Txt or Epub) => [new(Docx, DocTool.LibreOffice), new(target, DocTool.Pandoc)],
        (Txt, Docx or Epub) => [new(target, DocTool.Pandoc)],
        (Docx or Odt or Rtf or Html or Md or Epub, Docx or Md or Txt or Epub) => [new(target, DocTool.Pandoc)],

        (Pptx or Ppt or Odp, Pdf) => [new(Pdf, DocTool.LibreOffice)],

        // Spreadsheets: XLSX ↔ CSV/TSV in-process, everything touching ODS/XLS/PDF via LibreOffice.
        (Xlsx, Csv or Tsv) => [new(target, DocTool.ClosedXml)],
        (Xls or Ods, Csv or Tsv) => [new(Xlsx, DocTool.LibreOffice), new(target, DocTool.ClosedXml)],
        (Csv or Tsv, Xlsx) => [new(Xlsx, DocTool.ClosedXml)],
        (Csv or Tsv, Ods or Pdf) => [new(Xlsx, DocTool.ClosedXml), new(target, DocTool.LibreOffice)],
        (Csv, Tsv) or (Tsv, Csv) => [new(target, DocTool.Builtin)],
        (Xlsx or Xls, Ods) or (Xls or Ods, Xlsx) => [new(target, DocTool.LibreOffice)],
        (Xlsx or Xls or Ods, Pdf) => [new(Pdf, DocTool.LibreOffice)],

        (Srt, Vtt) or (Vtt, Srt) => [new(target, DocTool.Builtin)],
        _ => null,
    };

    public static IReadOnlyList<DocFormat> TargetsFor(DocFormat source) =>
        TargetOrder.Where(t => Route(source, t) is not null).ToList();

    public static bool CanConvert(string path, DocFormat target) =>
        FormatInfo.DocFormatOf(path) is { } source && Route(source, target) is not null;

    /// <summary>Name of the external program this conversion needs but that is not installed, else null.</summary>
    public string? MissingTool(DocFormat source, DocFormat target)
    {
        var route = Route(source, target);
        if (route is null) return null;
        if (Office is null && route.Any(s => s.Tool == DocTool.LibreOffice)) return "LibreOffice";
        if (Pandoc is null && route.Any(s => s.Tool == DocTool.Pandoc)) return "Pandoc";
        return null;
    }

    // ----------------------------------------------------------------- convert

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, DocFormat target, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var source = FormatInfo.DocFormatOf(input) ?? throw new NotSupportedException("Unknown document format.");
        var route = Route(source, target)
            ?? throw new NotSupportedException($"{FormatInfo.Label(source)} → {FormatInfo.Label(target)} is not supported.");
        if (MissingTool(source, target) is { } missing)
            throw new InvalidOperationException(missing == "LibreOffice"
                ? $"LibreOffice is not installed ({LibreOffice.DownloadUrl})."
                : "Pandoc is missing (scripts\\fetch-engines.ps1).");

        var stem = Path.GetFileNameWithoutExtension(input);
        string? work = null;
        try
        {
            var current = input;
            var currentFormat = source;
            for (var i = 0; i < route.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = route[i];
                var last = i == route.Length - 1;
                string output;
                if (last) output = _out.For(input, FormatInfo.Extension(step.To));
                else
                {
                    work ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"dialvert-{Guid.NewGuid():N}")).FullName;
                    output = Path.Combine(work, stem + FormatInfo.Extension(step.To));
                }
                var done = i;
                var stepProgress = new Progress<double>(p => progress?.Report((done + Math.Clamp(p, 0, 1)) / route.Length));
                var written = await RunStepAsync(step, current, currentFormat, output, stepProgress, ct);
                if (last) return written;
                current = written[0];
                currentFormat = step.To;
            }
            throw new InvalidOperationException("unreachable");
        }
        finally
        {
            if (work is not null) try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    async Task<IReadOnlyList<string>> RunStepAsync(Step step, string input, DocFormat from, string output, IProgress<double> progress, CancellationToken ct)
    {
        switch (step.Tool)
        {
            case DocTool.Pdfium when step.To is Jpg or Png:
                return RenderPages(input, step.To, output, progress, ct);
            case DocTool.Pdfium:
                return [WriteText(output, step.To == Md ? PdfToMarkdown(input, ct) : PdfToText(input, ct), bom: step.To == Txt)];
            case DocTool.Pandoc:
                return [await PandocAsync(input, from, step.To, output, ct)];
            case DocTool.LibreOffice:
                return [await OfficeAsync(input, from, step.To, output, ct)];
            case DocTool.ClosedXml when step.To is Csv or Tsv:
                return Tables.XlsxToDelimited(input, output, Dialect(step.To), ct);
            case DocTool.ClosedXml:
                return [Atomic(output, temp => Tables.DelimitedToXlsx(input, temp, ct))];
            case DocTool.Builtin when step.To is Csv or Tsv:
                return [Atomic(output, temp => Tables.Redelimit(input, temp, Dialect(step.To).Delimiter))];
            case DocTool.Builtin:
                var cues = Subtitles.Parse(TextFiles.Read(input));
                if (cues.Count == 0) throw new InvalidOperationException("No subtitles found in the file.");
                return [step.To == Srt ? WriteText(output, Subtitles.ToSrt(cues), bom: true) : WriteText(output, Subtitles.ToVtt(cues), bom: false)];
            default:
                throw new NotSupportedException(step.Tool.ToString());
        }
    }

    CsvDialect Dialect(DocFormat f)
    {
        var csv = _options.Csv ?? CsvDialect.Csv();
        return f == Tsv ? csv with { Delimiter = '\t' } : csv;
    }

    /// <summary>Writes through a hidden temp file next to the target and commits it (never overwrites).</summary>
    static string Atomic(string output, Action<string> write)
    {
        var temp = OutputPaths.TempFor(output);
        try
        {
            write(temp);
            return OutputPaths.Commit(temp, output);
        }
        catch { OutputPaths.TryDelete(temp); throw; }
    }

    static string WriteText(string output, string text, bool bom) => Atomic(output, temp => TextFiles.Write(temp, text, bom));

    // ------------------------------------------------------------------- PDFium

    /// <summary>One image per page. A single page becomes "name.png", several go into a folder "name\Page 001.png".</summary>
    IReadOnlyList<string> RenderPages(string input, DocFormat format, string output, IProgress<double> progress, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(input);
        var count = pdf.PageCount;
        if (count == 0) throw new InvalidOperationException("The PDF has no pages.");
        var ext = FormatInfo.Extension(format);

        if (count == 1)
            return [Atomic(output, temp => RenderPage(pdf, 0, format, temp))];

        var parent = Path.GetDirectoryName(output)!;
        var folder = OutputPaths.Unique(Path.Combine(parent, Path.GetFileNameWithoutExtension(output)));
        var temp = Path.Combine(parent, $".dialvert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var digits = Math.Max(3, count.ToString().Length);
            var names = new List<string>();
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var name = $"Page {(i + 1).ToString().PadLeft(digits, '0')}{ext}";
                RenderPage(pdf, i, format, Path.Combine(temp, name));
                names.Add(name);
                progress.Report((i + 1.0) / count);
            }
            folder = OutputPaths.Unique(folder);
            Directory.Move(temp, folder);
            return names.Select(n => Path.Combine(folder, n)).ToList();
        }
        catch
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    void RenderPage(PdfDocument pdf, int index, DocFormat format, string path)
    {
        var (wPt, hPt) = pdf.PageSize(index);
        // Cap huge pages (posters, plans) at 12k px on the long edge.
        var scale = Math.Min(_options.RenderDpi / 72.0, 12000 / Math.Max(wPt, hPt));
        var w = Math.Max(1, (int)Math.Round(wPt * scale));
        var h = Math.Max(1, (int)Math.Round(hPt * scale));
        var bgra = pdf.Render(index, w, h);

        using var img = new MagickImage();
        img.ReadPixels(bgra, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
        img.Alpha(AlphaAction.Off);
        img.Density = new Density(_options.RenderDpi, _options.RenderDpi, DensityUnit.PixelsPerInch);
        if (format == Jpg) img.Quality = (uint)Math.Clamp(_options.ImageQuality, 1, 100);
        img.Write(path, format == Jpg ? MagickFormat.Jpeg : MagickFormat.Png);
    }

    static List<string> PageTexts(string input, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(input);
        var pages = new List<string>();
        for (var i = 0; i < pdf.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            // PDFium itself re-joins words hyphenated at a line end and marks the spot with U+FFFE (older builds: U+0002).
            pages.Add(pdf.Text(i).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "").Replace("￾", "").Replace("\u0002", ""));
        }
        if (pages.All(p => string.IsNullOrWhiteSpace(p)))
            throw new InvalidOperationException("The PDF contains no text (scanned?) – text recognition (OCR) is not supported.");
        return pages;
    }

    static string PdfToText(string input, CancellationToken ct) =>
        string.Join("\r\n\r\n", PageTexts(input, ct).Select(p => p.TrimEnd().Replace("\n", "\r\n"))) + "\r\n";

    /// <summary>
    /// Text with light structure: lines in a block flow into one paragraph (Markdown soft wraps), words hyphenated
    /// across lines are rejoined, bullets become list items, pages are separated by a rule. No layout or tables.
    /// </summary>
    static string PdfToMarkdown(string input, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var pages = PageTexts(input, ct);
        for (var p = 0; p < pages.Count; p++)
        {
            if (p > 0) sb.Append("\n\n---\n\n");
            var lines = pages[p].Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) { sb.Append("\n\n"); continue; }
                var bullet = Bullet().Match(line);
                if (bullet.Success) line = "- " + EscapeMarkdown(line[bullet.Length..]);
                else line = EscapeMarkdown(line);
                sb.Append(line);
                var next = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
                if (line.EndsWith('-') && next.Length > 0 && char.IsLower(next[0]) && !bullet.Success) sb.Length--; // re-join "hyphen-\nated"
                else sb.Append('\n');
            }
        }
        var text = MultiBlank().Replace(sb.ToString(), "\n\n").Trim();
        return text + "\n";
    }

    /// <summary>Escapes what would otherwise turn plain PDF text into Markdown structure.</summary>
    static string EscapeMarkdown(string line)
    {
        line = line.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`")
                   .Replace("[", "\\[").Replace("]", "\\]").Replace("<", "\\<");
        if (line.Length > 0 && line[0] is '#' or '>' or '+' or '-' or '=' or '|') line = "\\" + line;
        return OrderedList().Replace(line, "$1\\.", 1);
    }

    [GeneratedRegex(@"^[•·▪◦●○■□‣⁃–]\s*")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\d+)\.(?=\s)")]
    private static partial Regex OrderedList();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiBlank();

    // ------------------------------------------------------------------- Pandoc

    static string PandocReader(DocFormat f) => f switch
    {
        Docx => "docx",
        Odt => "odt",
        Rtf => "rtf",
        Html => "html",
        Epub => "epub",
        Md => "markdown",
        Txt => "markdown+hard_line_breaks", // input is escaped first, see LiteralMarkdown
        _ => throw new NotSupportedException($"Pandoc cannot read {f}."),
    };

    static string PandocWriter(DocFormat f) => f switch
    {
        Docx => "docx",
        Md => "gfm",
        Txt => "plain",
        Epub => "epub3",
        _ => throw new NotSupportedException($"Pandoc cannot write {f}."),
    };

    async Task<string> PandocAsync(string input, DocFormat from, DocFormat to, string output, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(output)!;
        var temp = OutputPaths.TempFor(output);
        var scratch = new List<string>();
        try
        {
            var source = input;
            if (from == Txt)
            {
                // Plain text must stay plain: escape it so "# 1" or "* x" don't become headings/lists.
                source = Path.Combine(Path.GetTempPath(), $"dialvert-{Guid.NewGuid():N}.md");
                File.WriteAllText(source, LiteralMarkdown(TextFiles.Read(input)), new UTF8Encoding(false));
                scratch.Add(source);
            }
            List<string> args = ["-f", PandocReader(from), "-t", PandocWriter(to), "-o", temp];
            if (to is Md or Txt) args.Add("--wrap=none");
            if (to == Md)
            {
                // Embedded images go to a folder beside the result, linked relatively (pandoc runs in that folder).
                var media = Path.GetFileName(OutputPaths.Unique(Path.Combine(dir, Path.GetFileNameWithoutExtension(output) + "_media")));
                args.Add($"--extract-media={media}");
            }
            if (to == Epub)
            {
                // EPUB needs a title; the document's own title (if any) wins over a metadata file.
                var meta = Path.Combine(Path.GetTempPath(), $"dialvert-{Guid.NewGuid():N}.yaml");
                File.WriteAllText(meta, $"title: '{Path.GetFileNameWithoutExtension(input).Replace("'", "''")}'\n", new UTF8Encoding(false));
                scratch.Add(meta);
                args.Add($"--metadata-file={meta}");
            }
            args.Add(Path.GetFullPath(source));
            await Pandoc!.RunAsync(args, dir, ct);
            return OutputPaths.Commit(temp, output);
        }
        catch { OutputPaths.TryDelete(temp); throw; }
        finally { foreach (var f in scratch) OutputPaths.TryDelete(f); }
    }

    /// <summary>Plain text as Markdown that renders literally: punctuation escaped, leading spaces kept.</summary>
    internal static string LiteralMarkdown(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            var indent = line.Length - line.TrimStart().Length;
            for (var i = 0; i < indent; i++) sb.Append("\\ ");
            foreach (var ch in line.AsSpan(indent))
            {
                if (char.IsAsciiLetterOrDigit(ch) || ch == ' ' || ch > 127) sb.Append(ch);
                else if (ch == '\t') sb.Append("\\ \\ \\ \\ ");
                else sb.Append('\\').Append(ch);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------- LibreOffice

    async Task<string> OfficeAsync(string input, DocFormat from, DocFormat to, string output, CancellationToken ct)
    {
        var (convertTo, inFilter) = (from, to) switch
        {
            (Pdf, Docx) => ("docx:MS Word 2007 XML", "writer_pdf_import"),
            (_, Docx) => ("docx:MS Word 2007 XML", (string?)null),
            (_, Xlsx) => ("xlsx:Calc MS Excel 2007 XML", null),
            (_, Ods) => ("ods", null),
            (_, Pdf) => ("pdf", null),
            _ => throw new NotSupportedException($"LibreOffice: {from} → {to}"),
        };
        var temp = OutputPaths.TempFor(output);
        try
        {
            await Office!.ConvertAsync(input, convertTo, temp, inFilter, ct);
            return OutputPaths.Commit(temp, output);
        }
        catch { OutputPaths.TryDelete(temp); throw; }
    }
}
