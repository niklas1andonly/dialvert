namespace Dialvert.Core.Formats;

public enum ImageFormat { Jpeg, Png, WebP, Avif, Heic, Tiff, Bmp }

public enum VideoFormat { Mp4, Mov, Mkv, WebM, Avi, Gif }

public enum AudioFormat { Mp3, M4a, Wav, Flac, Ogg, Opus }

/// <summary>Documents, spreadsheets and subtitles – as sources and as targets (JPG/PNG only as page renders).</summary>
public enum DocFormat
{
    Pdf, Docx, Doc, Odt, Rtf, Md, Txt, Epub, Html, Pptx, Ppt, Odp,
    Xlsx, Xls, Ods, Csv, Tsv,
    Srt, Vtt,
    Jpg, Png,
}

public static class FormatInfo
{
    public static string Extension(ImageFormat f) => f switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.WebP => ".webp",
        ImageFormat.Avif => ".avif",
        ImageFormat.Heic => ".heic",
        ImageFormat.Tiff => ".tiff",
        ImageFormat.Bmp => ".bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    public static string Extension(VideoFormat f) => "." + f.ToString().ToLowerInvariant();

    public static string Extension(AudioFormat f) => "." + f.ToString().ToLowerInvariant();

    public static string Extension(DocFormat f) => "." + f.ToString().ToLowerInvariant();

    public static string Label(DocFormat f) => f.ToString().ToUpperInvariant();

    public static DocFormat? DocFormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => DocFormat.Pdf,
        ".docx" => DocFormat.Docx,
        ".doc" => DocFormat.Doc,
        ".odt" => DocFormat.Odt,
        ".rtf" => DocFormat.Rtf,
        ".md" or ".markdown" => DocFormat.Md,
        ".txt" => DocFormat.Txt,
        ".epub" => DocFormat.Epub,
        ".html" or ".htm" => DocFormat.Html,
        ".pptx" => DocFormat.Pptx,
        ".ppt" => DocFormat.Ppt,
        ".odp" => DocFormat.Odp,
        ".xlsx" or ".xlsm" => DocFormat.Xlsx,
        ".xls" => DocFormat.Xls,
        ".ods" => DocFormat.Ods,
        ".csv" => DocFormat.Csv,
        ".tsv" => DocFormat.Tsv,
        ".srt" => DocFormat.Srt,
        ".vtt" => DocFormat.Vtt,
        _ => null,
    };

    public static ImageFormat? ImageFormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jfif" => ImageFormat.Jpeg,
        ".png" => ImageFormat.Png,
        ".webp" => ImageFormat.WebP,
        ".avif" => ImageFormat.Avif,
        ".heic" or ".heif" => ImageFormat.Heic,
        ".tif" or ".tiff" => ImageFormat.Tiff,
        ".bmp" => ImageFormat.Bmp,
        _ => null,
    };

    public static AudioFormat? AudioFormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => AudioFormat.Mp3,
        ".m4a" or ".aac" => AudioFormat.M4a,
        ".wav" => AudioFormat.Wav,
        ".flac" => AudioFormat.Flac,
        ".ogg" or ".oga" => AudioFormat.Ogg,
        ".opus" => AudioFormat.Opus,
        _ => null,
    };

    public static bool IsLossy(ImageFormat f) => f is ImageFormat.Jpeg or ImageFormat.WebP or ImageFormat.Avif or ImageFormat.Heic;
}
