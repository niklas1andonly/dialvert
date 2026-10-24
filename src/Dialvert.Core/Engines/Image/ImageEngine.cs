using ImageMagick;
using Dialvert.Core.Formats;

namespace Dialvert.Core.Engines.Image;

public sealed record ImageOptions(int Quality = 88, bool StripMetadata = false, uint? MaxEdge = null, double? Scale = null);

/// <summary>Image work in-process via Magick.NET (ImageMagick).</summary>
public sealed class ImageEngine
{
    readonly OutputPaths _out;

    public ImageEngine(OutputPaths output) => _out = output;

    public static bool CanWrite(ImageFormat format)
    {
        var mf = ToMagick(format);
        return MagickNET.SupportedFormats.Any(f => f.Format == mf && f.SupportsWriting);
    }

    public static bool CanRead(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return MagickNET.SupportedFormats.Any(f => f.SupportsReading &&
            string.Equals(f.Format.ToString(), ext, StringComparison.OrdinalIgnoreCase)) || FormatInfo.ImageFormatOf(path) is not null;
    }

    // ---------------------------------------------------------------- convert

    public string Convert(string input, ImageFormat target, ImageOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ImageOptions();
        var final = _out.For(input, FormatInfo.Extension(target));
        var temp = OutputPaths.TempFor(final);
        try
        {
            using var frames = new MagickImageCollection(input);
            ct.ThrowIfCancellationRequested();
            var keepAnimation = frames.Count > 1 && target is ImageFormat.WebP or ImageFormat.Tiff;
            if (keepAnimation)
            {
                frames.Coalesce();
                foreach (var f in frames) Prepare(f, target, options);
                frames.Write(temp, ToMagick(target));
            }
            else
            {
                var img = frames[0];
                Prepare(img, target, options);
                img.Write(temp, ToMagick(target));
            }
            return OutputPaths.Commit(temp, final);
        }
        catch { OutputPaths.TryDelete(temp); throw; }
    }

    // --------------------------------------------------------------- compress

    public string Compress(string input, CompressSpec spec, bool stripMetadata = true, CancellationToken ct = default)
    {
        var source = FormatInfo.ImageFormatOf(input) ?? ImageFormat.Jpeg;
        // Lossless/uncompressed sources and formats we cannot write become JPEG (or PNG to keep alpha).
        using var probe = new MagickImage();
        probe.Ping(input);
        var target = source switch
        {
            ImageFormat.Bmp or ImageFormat.Tiff => probe.HasAlpha ? ImageFormat.WebP : ImageFormat.Jpeg,
            ImageFormat.Heic when !CanWrite(ImageFormat.Heic) => ImageFormat.Jpeg,
            _ => source,
        };

        var originalBytes = new FileInfo(input).Length;
        var budget = spec.BudgetFor(originalBytes);
        var final = _out.For(input, FormatInfo.Extension(target), target == source ? "compressed" : null);

        using var original = new MagickImage(input);
        original.AutoOrient();
        if (stripMetadata) NormalizeAndStrip(original);

        byte[] best = budget is null
            ? EncodePreset(original, target, spec.Preset)
            : EncodeToBudget(original, target, budget.Value, ct);

        // Never hand back something bigger than what came in for a preset run.
        if (budget is null && best.Length >= originalBytes && target == source)
            best = File.ReadAllBytes(input);

        var temp = OutputPaths.TempFor(final);
        try
        {
            File.WriteAllBytes(temp, best);
            return OutputPaths.Commit(temp, final);
        }
        catch { OutputPaths.TryDelete(temp); throw; }
    }

    static byte[] EncodePreset(MagickImage original, ImageFormat target, CompressPreset preset)
    {
        using var img = (MagickImage)original.Clone();
        if (target == ImageFormat.Png)
        {
            if (preset != CompressPreset.High)
                Quantize(img, preset == CompressPreset.Balanced ? 256u : 96u);
            return Encode(img, target, 0);
        }
        if (preset == CompressPreset.Small) FitWithin(img, 2560);
        var q = preset switch { CompressPreset.High => 85, CompressPreset.Balanced => 74, _ => 60 };
        if (target == ImageFormat.Avif) q -= 10; // AVIF quality scale runs "higher" than JPEG's
        return Encode(img, target, q);
    }

    /// <summary>Binary search on quality, then on colors (PNG), then shrink until it fits.</summary>
    static byte[] EncodeToBudget(MagickImage original, ImageFormat target, long budget, CancellationToken ct)
    {
        byte[]? smallest = null;
        double scale = 1.0;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var img = (MagickImage)original.Clone();
            if (scale < 1.0) img.Resize(new Percentage(scale * 100));

            byte[]? fit = FormatInfo.IsLossy(target)
                ? SearchQuality(img, target, budget, ref smallest, ct)
                : SearchLossless(img, target, budget, ref smallest, ct);
            if (fit is not null) return fit;

            // Estimate the scale needed from how far off the smallest attempt was (bytes ~ pixels).
            var ratio = (double)budget / smallest!.Length;
            scale *= Math.Clamp(Math.Sqrt(ratio) * 0.95, 0.3, 0.9);
        }
        return smallest!;
    }

    static byte[]? SearchQuality(MagickImage img, ImageFormat target, long budget, ref byte[]? smallest, CancellationToken ct)
    {
        int lo = 20, hi = 92;
        byte[]? bestFit = null;
        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();
            var q = (lo + hi) / 2;
            var data = Encode(img, target, q);
            if (smallest is null || data.Length < smallest.Length) smallest = data;
            if (data.Length <= budget) { bestFit = data; lo = q + 1; }
            else hi = q - 1;
        }
        return bestFit;
    }

    static byte[]? SearchLossless(MagickImage img, ImageFormat target, long budget, ref byte[]? smallest, CancellationToken ct)
    {
        var data = Encode(img, target, 0);
        smallest = smallest is null || data.Length < smallest.Length ? data : smallest;
        if (data.Length <= budget) return data;
        if (target != ImageFormat.Png) return null;
        foreach (var colors in new uint[] { 256, 128, 64 })
        {
            ct.ThrowIfCancellationRequested();
            using var q = (MagickImage)img.Clone();
            Quantize(q, colors);
            data = Encode(q, target, 0);
            if (data.Length < smallest.Length) smallest = data;
            if (data.Length <= budget) return data;
        }
        return null;
    }

    // ------------------------------------------------------------------ tools

    public string Resize(string input, ImageOptions options, CancellationToken ct = default)
    {
        var target = FormatInfo.ImageFormatOf(input) is { } f && CanWrite(f) ? f : ImageFormat.Png;
        var suffix = options.MaxEdge is { } edge ? $"{edge}px" : $"{options.Scale * 100:0}%";
        var final = _out.For(input, FormatInfo.Extension(target), suffix);
        return WriteSingle(input, final, target, img =>
        {
            img.AutoOrient();
            if (options.MaxEdge is { } max) FitWithin(img, max);
            else if (options.Scale is { } s) img.Resize(new Percentage(s * 100));
            img.Quality = (uint)options.Quality;
        }, ct);
    }

    public string StripMetadata(string input, CancellationToken ct = default)
    {
        var target = FormatInfo.ImageFormatOf(input) is { } f && CanWrite(f) ? f : ImageFormat.Png;
        var final = _out.For(input, FormatInfo.Extension(target), "no metadata");
        return WriteSingle(input, final, target, img =>
        {
            img.AutoOrient();
            NormalizeAndStrip(img);
            img.Quality = 92;
        }, ct);
    }

    public string ImagesToPdf(IReadOnlyList<string> inputs, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var name = inputs.Count == 1 ? Path.GetFileNameWithoutExtension(inputs[0]) : "images";
        var final = _out.ForGroup(inputs[0], name, ".pdf");
        var temp = OutputPaths.TempFor(final);
        try
        {
            using var pages = new MagickImageCollection();
            for (var i = 0; i < inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var img = new MagickImage(inputs[i]);
                img.AutoOrient();
                if (img.HasAlpha) { img.BackgroundColor = MagickColors.White; img.Alpha(AlphaAction.Remove); }
                // 150 dpi keeps typical photos at a sensible page size.
                img.Density = new Density(150, 150, DensityUnit.PixelsPerInch);
                img.Settings.Compression = CompressionMethod.JPEG;
                img.Quality = 90;
                pages.Add(img);
                progress?.Report((i + 1.0) / (inputs.Count + 1));
            }
            pages.Write(temp, MagickFormat.Pdf);
            return OutputPaths.Commit(temp, final);
        }
        catch { OutputPaths.TryDelete(temp); throw; }
    }

    // ---------------------------------------------------------------- helpers

    static string WriteSingle(string input, string final, ImageFormat target, Action<MagickImage> edit, CancellationToken ct)
    {
        var temp = OutputPaths.TempFor(final);
        try
        {
            using var img = new MagickImage(input);
            ct.ThrowIfCancellationRequested();
            edit(img);
            if (target == ImageFormat.Jpeg) FlattenAlpha(img);
            img.Write(temp, ToMagick(target));
            return OutputPaths.Commit(temp, final);
        }
        catch { OutputPaths.TryDelete(temp); throw; }
    }

    static void Prepare(IMagickImage<byte> img, ImageFormat target, ImageOptions options)
    {
        img.AutoOrient();
        if (options.MaxEdge is { } max) FitWithin(img, max);
        if (options.StripMetadata) NormalizeAndStrip(img);
        if (target is ImageFormat.Jpeg or ImageFormat.Bmp) FlattenAlpha(img);
        img.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
    }

    static byte[] Encode(MagickImage img, ImageFormat target, int quality)
    {
        using var clone = (MagickImage)img.Clone();
        if (target == ImageFormat.Jpeg) FlattenAlpha(clone);
        if (target == ImageFormat.Png)
        {
            clone.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            clone.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
        }
        else if (quality > 0) clone.Quality = (uint)quality;
        if (target == ImageFormat.Jpeg) clone.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", quality >= 90 ? "1x1" : "2x2");
        if (target == ImageFormat.WebP) clone.Settings.SetDefine(MagickFormat.WebP, "method", "6");
        return clone.ToByteArray(ToMagick(target));
    }

    static void Quantize(MagickImage img, uint colors) =>
        img.Quantize(new QuantizeSettings { Colors = colors, DitherMethod = DitherMethod.FloydSteinberg });

    static void FitWithin(IMagickImage<byte> img, uint maxEdge)
    {
        if (img.Width <= maxEdge && img.Height <= maxEdge) return;
        img.Resize(new MagickGeometry(maxEdge, maxEdge)); // keeps aspect ratio
    }

    static void FlattenAlpha(IMagickImage<byte> img)
    {
        if (!img.HasAlpha) return;
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaAction.Remove);
    }

    /// <summary>Bakes wide-gamut colors into sRGB before the profile is dropped, then removes all metadata.</summary>
    static void NormalizeAndStrip(IMagickImage<byte> img)
    {
        var icc = img.GetColorProfile();
        if (icc is not null && !(icc.Description ?? "").Contains("sRGB", StringComparison.OrdinalIgnoreCase))
            img.TransformColorSpace(ColorProfiles.SRGB);
        img.Strip();
    }

    static MagickFormat ToMagick(ImageFormat f) => f switch
    {
        ImageFormat.Jpeg => MagickFormat.Jpeg,
        ImageFormat.Png => MagickFormat.Png,
        ImageFormat.WebP => MagickFormat.WebP,
        ImageFormat.Avif => MagickFormat.Avif,
        ImageFormat.Heic => MagickFormat.Heic,
        ImageFormat.Tiff => MagickFormat.Tiff,
        ImageFormat.Bmp => MagickFormat.Bmp,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };
}
