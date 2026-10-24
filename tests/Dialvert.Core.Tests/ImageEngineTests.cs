using ImageMagick;
using Dialvert.Core.Engines.Image;
using Dialvert.Core.Formats;
using Xunit.Abstractions;

namespace Dialvert.Core.Tests;

[Collection("media")]
public sealed class ImageEngineTests(Fixtures fx, ITestOutputHelper output)
{
    [Fact]
    public void Reports_capabilities()
    {
        foreach (var f in Enum.GetValues<ImageFormat>())
            output.WriteLine($"{f}: write={ImageEngine.CanWrite(f)}");
        var heic = MagickNET.SupportedFormats.FirstOrDefault(x => x.Format == MagickFormat.Heic);
        output.WriteLine($"HEIC read={heic?.SupportsReading} write={heic?.SupportsWriting}");
        Assert.True(ImageEngine.CanWrite(ImageFormat.WebP));
        Assert.True(ImageEngine.CanWrite(ImageFormat.Avif));
    }

    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.WebP)]
    [InlineData(ImageFormat.Avif)]
    [InlineData(ImageFormat.Tiff)]
    [InlineData(ImageFormat.Bmp)]
    [InlineData(ImageFormat.Heic)]
    public void Converts_jpeg_to(ImageFormat target)
    {
        if (!ImageEngine.CanWrite(target)) { output.WriteLine($"{target} not writable in this build – skipped"); return; }
        var engine = new ImageEngine(new OutputPaths(fx.Sub("convert")));
        var result = engine.Convert(fx.Photo, target);
        output.Report(fx.Photo, result);

        Assert.Equal(FormatInfo.Extension(target), Path.GetExtension(result));
        using var img = new MagickImage(result);
        Assert.Equal(2400u, img.Width);
        Assert.Equal(1600u, img.Height);
    }

    [Fact]
    public void Png_with_alpha_to_jpeg_gets_white_background()
    {
        var engine = new ImageEngine(new OutputPaths(fx.Sub("alpha")));
        var result = engine.Convert(fx.AlphaPng, ImageFormat.Jpeg);
        using var img = new MagickImage(result);
        var corner = img.GetPixels().GetPixel(0, 0).ToColor()!;
        Assert.True(corner.R > 250 && corner.G > 250 && corner.B > 250, $"corner was {corner}");
    }

    [Fact]
    public void Never_overwrites_existing_output()
    {
        var engine = new ImageEngine(new OutputPaths(fx.Sub("unique")));
        var a = engine.Convert(fx.Photo, ImageFormat.Png);
        var b = engine.Convert(fx.Photo, ImageFormat.Png);
        Assert.NotEqual(a, b);
        Assert.EndsWith("foto (1).png", b);
    }

    [Theory]
    [InlineData(CompressPreset.High)]
    [InlineData(CompressPreset.Balanced)]
    [InlineData(CompressPreset.Small)]
    public void Preset_compression_shrinks_jpeg(CompressPreset preset)
    {
        var engine = new ImageEngine(new OutputPaths(fx.Sub("cmp-" + preset)));
        var result = engine.Compress(fx.Photo, CompressSpec.FromPreset(preset));
        output.Report(fx.Photo, result);
        Assert.True(Assertions.Size(result) < Assertions.Size(fx.Photo));
        Assert.Contains("(compressed)", result);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(60)]
    public void Target_size_is_respected(int kb)
    {
        var engine = new ImageEngine(new OutputPaths(fx.Sub("target-" + kb)));
        var result = engine.Compress(fx.Photo, CompressSpec.FromTarget(kb * 1024L));
        output.Report(fx.Photo, result);
        Assert.True(Assertions.Size(result) <= kb * 1024L, $"{Assertions.Size(result)} > {kb * 1024}");
    }

    [Fact]
    public void Percent_mode_hits_fraction_of_original()
    {
        var engine = new ImageEngine(new OutputPaths(fx.Sub("percent")));
        var result = engine.Compress(fx.Photo, CompressSpec.FromPercent(25));
        output.Report(fx.Photo, result);
        Assert.True(Assertions.Size(result) <= Assertions.Size(fx.Photo) / 4);
    }

    [Fact]
    public void Png_compression_quantizes()
    {
        var engine = new ImageEngine(new OutputPaths(fx.Sub("png")));
        var big = engine.Convert(fx.Photo, ImageFormat.Png);
        var result = engine.Compress(big, CompressSpec.FromPreset(CompressPreset.Balanced));
        output.Report(big, result);
        Assert.Equal(".png", Path.GetExtension(result));
        Assert.True(Assertions.Size(result) < Assertions.Size(big));
    }

    [Fact]
    public void Strip_metadata_removes_exif()
    {
        using (var before = new MagickImage(fx.Photo)) Assert.NotNull(before.GetExifProfile());
        var engine = new ImageEngine(new OutputPaths(fx.Sub("strip")));
        var result = engine.StripMetadata(fx.Photo);
        using var after = new MagickImage(result);
        Assert.Null(after.GetExifProfile());
    }

    [Fact]
    public void Resize_to_max_edge()
    {
        var engine = new ImageEngine(new OutputPaths(fx.Sub("resize")));
        var result = engine.Resize(fx.Photo, new ImageOptions(MaxEdge: 1000));
        using var img = new MagickImage(result);
        Assert.Equal(1000u, img.Width);
        Assert.Equal(667u, img.Height);
    }

    [Fact]
    public void Images_to_pdf()
    {
        var engine = new ImageEngine(new OutputPaths(fx.Sub("pdf")));
        var result = engine.ImagesToPdf([fx.Photo, fx.AlphaPng]);
        output.WriteLine($"{result} {Assertions.Size(result) / 1024} KB");
        using var fs = File.OpenRead(result);
        var head = new byte[4];
        fs.ReadExactly(head);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(head));
    }
}
