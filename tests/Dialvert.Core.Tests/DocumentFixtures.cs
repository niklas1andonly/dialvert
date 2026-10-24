using System.Text;
using ClosedXML.Excel;
using ImageMagick;
using ImageMagick.Drawing;
using Dialvert.Core.Engines.Document;

namespace Dialvert.Core.Tests;

/// <summary>Synthetic documents, built once per run: PDFs, DOCX (via Pandoc), XLSX, CSV and subtitles.</summary>
public sealed class DocumentFixtures : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "dialvert-doctests-" + Guid.NewGuid().ToString("N")[..8]);
    public Pandoc? Pandoc { get; } = Pandoc.Locate();
    /// <summary>Installed LibreOffice, or the one DIALVERT_SOFFICE points at (e.g. an extracted MSI); tests skip without it.</summary>
    public LibreOffice? Office { get; } = LibreOffice.Locate(Environment.GetEnvironmentVariable("DIALVERT_SOFFICE"));

    /// <summary>Two A4 pages with a real text layer (Helvetica, WinAnsi so umlauts survive).</summary>
    public string TextPdf { get; }
    /// <summary>Two A4 pages that are only pictures, like a scan (written by Magick).</summary>
    public string ScanPdf { get; }
    public string OnePagePdf { get; }
    public string Png { get; }
    public string Markdown { get; }
    public string PlainText { get; }
    public string Csv { get; }
    public string Xlsx { get; }
    public string Srt { get; }
    public string Vtt { get; }

    readonly Lazy<string?> _docx;

    public DocumentFixtures()
    {
        Directory.CreateDirectory(Dir);
        var src = Path.Combine(Dir, "src");
        Directory.CreateDirectory(src);

        TextPdf = Path.Combine(src, "bericht.pdf");
        WriteTextPdf(TextPdf,
        [
            ["Dialvert Testbericht", "Die Größe der Datei ist klein.", "Ein Wort mit Silben-", "trennung am Zeilenende.", "# keine Überschrift"],
            ["Seite zwei", "\u2022 erster Punkt", "\u2022 zweiter Punkt"],
        ]);
        OnePagePdf = Path.Combine(src, "einseitig.pdf");
        WriteTextPdf(OnePagePdf, [["Nur eine Seite"]]);

        ScanPdf = Path.Combine(src, "scan.pdf");
        using (var pages = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.LightSteelBlue, MagickColors.Wheat })
            {
                var page = new MagickImage(color, 595, 842) { Density = new Density(72) };
                new Drawables().FillColor(MagickColors.DarkRed).Rectangle(60, 60, 300, 200).Draw(page);
                pages.Add(page);
            }
            pages.Write(ScanPdf, MagickFormat.Pdf);
        }

        Png = Path.Combine(src, "bild.png");
        using (var img = new MagickImage(MagickColors.Teal, 120, 80)) img.Write(Png);

        Markdown = Path.Combine(src, "notiz.md");
        File.WriteAllText(Markdown,
            $"# Titel\n\nEin Absatz mit Umlauten: äöü ß.\n\n| Name | Wert |\n|------|------|\n| a    | 1    |\n\n![Bild]({Png.Replace('\\', '/')})\n",
            new UTF8Encoding(false));

        PlainText = Path.Combine(src, "liste.txt");
        File.WriteAllText(PlainText, "# kein Titel\n* kein Punkt\n    eingerückt\n", new UTF8Encoding(false));

        Csv = Path.Combine(src, "daten.csv");
        File.WriteAllText(Csv,
            "Artikel;Preis;PLZ;Notiz\r\nKäse;1,5;01067;\"mit ; Semikolon\"\r\nBrot;2.25;80331;\"zwei\r\nZeilen\"\r\n",
            new UTF8Encoding(true));

        Xlsx = Path.Combine(src, "umsatz.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Umsatz");
            ws.Cell("A1").Value = "Artikel";
            ws.Cell("B1").Value = "Preis";
            ws.Cell("A2").Value = "Käse";
            ws.Cell("B2").Value = 1.5;
            ws.Cell("A3").Value = "Brot";
            ws.Cell("B3").Value = 2.25;
            ws.Cell("A4").Value = "Summe";
            ws.Cell("B4").FormulaA1 = "SUM(B2:B3)";
            ws.Cell("C2").Value = new DateTime(2026, 10, 18);
            wb.AddWorksheet("Notizen").Cell("A1").Value = "Hallo; Welt";
            wb.AddWorksheet("Leer");
            wb.SaveAs(Xlsx);
        }

        Srt = Path.Combine(src, "film.srt");
        File.WriteAllText(Srt,
            "1\r\n00:00:01,000 --> 00:00:03,500\r\n<i>Hallo</i> & willkommen\r\n\r\n" +
            "2\r\n00:00:04,000 --> 00:00:06,250\r\n{\\an8}<font color=\"#ff0\">Oben</font>\r\nzweite Zeile\r\n\r\n",
            Encoding.GetEncoding("utf-8"));

        Vtt = Path.Combine(src, "clip.vtt");
        File.WriteAllText(Vtt,
            "WEBVTT - Test\n\nNOTE das ist ein Kommentar\nüber zwei Zeilen\n\n" +
            "eins\n00:01.000 --> 00:02.500 align:start\n<v Anna>Guten Tag</v> &amp; <b>fett</b>\n\n" +
            "01:00:00.000 --> 01:00:01.000\nLetzte\n",
            new UTF8Encoding(false));

        _docx = new(() =>
        {
            if (Pandoc is null) return null;
            var path = Path.Combine(src, "brief.docx");
            Pandoc.RunAsync(["-f", "markdown", "-t", "docx", "-o", path, Markdown], src, CancellationToken.None).GetAwaiter().GetResult();
            return path;
        });
    }

    /// <summary>A Word file made by Pandoc from <see cref="Markdown"/> (heading, umlauts, table, image); null without Pandoc.</summary>
    public string? Docx => _docx.Value;

    public string Sub(string name)
    {
        var d = Path.Combine(Dir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Hand-written minimal PDF 1.4: one Helvetica text block per page, correct xref offsets.</summary>
    static void WriteTextPdf(string path, IReadOnlyList<string[]> pages)
    {
        var latin = Encoding.Latin1;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", pages.Select((_, i) => $"{4 + i * 2} 0 R"))}] /Count {pages.Count} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        for (var i = 0; i < pages.Count; i++)
        {
            var content = "BT /F1 14 Tf 18 TL 72 770 Td " +
                string.Concat(pages[i].Select(l => $"({l.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\u2022", "\x95")}) Tj T* ")) + "ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            objects.Add($"<< /Length {latin.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }

        using var ms = new MemoryStream();
        void Put(string s) { var b = latin.GetBytes(s); ms.Write(b); }
        Put("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Put($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = ms.Position;
        Put($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Put($"{o:D10} 00000 n \n");
        Put($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, ms.ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

[CollectionDefinition("documents")]
public sealed class DocumentCollection : ICollectionFixture<DocumentFixtures>;
