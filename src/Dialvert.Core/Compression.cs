namespace Dialvert.Core;

public enum CompressPreset { High, Balanced, Small }

public enum CompressMode { Preset, Percent, TargetSize }

/// <summary>
/// How hard to compress. <see cref="CompressMode.Percent"/> means "result should be
/// at most N % of the original file size".
/// </summary>
public sealed record CompressSpec(CompressMode Mode, CompressPreset Preset = CompressPreset.Balanced,
                                  int Percent = 50, long TargetBytes = 0)
{
    public static CompressSpec FromPreset(CompressPreset p) => new(CompressMode.Preset, p);
    public static CompressSpec FromPercent(int percent) => new(CompressMode.Percent, Percent: percent);
    public static CompressSpec FromTarget(long bytes) => new(CompressMode.TargetSize, TargetBytes: bytes);

    /// <summary>Byte budget for size-driven modes, or null for presets.</summary>
    public long? BudgetFor(long originalBytes) => Mode switch
    {
        CompressMode.Percent => Math.Max(1, originalBytes * Math.Clamp(Percent, 1, 100) / 100),
        CompressMode.TargetSize => TargetBytes,
        _ => null,
    };

    public static string Describe(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):0.#} MB"
        : $"{bytes / 1024d:0} KB";
}
