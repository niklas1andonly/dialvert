using System.Globalization;
using Dialvert.Core.Formats;

namespace Dialvert.Core.Engines.Media;

/// <summary>Video and audio work via FFmpeg.</summary>
public sealed class MediaEngine
{
    readonly Ffmpeg _ff;
    readonly OutputPaths _out;
    readonly VideoEncoders _encoders;

    public MediaEngine(Ffmpeg ff, OutputPaths output, bool allowHardware = true)
    {
        _ff = ff;
        _out = output;
        _encoders = new VideoEncoders(ff, allowHardware);
    }

    public Task<string> H264EncoderAsync() => _encoders.H264Async();

    // ------------------------------------------------------------ video convert

    public async Task<string> ConvertVideoAsync(string input, VideoFormat target, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = await _ff.ProbeAsync(input, ct);
        if (info.Video is null) throw new InvalidOperationException("The file has no video track.");
        var final = _out.For(input, FormatInfo.Extension(target));

        if (target == VideoFormat.Gif)
        {
            return await EncodeAsync(input, final, info, progress, ct,
                ["-i", input, "-map", "0:v:0", "-an", "-sn",
                 "-vf", "fps=12,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                 "-loop", "0"]);
        }

        if (CanRemux(info, target))
        {
            try
            {
                List<string> remux = ["-i", input, "-map", "0:v:0", "-map", "0:a?", "-sn", "-dn", "-c", "copy", "-map_metadata", "0"];
                if (target is VideoFormat.Mp4 or VideoFormat.Mov) remux.AddRange(["-movflags", "+faststart"]);
                if (info.Video.Codec == "hevc" && target is VideoFormat.Mp4 or VideoFormat.Mov) remux.AddRange(["-tag:v", "hvc1"]);
                return await EncodeAsync(input, final, info, progress, ct, remux);
            }
            catch (InvalidOperationException) { /* odd stream layout: fall through to a real encode */ }
        }

        var args = new List<string> { "-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn", "-map_metadata", "0" };
        switch (target)
        {
            case VideoFormat.WebM:
                args.AddRange(["-vf", EvenScale(null), "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0",
                               "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p",
                               "-c:a", "libopus", "-b:a", "128k"]);
                break;
            case VideoFormat.Avi:
                args.AddRange(["-vf", EvenScale(null), "-c:v", "mpeg4", "-q:v", "4", "-tag:v", "XVID",
                               "-c:a", "libmp3lame", "-q:a", "3"]);
                break;
            default: // MP4 / MOV / MKV → H.264 + AAC
                var enc = await _encoders.H264Async();
                args.AddRange(["-vf", EvenScale(null), "-pix_fmt", "yuv420p"]);
                args.AddRange(VideoEncoders.QualityArgs(enc, 0, info.Video.Width, info.Video.Height, info.Video.Fps));
                args.AddRange(["-c:a", "aac", "-b:a", "160k"]);
                if (target is VideoFormat.Mp4 or VideoFormat.Mov) args.AddRange(["-movflags", "+faststart"]);
                break;
        }
        return await EncodeAsync(input, final, info, progress, ct, args);
    }

    static bool CanRemux(MediaInfo info, VideoFormat target)
    {
        var v = info.Video!.Codec;
        var a = info.Audio?.Codec;
        return target switch
        {
            VideoFormat.Mp4 => v is "h264" or "hevc" or "av1" && a is null or "aac" or "mp3",
            VideoFormat.Mov => v is "h264" or "hevc" or "prores" or "mjpeg" && a is null or "aac" or "alac" or "pcm_s16le",
            VideoFormat.Mkv => v is not ("gif" or "rawvideo") ,
            VideoFormat.WebM => v is "vp8" or "vp9" or "av1" && a is null or "opus" or "vorbis",
            _ => false,
        };
    }

    // ------------------------------------------------------------ audio convert

    /// <summary>Converts audio files, or pulls the audio track out of a video into the given format.</summary>
    public async Task<string> ConvertAudioAsync(string input, AudioFormat target, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = await _ff.ProbeAsync(input, ct);
        if (info.Audio is null) throw new InvalidOperationException("The file has no audio track.");
        var final = _out.For(input, FormatInfo.Extension(target));
        var args = new List<string> { "-i", input, "-map", "0:a:0", "-vn", "-sn", "-map_metadata", "0" };
        args.AddRange(AudioCodecArgs(target, null));
        return await EncodeAsync(input, final, info, progress, ct, args);
    }

    /// <summary>Copies the audio track without re-encoding into the container that fits its codec.</summary>
    public async Task<string> ExtractAudioAsync(string input, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = await _ff.ProbeAsync(input, ct);
        if (info.Audio is null) throw new InvalidOperationException("The file has no audio track.");
        var ext = info.Audio.Codec switch
        {
            "aac" or "alac" => ".m4a",
            "mp3" => ".mp3",
            "opus" => ".opus",
            "vorbis" => ".ogg",
            "flac" => ".flac",
            var c when c.StartsWith("pcm_", StringComparison.Ordinal) => ".wav",
            _ => null,
        };
        if (ext is null) return await ConvertAudioAsync(input, AudioFormat.M4a, progress, ct);
        var final = _out.For(input, ext);
        return await EncodeAsync(input, final, info, progress, ct,
            ["-i", input, "-map", "0:a:0", "-vn", "-sn", "-c:a", "copy", "-map_metadata", "0"]);
    }

    static IEnumerable<string> AudioCodecArgs(AudioFormat target, int? kbps) => target switch
    {
        AudioFormat.Mp3 => kbps is { } k ? ["-c:a", "libmp3lame", "-b:a", $"{k}k"] : ["-c:a", "libmp3lame", "-q:a", "2"],
        AudioFormat.M4a => ["-c:a", "aac", "-b:a", $"{kbps ?? 192}k", "-movflags", "+faststart"],
        AudioFormat.Wav => ["-c:a", "pcm_s16le"],
        AudioFormat.Flac => ["-c:a", "flac", "-compression_level", "8"],
        AudioFormat.Ogg => kbps is { } k ? ["-c:a", "libvorbis", "-b:a", $"{k}k"] : ["-c:a", "libvorbis", "-q:a", "5"],
        AudioFormat.Opus => ["-c:a", "libopus", "-b:a", $"{kbps ?? 128}k"],
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    // ---------------------------------------------------------------- compress

    public async Task<string> CompressAsync(string input, CompressSpec spec, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = await _ff.ProbeAsync(input, ct);
        if (info.DurationSeconds <= 0) throw new InvalidOperationException("Unknown duration – cannot compress.");
        return info.Video is not null
            ? await CompressVideoAsync(input, info, spec, progress, ct)
            : await CompressAudioAsync(input, info, spec, progress, ct);
    }

    async Task<string> CompressVideoAsync(string input, MediaInfo info, CompressSpec spec, IProgress<double>? progress, CancellationToken ct)
    {
        var v = info.Video!;
        var enc = await _encoders.H264Async();
        var budget = spec.BudgetFor(info.SizeBytes);
        var sourceVideoKbps = SourceVideoKbps(info);
        var fps = v.Fps is > 0 and < 240 ? v.Fps : 30;

        int videoKbps, audioKbps;
        int? maxEdge;
        if (budget is null)
        {
            (double factor, maxEdge, audioKbps) = spec.Preset switch
            {
                CompressPreset.High => (0.6, (int?)null, 160),
                CompressPreset.Balanced => (0.4, 1920, 128),
                _ => (0.25, 1280, 96),
            };
            var (w, h) = Fit(v.Width, v.Height, maxEdge);
            var ceiling = VideoEncoders.EstimateKbps(w, h, fps, spec.Preset switch
            {
                CompressPreset.High => 0.10, CompressPreset.Balanced => 0.07, _ => 0.05,
            });
            videoKbps = Math.Max(150, Math.Min((int)(sourceVideoKbps * factor), ceiling));
        }
        else
        {
            var totalKbps = budget.Value * 8.0 / info.DurationSeconds / 1000 * 0.95; // container overhead
            audioKbps = info.Audio is null ? 0 : totalKbps < 400 ? 48 : totalKbps < 1200 ? 96 : 128;
            videoKbps = (int)Math.Min(totalKbps - audioKbps, sourceVideoKbps * 0.95);
            if (videoKbps < 80)
                throw new InvalidOperationException(
                    $"Target size {CompressSpec.Describe(budget.Value)} is too small for {info.DurationSeconds:0} s of video.");
            maxEdge = PickEdgeForBitrate(videoKbps, fps, Math.Max(v.Width, v.Height));
        }

        var final = _out.For(input, ".mp4", "compressed");
        for (var attempt = 0; ; attempt++)
        {
            var args = new List<string> { "-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn", "-map_metadata", "0",
                                          "-vf", EvenScale(maxEdge), "-pix_fmt", "yuv420p" };
            args.AddRange(VideoEncoders.BitrateArgs(enc, videoKbps));
            if (info.Audio is not null) args.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k"]);
            args.AddRange(["-movflags", "+faststart"]);

            var result = await EncodeAsync(input, final, info, progress, ct, args);
            var size = new FileInfo(result).Length;
            if (budget is null || size <= budget.Value * 1.02 || attempt >= 1) return result;

            // Encoder overshot the budget: scale the video bitrate down by the miss and try once more.
            OutputPaths.TryDelete(result);
            var audioBytes = audioKbps * 1000.0 / 8 * info.DurationSeconds;
            var videoBytes = Math.Max(1, size - audioBytes);
            videoKbps = (int)(videoKbps * Math.Min(0.95, (budget.Value - audioBytes) / videoBytes * 0.97));
            if (videoKbps < 80) throw new InvalidOperationException("Could not reach the target size.");
            progress?.Report(0);
        }
    }

    async Task<string> CompressAudioAsync(string input, MediaInfo info, CompressSpec spec, IProgress<double>? progress, CancellationToken ct)
    {
        if (info.Audio is null) throw new InvalidOperationException("No audio or video track found.");
        var budget = spec.BudgetFor(info.SizeBytes);
        var kbps = budget is null
            ? spec.Preset switch { CompressPreset.High => 192, CompressPreset.Balanced => 128, _ => 96 }
            : (int)Math.Clamp(budget.Value * 8.0 / info.DurationSeconds / 1000 * 0.97, 32, 320);

        // Keep the lossy format the user had; lossless sources become MP3.
        var target = FormatInfo.AudioFormatOf(input) switch
        {
            AudioFormat.M4a => AudioFormat.M4a,
            AudioFormat.Ogg => AudioFormat.Ogg,
            AudioFormat.Opus => AudioFormat.Opus,
            _ => AudioFormat.Mp3,
        };
        if (target == AudioFormat.Opus) kbps = Math.Min(kbps, 256);
        var final = _out.For(input, FormatInfo.Extension(target), "compressed");
        var args = new List<string> { "-i", input, "-map", "0:a:0", "-vn", "-sn", "-map_metadata", "0" };
        args.AddRange(AudioCodecArgs(target, kbps));
        return await EncodeAsync(input, final, info, progress, ct, args);
    }

    static int SourceVideoKbps(MediaInfo info)
    {
        if (info.Video!.Bitrate > 0) return (int)(info.Video.Bitrate / 1000);
        var total = info.FormatBitrate > 0 ? info.FormatBitrate : (long)(info.SizeBytes * 8 / Math.Max(0.1, info.DurationSeconds));
        return (int)Math.Max(200, (total - (info.Audio?.Bitrate ?? 128_000)) / 1000);
    }

    /// <summary>Largest standard edge that still gets at least ~0.06 bits per pixel.</summary>
    static int? PickEdgeForBitrate(int videoKbps, double fps, int sourceEdge)
    {
        foreach (var edge in new[] { 3840, 2560, 1920, 1280, 960, 854, 640, 426 })
        {
            if (edge > sourceEdge) continue;
            var pixels = edge * edge * 9.0 / 16;
            if (videoKbps * 1000 / (pixels * fps) >= 0.06) return edge >= sourceEdge ? null : edge;
        }
        return 426;
    }

    static (int W, int H) Fit(int w, int h, int? maxEdge)
    {
        if (maxEdge is not { } m || Math.Max(w, h) <= m) return (w, h);
        var s = (double)m / Math.Max(w, h);
        return ((int)(w * s), (int)(h * s));
    }

    /// <summary>
    /// Scale filter evaluated after FFmpeg's auto-rotation: caps the longest edge and forces even
    /// dimensions (required by yuv420p encoders).
    /// </summary>
    static string EvenScale(int? maxEdge)
    {
        if (maxEdge is null) return "scale='trunc(iw/2)*2':'trunc(ih/2)*2'";
        var m = maxEdge.Value.ToString(CultureInfo.InvariantCulture);
        return $"scale='trunc(min(1,{m}/max(iw,ih))*iw/2)*2':'trunc(min(1,{m}/max(iw,ih))*ih/2)*2':flags=lanczos";
    }

    async Task<string> EncodeAsync(string input, string final, MediaInfo info, IProgress<double>? progress,
                                   CancellationToken ct, IReadOnlyList<string> args)
    {
        var temp = OutputPaths.TempFor(final);
        try
        {
            await _ff.RunAsync([.. args, temp], info.DurationSeconds, progress, ct);
            return OutputPaths.Commit(temp, final);
        }
        catch { OutputPaths.TryDelete(temp); throw; }
    }
}
