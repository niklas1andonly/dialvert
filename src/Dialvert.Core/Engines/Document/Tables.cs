using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace Dialvert.Core.Engines.Document;

/// <summary>
/// How delimited text is written. CSV follows the Windows list separator so Excel opens it directly:
/// German systems get ";" with decimal comma, others "," with decimal point.
/// </summary>
public sealed record CsvDialect(char Delimiter, CultureInfo Culture)
{
    public static CsvDialect Csv(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return culture.TextInfo.ListSeparator == ";" ? new(';', culture) : new(',', CultureInfo.InvariantCulture);
    }

    public static CsvDialect Tsv(CultureInfo? culture = null)
    {
        var csv = Csv(culture);
        return new('\t', csv.Culture);
    }
}

/// <summary>XLSX ↔ CSV/TSV in-process (ClosedXML, MIT) plus a small RFC 4180 reader/writer.</summary>
public static partial class Tables
{
    // ------------------------------------------------------------ delimited

    /// <summary>Parses CSV/TSV; the delimiter is guessed from the first lines (tab, semicolon, comma).</summary>
    public static List<string[]> ReadDelimited(string path, out char delimiter)
    {
        var text = TextFiles.Read(path);
        delimiter = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : GuessDelimiter(text);
        return Parse(text, delimiter);
    }

    static char GuessDelimiter(string text)
    {
        var sample = string.Join("\n", text.Split('\n').Take(20));
        var best = ',';
        var bestCount = 0;
        foreach (var c in new[] { '\t', ';', ',', '|' })
        {
            // Count outside quotes only.
            var count = 0;
            var quoted = false;
            foreach (var ch in sample)
            {
                if (ch == '"') quoted = !quoted;
                else if (ch == c && !quoted) count++;
            }
            if (count > bestCount) { best = c; bestCount = count; }
        }
        return best;
    }

    public static List<string[]> Parse(string text, char delimiter)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }
        return rows;
    }

    public static string Format(IEnumerable<IReadOnlyList<string>> rows, char delimiter)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(delimiter);
                var v = row[i];
                if (v.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 || (v.Length > 0 && (v[0] == ' ' || v[^1] == ' ')))
                    sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
                else sb.Append(v);
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Re-delimits CSV ↔ TSV without touching the values.</summary>
    public static void Redelimit(string input, string output, char delimiter)
    {
        var rows = ReadDelimited(input, out _);
        TextFiles.Write(output, Format(rows, delimiter), bom: true);
    }

    // ----------------------------------------------------------------- xlsx

    /// <summary>Writes a delimited file as a one-sheet workbook; plain numbers become numbers, everything else stays text.</summary>
    public static void DelimitedToXlsx(string input, string output, CancellationToken ct = default)
    {
        var rows = ReadDelimited(input, out var delimiter);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName(Path.GetFileNameWithoutExtension(input)));
        var widths = new Dictionary<int, int>();
        for (var r = 0; r < rows.Count; r++)
        {
            if (r % 1000 == 0) ct.ThrowIfCancellationRequested();
            for (var c = 0; c < rows[r].Length; c++)
            {
                var raw = rows[r][c];
                if (raw.Length == 0) continue;
                var cell = ws.Cell(r + 1, c + 1);
                if (TryNumber(raw, delimiter, out var number)) cell.Value = number;
                else cell.Value = raw;
                if (r < 500) widths[c] = Math.Max(widths.GetValueOrDefault(c), raw.Length);
            }
        }
        foreach (var (c, w) in widths) ws.Column(c + 1).Width = Math.Clamp(w + 2, 6, 60);
        wb.SaveAs(output);
    }

    /// <summary>
    /// Conservative number detection: optional sign, digits, one decimal mark. Values with leading zeros
    /// (postcodes, IDs) or more than 15 digits (IBANs, phone numbers) stay text, so nothing is lost.
    /// </summary>
    static bool TryNumber(string raw, char delimiter, out double value)
    {
        value = 0;
        var s = raw.Trim();
        if (!NumberShape().IsMatch(s)) return false;
        var digits = s.Count(char.IsAsciiDigit);
        var unsigned = s.TrimStart('-', '+');
        if (digits > 15 || (unsigned.Length > 1 && unsigned[0] == '0' && unsigned[1] != '.' && unsigned[1] != ',')) return false;
        if (s.Contains(',') && delimiter == ',') return false;
        return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"^[-+]?\d+([.,]\d+)?$")]
    private static partial Regex NumberShape();

    /// <summary>
    /// Writes every non-empty worksheet as its own file: "name.csv" for one sheet,
    /// "name (Sheet).csv" for several. Formula cells contribute their (cached) result.
    /// </summary>
    public static IReadOnlyList<string> XlsxToDelimited(string input, string firstOutput, CsvDialect dialect, CancellationToken ct = default)
    {
        using var wb = new XLWorkbook(input);
        var sheets = wb.Worksheets.Where(ws => ws.LastCellUsed() is not null).ToList();
        if (sheets.Count == 0) throw new InvalidOperationException("The spreadsheet is empty.");

        var dir = Path.GetDirectoryName(firstOutput)!;
        var stem = Path.GetFileNameWithoutExtension(firstOutput);
        var ext = Path.GetExtension(firstOutput);
        var written = new List<string>();
        try
        {
            foreach (var ws in sheets)
            {
                ct.ThrowIfCancellationRequested();
                var target = sheets.Count == 1 ? firstOutput : Path.Combine(dir, $"{stem} ({SafeFileName(ws.Name)}){ext}");
                var temp = OutputPaths.TempFor(target);
                try
                {
                    TextFiles.Write(temp, Format(SheetRows(ws, dialect.Culture), dialect.Delimiter), bom: true);
                    written.Add(OutputPaths.Commit(temp, target));
                }
                catch { OutputPaths.TryDelete(temp); throw; }
            }
            return written;
        }
        catch (OperationCanceledException)
        {
            foreach (var w in written) OutputPaths.TryDelete(w);
            throw;
        }
    }

    static IEnumerable<IReadOnlyList<string>> SheetRows(IXLWorksheet ws, CultureInfo culture)
    {
        var last = ws.LastCellUsed()!.Address;
        var lastCol = ws.LastColumnUsed()!.ColumnNumber();
        for (var r = 1; r <= last.RowNumber; r++)
        {
            var row = new string[lastCol];
            for (var c = 1; c <= lastCol; c++) row[c - 1] = CellText(ws.Cell(r, c), culture);
            yield return row;
        }
    }

    static string CellText(IXLCell cell, CultureInfo culture)
    {
        XLCellValue v;
        try { v = cell.Value; }
        catch (Exception) { v = cell.CachedValue; } // functions ClosedXML cannot evaluate: use what Excel stored
        return v.Type switch
        {
            XLDataType.Blank => "",
            XLDataType.Boolean => v.GetBoolean() ? "TRUE" : "FALSE",
            XLDataType.Number => v.GetNumber().ToString("G15", culture),
            XLDataType.DateTime => v.GetDateTime() is var d && d.TimeOfDay == TimeSpan.Zero
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            XLDataType.TimeSpan => v.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),
            XLDataType.Error => v.GetError() switch
            {
                XLError.CellReference => "#REF!",
                XLError.IncompatibleValue => "#VALUE!",
                XLError.DivisionByZero => "#DIV/0!",
                XLError.NameNotRecognized => "#NAME?",
                XLError.NoValueAvailable => "#N/A",
                XLError.NullValue => "#NULL!",
                XLError.NumberInvalid => "#NUM!",
                var e => "#" + e,
            },
            _ => v.GetText(),
        };
    }

    static string SheetName(string name)
    {
        var clean = new string(name.Where(ch => ch is not (':' or '\\' or '/' or '?' or '*' or '[' or ']')).ToArray()).Trim('\'');
        if (clean.Length == 0) clean = "Sheet1";
        return clean.Length > 31 ? clean[..31] : clean;
    }

    static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
