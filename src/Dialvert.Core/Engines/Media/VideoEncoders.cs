namespace Dialvert.Core.Engines.Media;

/// <summary>
/// Picks the best working H.264 encoder once per run and knows how to drive each one,
/// either by constant quality or by target bitrate.
/// </summary>
public sealed class VideoEncoders
{
    // Best first. libx264 only exists in GPL builds; the hardware ones need a matching GPU + driver.
    static readonly string[] H264Preference = ["libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264"];
    static readonly HashSet<string> Hardware = ["h264_nvenc", "h264_qsv", "h264_amf"];

    readonly Ffmpeg _ff;
    readonly bool _allowHardware;
    Task<string>? _h264;

    public VideoEncoders(Ffmpeg ff, bool allowHardware)
    {
        _ff = ff;
        _allowHardware = allowHardware;
    }

    public Task<string> H264Async() => _h264 ??= PickH264Async();

    async Task<string> PickH264Async()
    {
        var available = await _ff.ListEncodersAsync();
        foreach (var name in H264Preference)
        {
            if (!available.Contains(name)) continue;
            if (Hardware.Contains(name) && !_allowHardware) continue;
            // Software encoders in the build always work; anything else gets a quick trial encode.
            if (name == "libx264" || await _ff.EncoderWorksAsync(name)) return name;
        }
        throw new InvalidOperationException("No working H.264 encoder found in this FFmpeg build.");
    }

    /// <summary>Encoder arguments for a quality level (0 = best … 2 = smallest).</summary>
    public static IEnumerable<string> QualityArgs(string encoder, int level, int width, int height, double fps)
    {
        level = Math.Clamp(level, 0, 2);
        return encoder switch
        {
            "libx264" => ["-c:v", "libx264", "-preset", "medium", "-crf", new[] { "20", "24", "28" }[level]],
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr",
                             "-cq", new[] { "22", "27", "32" }[level], "-b:v", "0"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-global_quality", new[] { "22", "26", "30" }[level]],
            "h264_amf" => ["-c:v", "h264_amf", "-rc", "cqp",
                           "-qp_i", new[] { "20", "24", "28" }[level], "-qp_p", new[] { "22", "26", "30" }[level]],
            // MediaFoundation and OpenH264 have no usable constant-quality mode: derive a bitrate.
            _ => BitrateArgs(encoder, EstimateKbps(width, height, fps, new[] { 0.12, 0.08, 0.05 }[level])),
        };
    }

    public static IEnumerable<string> BitrateArgs(string encoder, int kbps)
    {
        var b = $"{kbps}k";
        var max = $"{(int)(kbps * 1.5)}k";
        var buf = $"{kbps * 2}k";
        return encoder switch
        {
            "libx264" => ["-c:v", "libx264", "-preset", "medium", "-b:v", b, "-maxrate", max, "-bufsize", buf],
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres",
                             "-b:v", b, "-maxrate", max, "-bufsize", buf],
            "h264_qsv" => ["-c:v", "h264_qsv", "-b:v", b, "-maxrate", max, "-bufsize", buf],
            "h264_amf" => ["-c:v", "h264_amf", "-rc", "vbr_peak", "-b:v", b, "-maxrate", max, "-bufsize", buf],
            "h264_mf" => ["-c:v", "h264_mf", "-rate_control", "pc_vbr", "-b:v", b],
            _ => ["-c:v", encoder, "-b:v", b, "-maxrate", max, "-bufsize", buf],
        };
    }

    /// <summary>Bitrate from bits-per-pixel, clamped to sane bounds.</summary>
    public static int EstimateKbps(int width, int height, double fps, double bitsPerPixel)
    {
        if (width <= 0 || height <= 0) (width, height) = (1920, 1080);
        if (fps <= 0 || fps > 240) fps = 30;
        var kbps = width * (double)height * fps * bitsPerPixel / 1000;
        return (int)Math.Clamp(kbps, 300, 40000);
    }
}
