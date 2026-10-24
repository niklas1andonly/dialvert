using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dialvert.Core.Engines.Media;

public sealed record MediaInfo(double DurationSeconds, long SizeBytes, long FormatBitrate,
                               VideoStream? Video, AudioStream? Audio, string Container);

public sealed record VideoStream(string Codec, int Width, int Height, double Fps, long Bitrate, string? PixFmt);

public sealed record AudioStream(string Codec, long Bitrate, int Channels, int SampleRate);

/// <summary>Thin process wrapper around ffmpeg / ffprobe.</summary>
public sealed class Ffmpeg
{
    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    public Ffmpeg(string ffmpegPath, string? ffprobePath = null)
    {
        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath ?? Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
    }

    public static Ffmpeg? Locate(string? overridePath = null)
    {
        var exe = EngineLocator.Find("ffmpeg.exe", "ffmpeg", overridePath);
        return exe is null ? null : new Ffmpeg(exe);
    }

    public async Task<MediaInfo> ProbeAsync(string input, CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await RunCaptureAsync(FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", input], ct);
        if (code != 0) throw new InvalidOperationException($"ffprobe could not read the file: {LastLines(stderr, 3)}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var format = root.GetProperty("format");
        VideoStream? video = null;
        AudioStream? audio = null;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var type = Str(s, "codec_type");
            if (type == "video" && video is null && !IsAttachedPicture(s))
            {
                video = new VideoStream(Str(s, "codec_name") ?? "", Int(s, "width"), Int(s, "height"),
                    ParseRate(Str(s, "avg_frame_rate")) is > 0 and var r ? r : ParseRate(Str(s, "r_frame_rate")),
                    Long(s, "bit_rate"), Str(s, "pix_fmt"));
            }
            else if (type == "audio" && audio is null)
            {
                audio = new AudioStream(Str(s, "codec_name") ?? "", Long(s, "bit_rate"), Int(s, "channels"), Int(s, "sample_rate"));
            }
        }
        var duration = Dbl(format, "duration");
        return new MediaInfo(duration, new FileInfo(input).Length, Long(format, "bit_rate"), video, audio, Str(format, "format_name") ?? "");
    }

    /// <summary>Runs ffmpeg with -progress on stdout, reporting 0..1 against <paramref name="durationSeconds"/>.</summary>
    public async Task RunAsync(IReadOnlyList<string> args, double durationSeconds, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(CancellationToken.None)) is not null)
        {
            if (durationSeconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), out var us) && us > 0)
                progress?.Report(us / 1e6 / durationSeconds);
        }
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
        {
            string text;
            lock (stderr) text = stderr.ToString();
            throw new InvalidOperationException($"FFmpeg error: {LastLines(text, 4)}");
        }
    }

    /// <summary>Encoder names compiled into this ffmpeg build.</summary>
    public async Task<HashSet<string>> ListEncodersAsync(CancellationToken ct = default)
    {
        var (_, stdout, _) = await RunCaptureAsync(FfmpegPath, ["-hide_banner", "-encoders"], ct);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in stdout.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' or 'S') set.Add(parts[1]);
        }
        return set;
    }

    /// <summary>Encodes a few synthetic frames to check the encoder works on this machine (driver/GPU present).</summary>
    public async Task<bool> EncoderWorksAsync(string encoder, CancellationToken ct = default)
    {
        var (code, _, _) = await RunCaptureAsync(FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10",
             "-frames:v", "5", "-pix_fmt", "yuv420p", "-c:v", encoder, "-f", "null", "-"], ct);
        return code == 0;
    }

    static async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(exe)}.");
        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
        var outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (proc.ExitCode, await outTask, await errTask);
    }

    static string LastLines(string text, int n) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(n));

    static bool IsAttachedPicture(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : null;
    static int Int(JsonElement e, string name) => int.TryParse(Str(e, name), out var v) ? v : 0;
    static long Long(JsonElement e, string name) => long.TryParse(Str(e, name), out var v) ? v : 0;
    static double Dbl(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d > 0) return n / d;
        return double.TryParse(rate, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
