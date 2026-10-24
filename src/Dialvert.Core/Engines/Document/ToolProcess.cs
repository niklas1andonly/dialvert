using System.Diagnostics;
using System.Text;

namespace Dialvert.Core.Engines.Document;

/// <summary>Runs an external command-line tool to completion, killing it on cancel or timeout.</summary>
static class ToolProcess
{
    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string exe, IEnumerable<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
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
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(exe)}.");
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        using var reg = linked.Token.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        var outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (timer.IsCancellationRequested)
            throw new TimeoutException($"{Path.GetFileNameWithoutExtension(exe)} did not respond within {timeout.TotalMinutes:0} min.");
        return (proc.ExitCode, await outTask, await errTask);
    }

    public static string LastLines(string text, int n) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(n));
}
