namespace Dialvert.Core.Formats;

public enum FileKind { Other, Image, Video, Audio, Document, Spreadsheet, Subtitle }

public static class FileKinds
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".heic", ".heif", ".avif",
        ".tif", ".tiff", ".bmp", ".gif", ".ico", ".jxl", ".tga", ".psd",
    };

    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".3gp", ".ogv",
    };

    static readonly HashSet<string> Audios = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus",
        ".wma", ".aif", ".aiff", ".alac", ".mka",
    };

    static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".odt", ".rtf", ".md", ".markdown", ".txt", ".epub", ".html", ".htm",
        ".pptx", ".ppt", ".odp",
    };

    static readonly HashSet<string> Spreadsheets = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xlsm", ".xls", ".ods", ".csv", ".tsv",
    };

    static readonly HashSet<string> Subtitles = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt" };

    public static FileKind Of(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return FileKind.Image;
        if (Videos.Contains(ext)) return FileKind.Video;
        if (Audios.Contains(ext)) return FileKind.Audio;
        if (Documents.Contains(ext)) return FileKind.Document;
        if (Spreadsheets.Contains(ext)) return FileKind.Spreadsheet;
        if (Subtitles.Contains(ext)) return FileKind.Subtitle;
        return FileKind.Other;
    }

    /// <summary>The kind most of the given files share (ties follow the enum order: image &gt; video &gt; audio &gt; …).</summary>
    public static FileKind Dominant(IEnumerable<string> paths)
    {
        var counts = paths.Select(Of).Where(k => k != FileKind.Other)
                          .GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        if (counts.Count == 0) return FileKind.Other;
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }
}
