using ImageMagick;
using ImageMagick.Drawing;
using Dialvert.Core.Engines.Media;
using Xunit.Abstractions;

namespace Dialvert.Core.Tests;

/// <summary>Creates synthetic test media once per test run in a temp folder.</summary>
public sealed class Fixtures : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "dialvert-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public string Photo { get; }
    public string AlphaPng { get; }
    public Ffmpeg? Ff { get; } = Ffmpeg.Locate();

    readonly Lazy<string> _video;
    readonly Lazy<string> _oddVideo;
    readonly Lazy<string> _wav;

    public Fixtures()
    {
        Directory.CreateDirectory(Dir);

        // A noisy, detailed photo-like image with EXIF + GPS so compression and stripping have work to do.
        Photo = Path.Combine(Dir, "foto.jpg");
        using (var img = new MagickImage("plasma:fractal", 2400, 1600))
        {
            img.AddNoise(NoiseType.Gaussian);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "DialvertTestCam");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            img.SetProfile(exif);
            img.Quality = 97;
            img.Write(Photo);
        }

        AlphaPng = Path.Combine(Dir, "logo.png");
        using (var img = new MagickImage(MagickColors.Transparent, 800, 600))
        {
            new Drawables().FillColor(MagickColors.OrangeRed).Ellipse(400, 300, 300, 200, 0, 360).Draw(img);
            img.Write(AlphaPng);
        }

        _video = new(() => MakeVideo("clip.mp4", "1280x720", 8));
        _oddVideo = new(() => MakeVideo("odd.mp4", "641x361", 3));
        _wav = new(() =>
        {
            var path = Path.Combine(Dir, "ton.wav");
            Run(["-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-ac", "2", "-c:a", "pcm_s16le", path]);
            return path;
        });
    }

    public string Video => _video.Value;
    public string OddVideo => _oddVideo.Value;
    public string Wav => _wav.Value;

    string MakeVideo(string name, string size, int seconds)
    {
        var path = Path.Combine(Dir, name);
        // High bitrate source so compression has something to remove.
        Run(["-f", "lavfi", "-i", $"testsrc2=size={size}:rate=30:duration={seconds}",
             "-f", "lavfi", "-i", $"sine=frequency=330:duration={seconds}",
             "-vf", "noise=alls=12:allf=t", "-c:v", "libopenh264", "-b:v", "8M", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-b:a", "192k", "-shortest", path]);
        return path;
    }

    void Run(string[] args)
    {
        if (Ff is null) throw new InvalidOperationException("ffmpeg not found (run scripts/fetch-engines.ps1)");
        Ff.RunAsync(args, 0, null, CancellationToken.None).GetAwaiter().GetResult();
    }

    public string Sub(string name)
    {
        var d = Path.Combine(Dir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

[CollectionDefinition("media")]
public sealed class MediaCollection : ICollectionFixture<Fixtures>;

static class Assertions
{
    public static long Size(string path) => new FileInfo(path).Length;

    public static void Report(this ITestOutputHelper output, string input, string result) =>
        output.WriteLine($"{Path.GetFileName(input)} ({Size(input) / 1024} KB) → {Path.GetFileName(result)} ({Size(result) / 1024} KB)");
}
