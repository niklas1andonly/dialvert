using System.Globalization;
using System.IO.Compression;
using ClosedXML.Excel;
using ImageMagick;
using Dialvert.Core.Actions;
using Dialvert.Core.Engines.Document;
using Dialvert.Core.Engines.Image;
using Dialvert.Core.Formats;
using Dialvert.Core.Settings;
using Xunit.Abstractions;

namespace Dialvert.Core.Tests;

[Collection("documents")]
public sealed class DocumentEngineTests(DocumentFixtures fx, ITestOutputHelper output)
{
    static readonly CsvDialect German = CsvDialect.Csv(CultureInfo.GetCultureInfo("de-DE"));

    DocumentEngine Engine(string sub, bool withOffice = true) =>
        new(new OutputPaths(fx.Sub(sub)), fx.Pandoc, withOffice ? fx.Office : null, new DocumentOptions(Csv: German));

    bool Skip(string? missing)
    {
        if (missing is null) return false;
        output.WriteLine($"{missing} not available – skipped");
        return true;
    }

    // ------------------------------------------------------------------ routes

    [Fact]
    public void Offers_expected_targets()
    {
        Assert.Equal([DocFormat.Docx, DocFormat.Md, DocFormat.Txt, DocFormat.Jpg, DocFormat.Png], DocumentEngine.TargetsFor(DocFormat.Pdf));
        Assert.Equal([DocFormat.Pdf, DocFormat.Md, DocFormat.Txt, DocFormat.Epub], DocumentEngine.TargetsFor(DocFormat.Docx));
        Assert.Equal([DocFormat.Pdf, DocFormat.Ods, DocFormat.Csv, DocFormat.Tsv], DocumentEngine.TargetsFor(DocFormat.Xlsx));
        Assert.Equal([DocFormat.Pdf, DocFormat.Xlsx, DocFormat.Ods, DocFormat.Tsv], DocumentEngine.TargetsFor(DocFormat.Csv));
        Assert.Equal([DocFormat.Vtt], DocumentEngine.TargetsFor(DocFormat.Srt));
        Assert.Equal([DocFormat.Pdf], DocumentEngine.TargetsFor(DocFormat.Pptx));
        Assert.DoesNotContain(DocFormat.Md, DocumentEngine.TargetsFor(DocFormat.Txt));
    }

    [Fact]
    public async Task Missing_libreoffice_is_reported_not_crashed()
    {
        var engine = Engine("no-office", withOffice: false);
        Assert.Equal("LibreOffice", engine.MissingTool(DocFormat.Docx, DocFormat.Pdf));
        Assert.Null(engine.MissingTool(DocFormat.Pdf, DocFormat.Png));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ConvertAsync(fx.Xlsx, DocFormat.Pdf));
        Assert.Contains("LibreOffice", ex.Message);
    }

    // ------------------------------------------------------------------- PDFium

    [Fact]
    public async Task Multi_page_pdf_renders_into_a_folder()
    {
        var results = await Engine("pdf-png").ConvertAsync(fx.ScanPdf, DocFormat.Png);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("scan", Path.GetFileName(Path.GetDirectoryName(r))));
        Assert.EndsWith("Page 001.png", results[0]);
        using var img = new MagickImage(results[0]);
        output.WriteLine($"{img.Width}x{img.Height}");
        Assert.InRange((int)img.Width, 1238, 1242);   // 595 pt at 150 dpi
        Assert.InRange((int)img.Height, 1752, 1756);
        var px = img.GetPixels().GetPixel(10, 10).ToColor()!;
        Assert.True(px.B > px.R, $"expected the blue page background, got {px}");
    }

    [Fact]
    public async Task Single_page_pdf_becomes_one_jpeg_and_never_overwrites()
    {
        var engine = Engine("pdf-jpg");
        var a = Assert.Single(await engine.ConvertAsync(fx.OnePagePdf, DocFormat.Jpg));
        var b = Assert.Single(await engine.ConvertAsync(fx.OnePagePdf, DocFormat.Jpg));
        Assert.EndsWith("einseitig.jpg", a);
        Assert.EndsWith("einseitig (1).jpg", b);
        using var img = new MagickImage(a);
        Assert.Equal(MagickFormat.Jpeg, img.Format);
        // White page with black text: mostly white.
        Assert.True(img.Statistics().GetChannel(PixelChannel.Red)!.Mean > Quantum.Max * 0.9);
    }

    [Fact]
    public async Task Pdf_text_is_extracted()
    {
        var txt = Assert.Single(await Engine("pdf-txt").ConvertAsync(fx.TextPdf, DocFormat.Txt));
        var text = File.ReadAllText(txt);
        output.WriteLine(text);
        Assert.Contains("Die Größe der Datei ist klein.", text);
        Assert.Contains("Seite zwei", text);
    }

    [Fact]
    public async Task Pdf_to_markdown_reflows_and_escapes()
    {
        var md = Assert.Single(await Engine("pdf-md").ConvertAsync(fx.TextPdf, DocFormat.Md));
        var text = File.ReadAllText(md);
        output.WriteLine(text);
        Assert.Contains("Silbentrennung am Zeilenende.", text);
        Assert.Contains("\\# keine Überschrift", text);
        Assert.Contains("- erster Punkt", text);
        Assert.Contains("\n---\n", text);
    }

    [Fact]
    public async Task Scanned_pdf_text_fails_with_hint()
    {
        var dir = fx.Sub("scan-txt");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DocumentEngine(new OutputPaths(dir), null, null).ConvertAsync(fx.ScanPdf, DocFormat.Txt));
        Assert.Contains("OCR", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    // ------------------------------------------------------------------- Pandoc

    [Fact]
    public async Task Docx_to_markdown_extracts_images()
    {
        if (Skip(fx.Pandoc is null ? "Pandoc" : null)) return;
        var dir = fx.Sub("docx-md");
        var md = Assert.Single(await Engine("docx-md").ConvertAsync(fx.Docx!, DocFormat.Md));
        var text = File.ReadAllText(md);
        output.WriteLine(text);
        Assert.Contains("# Titel", text);
        Assert.Contains("äöü ß", text);
        Assert.Contains("| Name | Wert |", text);
        Assert.Contains("brief_media/", text);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(dir, "brief_media"), "*", SearchOption.AllDirectories));
        Assert.DoesNotContain(Directory.GetFiles(dir), f => Path.GetFileName(f).StartsWith(".dialvert-"));
    }

    [Theory]
    [InlineData(DocFormat.Txt)]
    [InlineData(DocFormat.Epub)]
    public async Task Docx_converts_via_pandoc(DocFormat target)
    {
        if (Skip(fx.Pandoc is null ? "Pandoc" : null)) return;
        var result = Assert.Single(await Engine("docx-" + target).ConvertAsync(fx.Docx!, target));
        output.Report(fx.Docx!, result);
        Assert.Equal(FormatInfo.Extension(target), Path.GetExtension(result));
        if (target == DocFormat.Txt) Assert.Contains("Ein Absatz mit Umlauten: äöü ß.", File.ReadAllText(result));
        else
        {
            using var zip = ZipFile.OpenRead(result);
            using var reader = new StreamReader(zip.GetEntry("mimetype")!.Open());
            Assert.Equal("application/epub+zip", reader.ReadToEnd());
        }
    }

    [Fact]
    public async Task Plain_text_stays_literal()
    {
        if (Skip(fx.Pandoc is null ? "Pandoc" : null)) return;
        var engine = Engine("txt-docx");
        var docx = Assert.Single(await engine.ConvertAsync(fx.PlainText, DocFormat.Docx));
        var back = Assert.Single(await engine.ConvertAsync(docx, DocFormat.Txt));
        var text = File.ReadAllText(back);
        output.WriteLine(text);
        Assert.Contains("# kein Titel", text);
        Assert.Contains("* kein Punkt", text);
    }

    [Fact]
    public async Task Markdown_to_docx_and_back()
    {
        if (Skip(fx.Pandoc is null ? "Pandoc" : null)) return;
        var engine = Engine("md-docx");
        var docx = Assert.Single(await engine.ConvertAsync(fx.Markdown, DocFormat.Docx));
        using (var zip = ZipFile.OpenRead(docx)) Assert.NotNull(zip.GetEntry("word/document.xml"));
        var md = Assert.Single(await engine.ConvertAsync(docx, DocFormat.Md));
        Assert.Contains("# Titel", File.ReadAllText(md));
    }

    // -------------------------------------------------------------- spreadsheets

    [Fact]
    public async Task Csv_to_xlsx_types_numbers_and_keeps_text()
    {
        var xlsx = Assert.Single(await Engine("csv-xlsx").ConvertAsync(fx.Csv, DocFormat.Xlsx));
        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheet(1);
        Assert.Equal("Käse", ws.Cell("A2").GetString());
        Assert.Equal(1.5, ws.Cell("B2").GetDouble());
        Assert.Equal(2.25, ws.Cell("B3").GetDouble());
        Assert.Equal(XLDataType.Text, ws.Cell("C2").DataType); // postcode with leading zero
        Assert.Equal("01067", ws.Cell("C2").GetString());
        Assert.Equal("mit ; Semikolon", ws.Cell("D2").GetString());
        Assert.Equal("zwei\nZeilen", ws.Cell("D3").GetString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Xlsx_to_csv_writes_one_file_per_sheet()
    {
        var results = await Engine("xlsx-csv").ConvertAsync(fx.Xlsx, DocFormat.Csv);
        Assert.Equal(["umsatz (Umsatz).csv", "umsatz (Notizen).csv"], results.Select(Path.GetFileName));
        var umsatz = File.ReadAllText(results[0]);
        output.WriteLine(umsatz);
        Assert.Contains("Käse;1,5;2026-10-18", umsatz);
        Assert.Contains("Summe;3,75", umsatz);
        Assert.Equal("\"Hallo; Welt\"\r\n", File.ReadAllText(results[1]));
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(results[0])[..3]);
    }

    [Fact]
    public async Task Csv_and_tsv_round_trip()
    {
        var engine = Engine("csv-tsv");
        var tsv = Assert.Single(await engine.ConvertAsync(fx.Csv, DocFormat.Tsv));
        var csv = Assert.Single(await engine.ConvertAsync(tsv, DocFormat.Csv));
        Assert.Equal(Tables.ReadDelimited(fx.Csv, out _), Tables.ReadDelimited(csv, out var d));
        Assert.Equal(';', d);
        Assert.Contains("Käse\t1,5\t01067\tmit ; Semikolon", File.ReadAllText(tsv));
    }

    [Fact]
    public async Task Office_conversions_when_libreoffice_is_installed()
    {
        if (Skip(fx.Office is null ? "LibreOffice" : null)) return;
        var engine = Engine("office");
        var ods = Assert.Single(await engine.ConvertAsync(fx.Xlsx, DocFormat.Ods));
        var csvFromOds = await engine.ConvertAsync(ods, DocFormat.Csv);
        Assert.Contains("Summe;3,75", File.ReadAllText(csvFromOds[0]));
        var pdf = Assert.Single(await engine.ConvertAsync(fx.Xlsx, DocFormat.Pdf));
        Assert.Equal("%PDF", File.ReadAllText(pdf)[..4]);
        Assert.EndsWith("daten.ods", Assert.Single(await engine.ConvertAsync(fx.Csv, DocFormat.Ods)));
        if (fx.Docx is { } docx) Assert.EndsWith(".pdf", Assert.Single(await engine.ConvertAsync(docx, DocFormat.Pdf)));
        if (fx.Pandoc is not null)
        {
            // Two steps (Pandoc → DOCX → LibreOffice → PDF); the intermediate DOCX must not show up.
            var mdPdf = Assert.Single(await engine.ConvertAsync(fx.Markdown, DocFormat.Pdf));
            using var pdfDoc = PdfDocument.Open(mdPdf);
            Assert.Contains("Titel", pdfDoc.Text(0));
            Assert.Empty(Directory.GetFiles(fx.Sub("office"), "*.docx"));
        }
        var word = Assert.Single(await engine.ConvertAsync(fx.TextPdf, DocFormat.Docx));
        using var zip = ZipFile.OpenRead(word);
        Assert.NotNull(zip.GetEntry("word/document.xml"));
    }

    // ---------------------------------------------------------------- subtitles

    [Fact]
    public async Task Srt_to_vtt()
    {
        var vtt = Assert.Single(await Engine("srt-vtt").ConvertAsync(fx.Srt, DocFormat.Vtt));
        var text = File.ReadAllText(vtt);
        output.WriteLine(text);
        Assert.StartsWith("WEBVTT\n\n00:00:01.000 --> 00:00:03.500\n<i>Hallo</i> &amp; willkommen\n", text);
        Assert.Contains("00:00:04.000 --> 00:00:06.250\nOben\nzweite Zeile\n", text);
    }

    [Fact]
    public async Task Vtt_to_srt()
    {
        var srt = Assert.Single(await Engine("vtt-srt").ConvertAsync(fx.Vtt, DocFormat.Srt));
        var cues = Subtitles.Parse(File.ReadAllText(srt));
        Assert.Equal(2, cues.Count);
        Assert.Equal("Anna: Guten Tag & <b>fett</b>", cues[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), cues[0].End);
        Assert.Equal(TimeSpan.FromHours(1), cues[1].Start);
        Assert.StartsWith("1\r\n00:00:01,000 --> 00:00:02,500\r\n", File.ReadAllText(srt));
    }

    // ------------------------------------------------------------------- wheel

    ActionCatalog Catalog(bool withOffice) =>
        new(new ImageEngine(new OutputPaths(fx.Sub("wheel"))), null, new AppSettings(), Engine("wheel", withOffice));

    [Theory]
    [InlineData("bericht.pdf", "DOCX,MD,TXT,JPG,PNG")]
    [InlineData("brief.docx", "PDF,MD,TXT,EPUB")]
    [InlineData("umsatz.xlsx", "PDF,ODS,CSV,TSV")]
    [InlineData("film.srt", "VTT")]
    [InlineData("folien.pptx", "PDF")]
    public void Wheel_shows_document_segments(string file, string labels)
    {
        var actions = Catalog(withOffice: false).For(WheelMode.Convert, [Path.Combine(fx.Dir, file)]);
        Assert.Equal(labels, string.Join(",", actions.Select(a => a.Label)));
    }

    [Fact]
    public void Segments_needing_libreoffice_are_marked_when_it_is_missing()
    {
        var docx = Path.Combine(fx.Dir, "brief.docx");
        var pdf = Catalog(withOffice: false).For(WheelMode.Convert, [docx]).Single(a => a.Label == "PDF");
        Assert.NotNull(pdf.Unavailable);
        Assert.Equal("LibreOffice missing", pdf.Detail);
        Assert.Empty(pdf.BuildJobs!([docx]));

        var fromPdf = Catalog(withOffice: false).For(WheelMode.Convert, [fx.TextPdf]).Single(a => a.Label == "DOCX");
        Assert.Contains("LibreOffice", fromPdf.Unavailable);
        var png = Catalog(withOffice: false).For(WheelMode.Convert, [fx.TextPdf]).Single(a => a.Label == "PNG");
        Assert.Null(png.Unavailable);
        Assert.Single(png.BuildJobs!([fx.TextPdf, fx.Csv]));
    }

    [Fact]
    public void Documents_have_no_compress_presets()
    {
        Assert.Empty(Catalog(withOffice: true).For(WheelMode.Compress, [fx.TextPdf]));
        Assert.Equal(FileKind.Spreadsheet, FileKinds.Of(fx.Csv));
        Assert.Equal(FileKind.Subtitle, FileKinds.Of(fx.Vtt));
    }
}
