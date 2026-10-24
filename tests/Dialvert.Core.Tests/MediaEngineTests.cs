using System.Diagnostics;
using Dialvert.Core.Engines.Media;
using Dialvert.Core.Formats;
using Xunit.Abstractions;

namespace Dialvert.Core.Tests;

[Collection("media")]
public sealed class MediaEngineTests(Fixtures fx, ITestOutputHelper output)
{
    MediaEngine Engine(string sub, bool hw = true) => new(fx.Ff!, new OutputPaths(fx.Sub(sub)), hw);

    sealed class Recorder : IProgress<double>
    {
        public readonly List<double> Values = [];
        public void Report(double value) { lock (Values) Values.Add(value); }
    }

    [Fact]
    public async Task Picks_a_working_h264_encoder()
    {
        var hw = await Engine("enc").H264EncoderAsync();
        var sw = await Engine("enc-sw", hw: false).H264EncoderAsync();
        output.WriteLine($"with hardware: {hw}, software only: {sw}");
        Assert.DoesNotContain(sw, new[] { "h264_nvenc", "h264_qsv", "h264_amf" });
    }

    [Theory]
    [InlineData(VideoFormat.Mkv)]
    [InlineData(VideoFormat.Mov)]
    [InlineData(VideoFormat.WebM)]
    [InlineData(VideoFormat.Avi)]
    [InlineData(VideoFormat.Gif)]
    public async Task Converts_mp4_to(VideoFormat target)
    {
        var sw = Stopwatch.StartNew();
        var progress = new Recorder();
        var result = await Engine("v-" + target).ConvertVideoAsync(fx.Video, target, progress);
        output.Report(fx.Video, result);
        output.WriteLine($"{sw.ElapsedMilliseconds} ms, {progress.Values.Count} progress updates");

        var info = await fx.Ff!.ProbeAsync(result);
        Assert.NotNull(info.Video);
        Assert.InRange(info.DurationSeconds, 7.0, 9.0);
        if (target != VideoFormat.Gif) Assert.NotNull(info.Audio);
        Assert.Equal(FormatInfo.Extension(target), Path.GetExtension(result));
    }

    [Fact]
    public async Task Webm_to_mp4_reencodes_with_h264()
    {
        var webm = await Engine("w2m-src").ConvertVideoAsync(fx.Video, VideoFormat.WebM);
        var mp4 = await Engine("w2m").ConvertVideoAsync(webm, VideoFormat.Mp4);
        var info = await fx.Ff!.ProbeAsync(mp4);
        Assert.Equal("h264", info.Video!.Codec);
        Assert.Equal("aac", info.Audio!.Codec);
    }

    [Fact]
    public async Task Odd_dimensions_are_made_even()
    {
        var result = await Engine("odd").ConvertVideoAsync(fx.OddVideo, VideoFormat.Avi);
        var info = await fx.Ff!.ProbeAsync(result);
        Assert.Equal(640, info.Video!.Width);
        Assert.Equal(360, info.Video.Height);
    }

    [Theory]
    [InlineData(CompressPreset.High)]
    [InlineData(CompressPreset.Balanced)]
    [InlineData(CompressPreset.Small)]
    public async Task Preset_compression_shrinks_video(CompressPreset preset)
    {
        var result = await Engine("vc-" + preset).CompressAsync(fx.Video, CompressSpec.FromPreset(preset));
        output.Report(fx.Video, result);
        Assert.True(Assertions.Size(result) < Assertions.Size(fx.Video));
        Assert.EndsWith("(compressed).mp4", result);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    public async Task Target_size_is_respected(double mb)
    {
        var budget = (long)(mb * 1024 * 1024);
        var result = await Engine($"vt-{mb}").CompressAsync(fx.Video, CompressSpec.FromTarget(budget));
        output.Report(fx.Video, result);
        Assert.True(Assertions.Size(result) <= budget * 1.02, $"{Assertions.Size(result)} > {budget}");
    }

    [Fact]
    public async Task Too_small_target_fails_with_message()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Engine("vt-tiny").CompressAsync(fx.Video, CompressSpec.FromTarget(20 * 1024)));
        output.WriteLine(ex.Message);
        Assert.Contains("too small", ex.Message);
    }

    [Fact]
    public async Task Extracts_audio_losslessly()
    {
        var result = await Engine("extract").ExtractAudioAsync(fx.Video);
        Assert.Equal(".m4a", Path.GetExtension(result));
        var info = await fx.Ff!.ProbeAsync(result);
        Assert.Null(info.Video);
        Assert.Equal("aac", info.Audio!.Codec);
    }

    [Theory]
    [InlineData(AudioFormat.Mp3, "mp3")]
    [InlineData(AudioFormat.M4a, "aac")]
    [InlineData(AudioFormat.Flac, "flac")]
    [InlineData(AudioFormat.Ogg, "vorbis")]
    [InlineData(AudioFormat.Opus, "opus")]
    [InlineData(AudioFormat.Wav, "pcm_s16le")]
    public async Task Converts_audio(AudioFormat target, string codec)
    {
        var result = await Engine("a-" + target).ConvertAudioAsync(fx.Wav, target);
        output.Report(fx.Wav, result);
        var info = await fx.Ff!.ProbeAsync(result);
        Assert.Equal(codec, info.Audio!.Codec);
    }

    [Fact]
    public async Task Compresses_audio_to_target()
    {
        var result = await Engine("ac").CompressAsync(fx.Wav, CompressSpec.FromTarget(64 * 1024));
        output.Report(fx.Wav, result);
        Assert.Equal(".mp3", Path.GetExtension(result));
        Assert.True(Assertions.Size(result) <= 64 * 1024 * 1.05);
    }

    [Fact]
    public async Task Cancel_leaves_no_files_behind()
    {
        var dir = fx.Sub("cancel");
        var engine = new MediaEngine(fx.Ff!, new OutputPaths(dir));
        using var cts = new CancellationTokenSource();
        var progress = new Progress<double>(p => { if (p > 0.05) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ConvertVideoAsync(fx.Video, VideoFormat.WebM, progress, cts.Token));
        await Task.Delay(300);
        Assert.Empty(Directory.GetFiles(dir));
    }
}
