namespace Dialvert.Core.Engines.Document;

/// <summary>Thin process wrapper around an unmodified pandoc.exe (GPL, kept a separate program on purpose).</summary>
public sealed class Pandoc
{
    public string ExePath { get; }

    public Pandoc(string exePath) => ExePath = exePath;

    public static Pandoc? Locate(string? overridePath = null)
    {
        var exe = EngineLocator.Find("pandoc.exe", "pandoc", overridePath);
        return exe is null ? null : new Pandoc(exe);
    }

    public async Task RunAsync(IReadOnlyList<string> args, string? workingDirectory, CancellationToken ct)
    {
        var (code, _, stderr) = await ToolProcess.RunAsync(ExePath, args, workingDirectory, TimeSpan.FromMinutes(5), ct);
        if (code != 0) throw new InvalidOperationException($"Pandoc error: {ToolProcess.LastLines(stderr, 3)}");
    }

    public async Task<string> VersionAsync(CancellationToken ct = default)
    {
        var (_, stdout, _) = await ToolProcess.RunAsync(ExePath, ["--version"], null, TimeSpan.FromSeconds(20), ct);
        return stdout.Split('\n')[0].Trim();
    }
}
