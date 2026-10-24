using Dialvert.Core.Engines.Document;
using Dialvert.Core.Engines.Image;
using Dialvert.Core.Engines.Media;
using Dialvert.Core.Formats;
using Dialvert.Core.Jobs;
using Dialvert.Core.Settings;

namespace Dialvert.Core.Actions;

/// <summary>Things the UI does itself (clipboard, shell) rather than queueing a job.</summary>
public enum ImmediateAction { None, CopyPath, OpenTerminal, RevealInExplorer }

/// <summary>
/// One segment of the action wheel. <paramref name="Unavailable"/> is set when a needed program is
/// missing: the segment is still shown (dimmed) and explains what to install instead of running.
/// </summary>
public sealed record WheelAction(
    string Id,
    string Label,
    string Detail,
    string? Glyph,
    Func<IReadOnlyList<string>, IEnumerable<Job>>? BuildJobs,
    ImmediateAction Immediate = ImmediateAction.None,
    string? Unavailable = null);

/// <summary>Decides which wheel segments make sense for a set of dropped files.</summary>
public sealed class ActionCatalog
{
    readonly ImageEngine _images;
    readonly MediaEngine? _media;
    readonly DocumentEngine? _documents;
    readonly AppSettings _settings;

    public ActionCatalog(ImageEngine images, MediaEngine? media, AppSettings settings, DocumentEngine? documents = null)
    {
        _images = images;
        _media = media;
        _settings = settings;
        _documents = documents;
    }

    public bool HasMedia => _media is not null;

    public IReadOnlyList<WheelAction> For(WheelMode mode, IReadOnlyList<string> files)
    {
        var kind = FileKinds.Dominant(files);
        return mode switch
        {
            WheelMode.Convert => Convert(kind, files).Where(a => !AllAlreadyIn(files, a.Label)).ToList(),
            WheelMode.Compress => Compress(kind),
            _ => Tools(kind),
        };
    }

    /// <summary>Hides "→ WEBP" when every dropped file already is a WebP.</summary>
    static bool AllAlreadyIn(IReadOnlyList<string> files, string label)
    {
        var ext = "." + label.ToLowerInvariant();
        static string Norm(string e) => e switch
        {
            ".jpeg" or ".jfif" => ".jpg", ".tif" => ".tiff", ".markdown" => ".md", ".htm" => ".html", ".xlsm" => ".xlsx", _ => e,
        };
        return files.Count > 0 && files.All(f => Norm(Path.GetExtension(f).ToLowerInvariant()) == Norm(ext));
    }

    // ------------------------------------------------------------------ convert

    IReadOnlyList<WheelAction> Convert(FileKind kind, IReadOnlyList<string> files)
    {
        var list = new List<WheelAction>();
        switch (kind)
        {
            case FileKind.Image:
                foreach (var f in new[] { ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP, ImageFormat.Avif,
                                          ImageFormat.Heic, ImageFormat.Tiff, ImageFormat.Bmp })
                {
                    if (!ImageEngine.CanWrite(f)) continue;
                    var label = f == ImageFormat.Jpeg ? "JPG" : f.ToString().ToUpperInvariant();
                    list.Add(new($"img.to.{f}", label, "Convert image", null, files => PerFile(files, FileKind.Image,
                        p => $"{Path.GetFileName(p)} → {label}", JobLane.Image,
                        (p, _, ct) => Task.FromResult(_images.Convert(p, f, new ImageOptions(_settings.ImageQuality), ct)))));
                }
                list.Add(new("img.to.pdf", "PDF", "All into one PDF", null, files =>
                {
                    var images = files.Where(p => FileKinds.Of(p) == FileKind.Image).ToList();
                    return images.Count == 0 ? [] :
                    [
                        new Job($"{images.Count} image(s) → PDF", JobLane.Image, images,
                            (progress, ct) => Task.FromResult<IReadOnlyList<string>>([_images.ImagesToPdf(images, progress, ct)])),
                    ];
                }));
                break;

            case FileKind.Video when _media is not null:
                foreach (var f in Enum.GetValues<VideoFormat>())
                {
                    var label = f.ToString().ToUpperInvariant();
                    list.Add(new($"vid.to.{f}", label, f == VideoFormat.Gif ? "Animated GIF" : "Convert video", null,
                        files => PerFile(files, FileKind.Video, p => $"{Path.GetFileName(p)} → {label}", JobLane.Media,
                            (p, pr, ct) => _media.ConvertVideoAsync(p, f, pr, ct))));
                }
                list.Add(new("vid.to.mp3", "MP3", "Audio only", null, files => PerFile(files, FileKind.Video,
                    p => $"{Path.GetFileName(p)} → MP3", JobLane.Media, (p, pr, ct) => _media.ConvertAudioAsync(p, AudioFormat.Mp3, pr, ct))));
                break;

            case FileKind.Audio when _media is not null:
                foreach (var f in Enum.GetValues<AudioFormat>())
                {
                    var label = f.ToString().ToUpperInvariant();
                    list.Add(new($"aud.to.{f}", label, "Convert audio", null, files => PerFile(files, FileKind.Audio,
                        p => $"{Path.GetFileName(p)} → {label}", JobLane.Media, (p, pr, ct) => _media.ConvertAudioAsync(p, f, pr, ct))));
                }
                break;

            case FileKind.Document or FileKind.Spreadsheet or FileKind.Subtitle when _documents is not null:
                list.AddRange(DocumentTargets(_documents, files));
                break;
        }
        return list;
    }

    // ---------------------------------------------------------------- documents

    /// <summary>Targets of the most common document format among the drop; other files join where a route exists.</summary>
    IEnumerable<WheelAction> DocumentTargets(DocumentEngine docs, IReadOnlyList<string> files)
    {
        var source = files.Select(FormatInfo.DocFormatOf).OfType<DocFormat>()
                          .GroupBy(f => f).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (DocFormat?)g.Key).FirstOrDefault();
        if (source is not { } from) yield break;
        foreach (var to in DocumentEngine.TargetsFor(from))
        {
            var label = FormatInfo.Label(to);
            var missing = docs.MissingTool(from, to);
            var unavailable = missing switch
            {
                "LibreOffice" => "needs LibreOffice – not installed (free: libreoffice.org)",
                "Pandoc" => "needs Pandoc – missing from the engines folder (scripts\\fetch-engines.ps1)",
                _ => null,
            };
            yield return new WheelAction($"doc.to.{to}", label, missing is null ? DocDetail(from, to) : $"{missing} missing", null,
                fs => fs.Where(p => DocumentEngine.CanConvert(p, to) && docs.MissingTool(FormatInfo.DocFormatOf(p)!.Value, to) is null)
                        .Select(p => new Job($"{Path.GetFileName(p)} → {label}", JobLane.Document, [p],
                            (progress, ct) => docs.ConvertAsync(p, to, progress, ct))),
                Unavailable: unavailable);
        }
    }

    static string DocDetail(DocFormat from, DocFormat to) => (from, to) switch
    {
        (DocFormat.Pdf, DocFormat.Jpg or DocFormat.Png) => "Pages as images",
        (DocFormat.Pdf, DocFormat.Txt) => "Extract text",
        (DocFormat.Pdf, DocFormat.Md) => "Text as Markdown",
        (DocFormat.Pdf, DocFormat.Docx) => "Layout may differ",
        (_, DocFormat.Pdf) => "via LibreOffice",
        (DocFormat.Xlsx or DocFormat.Xls or DocFormat.Ods, DocFormat.Csv or DocFormat.Tsv) => "one file per sheet",
        (DocFormat.Srt or DocFormat.Vtt, _) => "Subtitles",
        (_, DocFormat.Csv or DocFormat.Tsv or DocFormat.Xlsx or DocFormat.Ods) => "Spreadsheet",
        _ => "Document",
    };

    // ----------------------------------------------------------------- compress

    IReadOnlyList<WheelAction> Compress(FileKind kind)
    {
        if (kind is not (FileKind.Image or FileKind.Video or FileKind.Audio) || (kind != FileKind.Image && _media is null)) return [];
        var target = kind == FileKind.Image ? _settings.ImageTargetKb * 1024L : _settings.VideoTargetMb * 1024L * 1024;
        return
        [
            CompressAction("cmp.high", "High", "Best quality", CompressSpec.FromPreset(CompressPreset.High)),
            CompressAction("cmp.balanced", "Medium", "Balanced", CompressSpec.FromPreset(CompressPreset.Balanced)),
            CompressAction("cmp.small", "Small", "Smallest file", CompressSpec.FromPreset(CompressPreset.Small)),
            CompressAction("cmp.p50", "50 %", "of original size", CompressSpec.FromPercent(50)),
            CompressAction("cmp.p25", "25 %", "of original size", CompressSpec.FromPercent(25)),
            CompressAction("cmp.target", $"≤ {CompressSpec.Describe(target)}", "Target size", CompressSpec.FromTarget(target)),
        ];
    }

    WheelAction CompressAction(string id, string label, string detail, CompressSpec spec) =>
        new(id, label, detail, null, files => files.Select(p => CompressJob(p, spec, label)).OfType<Job>());

    Job? CompressJob(string path, CompressSpec spec, string label)
    {
        var kind = FileKinds.Of(path);
        // A fixed target size only makes sense per kind: the image target for images, the video target for media.
        if (spec.Mode == CompressMode.TargetSize)
            spec = spec with { TargetBytes = kind == FileKind.Image ? _settings.ImageTargetKb * 1024L : _settings.VideoTargetMb * 1024L * 1024 };
        var title = $"{Path.GetFileName(path)} · compress ({label})";
        return kind switch
        {
            FileKind.Image => new Job(title, JobLane.Image, [path],
                (_, ct) => Task.FromResult<IReadOnlyList<string>>([_images.Compress(path, spec, _settings.StripMetadataOnCompress, ct)])),
            FileKind.Video or FileKind.Audio when _media is not null => new Job(title, JobLane.Media, [path],
                async (pr, ct) => [await _media.CompressAsync(path, spec, pr, ct)]),
            _ => null,
        };
    }

    // -------------------------------------------------------------------- tools

    IReadOnlyList<WheelAction> Tools(FileKind kind)
    {
        var list = new List<WheelAction>();
        if (kind == FileKind.Image)
        {
            list.Add(new("img.strip", "Metadata", "remove (EXIF/GPS)", "", files => PerFile(files, FileKind.Image,
                p => $"{Path.GetFileName(p)} · remove metadata", JobLane.Image,
                (p, _, ct) => Task.FromResult(_images.StripMetadata(p, ct)))));
            list.Add(new("img.half", "50 %", "Halve size", "", files => PerFile(files, FileKind.Image,
                p => $"{Path.GetFileName(p)} · 50 %", JobLane.Image,
                (p, _, ct) => Task.FromResult(_images.Resize(p, new ImageOptions(_settings.ImageQuality, Scale: 0.5), ct)))));
            list.Add(new("img.1920", "1920 px", "Longest edge", "", files => PerFile(files, FileKind.Image,
                p => $"{Path.GetFileName(p)} · 1920 px", JobLane.Image,
                (p, _, ct) => Task.FromResult(_images.Resize(p, new ImageOptions(_settings.ImageQuality, MaxEdge: 1920), ct)))));
        }
        if (kind == FileKind.Video && _media is not null)
        {
            list.Add(new("vid.extract", "Audio", "extract losslessly", "", files => PerFile(files, FileKind.Video,
                p => $"{Path.GetFileName(p)} · extract audio", JobLane.Media, (p, pr, ct) => _media.ExtractAudioAsync(p, pr, ct))));
        }
        list.Add(new("file.copypath", "Path", "copy", "", null, ImmediateAction.CopyPath));
        list.Add(new("file.reveal", "Folder", "show in Explorer", "", null, ImmediateAction.RevealInExplorer));
        list.Add(new("file.terminal", "Terminal", "open here", "", null, ImmediateAction.OpenTerminal));
        return list;
    }

    // ------------------------------------------------------------------ helpers

    static IEnumerable<Job> PerFile(IReadOnlyList<string> files, FileKind kind, Func<string, string> title, JobLane lane,
                                    Func<string, IProgress<double>, CancellationToken, Task<string>> work) =>
        files.Where(p => FileKinds.Of(p) == kind).Select(p =>
            new Job(title(p), lane, [p], async (progress, ct) => [await work(p, progress, ct)]));
}
