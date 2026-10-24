using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dialvert.Core.Engines.Document;

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>SRT ↔ WebVTT. Keeps timing and b/i/u styling; drops what the other format cannot express.</summary>
public static partial class Subtitles
{
    // ------------------------------------------------------------------ parse

    public static List<SubtitleCue> Parse(string text)
    {
        var cues = new List<SubtitleCue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            // Find the next timing line; anything before it (SRT index, VTT header/identifier, NOTE/STYLE blocks) is skipped.
            var match = Timing().Match(lines[i]);
            if (!match.Success)
            {
                if (IsVttBlock(lines[i])) i = SkipBlock(lines, i);
                else i++;
                continue;
            }
            var start = ParseTime(match.Groups["a"].Value);
            var end = ParseTime(match.Groups["b"].Value);
            i++;
            var body = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length > 0 && !Timing().IsMatch(lines[i])) body.Add(lines[i++]);
            if (body.Count > 0) cues.Add(new SubtitleCue(start, end, string.Join("\n", body)));
        }
        return cues;
    }

    static bool IsVttBlock(string line) =>
        line.StartsWith("NOTE", StringComparison.Ordinal) || line.StartsWith("STYLE", StringComparison.Ordinal)
        || line.StartsWith("REGION", StringComparison.Ordinal);

    static int SkipBlock(string[] lines, int i)
    {
        while (i < lines.Length && lines[i].Trim().Length > 0) i++;
        return i;
    }

    /// <summary>"01:02:03,456", "01:02:03.456" or VTT's short "02:03.456".</summary>
    static TimeSpan ParseTime(string s)
    {
        var parts = s.Replace(',', '.').Split(':');
        var (h, m, sec) = parts.Length == 3 ? (int.Parse(parts[0]), int.Parse(parts[1]), parts[2]) : (0, int.Parse(parts[0]), parts[1]);
        var seconds = double.Parse(sec, CultureInfo.InvariantCulture);
        return new TimeSpan(h, m, 0) + TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
    }

    // ------------------------------------------------------------------ write

    public static string ToSrt(IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        for (var n = 0; n < cues.Count; n++)
        {
            var c = cues[n];
            sb.Append(n + 1).Append("\r\n")
              .Append(Time(c.Start, ',')).Append(" --> ").Append(Time(c.End, ',')).Append("\r\n")
              .Append(VttTextToSrt(c.Text).Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    public static string ToVtt(IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
        {
            sb.Append(Time(c.Start, '.')).Append(" --> ").Append(Time(c.End, '.')).Append('\n')
              .Append(SrtTextToVtt(c.Text)).Append("\n\n");
        }
        return sb.ToString();
    }

    static string Time(TimeSpan t, char sep) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{sep}{t.Milliseconds:000}";

    /// <summary>SRT allows &lt;font&gt; and ASS position codes; VTT needs escaped &amp; and &lt;.</summary>
    static string SrtTextToVtt(string text)
    {
        text = AssCode().Replace(text, "");
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match tag in AnyTag().Matches(text))
        {
            sb.Append(Escape(text[last..tag.Index]));
            if (SimpleTag().IsMatch(tag.Value)) sb.Append(tag.Value.ToLowerInvariant());
            last = tag.Index + tag.Length;
        }
        sb.Append(Escape(text[last..]));
        return sb.ToString();

        static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>VTT voice/class/timestamp tags are dropped (voice becomes "Name: "), entities decoded.</summary>
    static string VttTextToSrt(string text)
    {
        text = Voice().Replace(text, m => m.Groups["name"].Value.Trim() + ": ");
        text = AnyTag().Replace(text, m => SimpleTag().IsMatch(m.Value) ? m.Value.ToLowerInvariant() : "");
        return text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&nbsp;", " ")
                   .Replace("&lrm;", "‎").Replace("&rlm;", "‏").Replace("&amp;", "&");
    }

    [GeneratedRegex(@"^\s*(?<a>(\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*(?<b>(\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})")]
    private static partial Regex Timing();

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"^</?[biuBIU]>$")]
    private static partial Regex SimpleTag();

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex AssCode();

    [GeneratedRegex(@"<v(\.[^ >]*)?\s+(?<name>[^>]+)>")]
    private static partial Regex Voice();
}
