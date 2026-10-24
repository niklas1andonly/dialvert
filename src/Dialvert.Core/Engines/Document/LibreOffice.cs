using Microsoft.Win32;

namespace Dialvert.Core.Engines.Document;

/// <summary>
/// Drives an installed LibreOffice in headless mode. Never bundled (size, licensing) – we only detect it.
/// Uses its own user profile so conversions work while the user has LibreOffice open, and runs one
/// conversion at a time because a profile cannot be shared by two soffice processes.
/// </summary>
public sealed class LibreOffice
{
    public const string DownloadUrl = "https://www.libreoffice.org/download/download/";

    static readonly SemaphoreSlim OneAtATime = new(1);

    public string ExePath { get; }
    readonly string _profileDir;

    public LibreOffice(string exePath, string? profileDir = null)
    {
        ExePath = exePath;
        _profileDir = profileDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dialvert", "libreoffice-profile");
    }

    /// <summary>Override → registry (UNO install path) → Program Files → PATH.</summary>
    public static LibreOffice? Locate(string? overridePath = null)
    {
        foreach (var candidate in Candidates(overridePath))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var exe = Directory.Exists(candidate) ? Path.Combine(candidate, "soffice.exe") : candidate;
            if (File.Exists(exe)) return new LibreOffice(PreferConsole(exe));
        }
        return null;
    }

    static IEnumerable<string?> Candidates(string? overridePath)
    {
        yield return overridePath;
        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                string? path = null;
                try { path = hive.OpenSubKey(@"SOFTWARE\LibreOffice\UNO\InstallPath")?.GetValue(null) as string; }
                catch (System.Security.SecurityException) { }
                yield return path;
            }
        }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            yield return Path.Combine(Environment.GetFolderPath(root), "LibreOffice", "program");
        yield return EngineLocator.Find("soffice.exe", "libreoffice");
    }

    /// <summary>soffice.com is the console launcher: it waits for the conversion and passes output through.</summary>
    static string PreferConsole(string exe)
    {
        var com = Path.ChangeExtension(exe, ".com");
        return File.Exists(com) ? com : exe;
    }

    /// <summary>
    /// Converts <paramref name="input"/> with <c>--convert-to</c> into <paramref name="outputPath"/>.
    /// <paramref name="convertTo"/> is "ext" or "ext:FilterName"; <paramref name="inFilter"/> forces an import filter.
    /// </summary>
    public async Task ConvertAsync(string input, string convertTo, string outputPath, string? inFilter, CancellationToken ct)
    {
        var outDir = Path.Combine(Path.GetDirectoryName(outputPath)!, $".dialvert-lo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        await OneAtATime.WaitAsync(ct);
        try
        {
            List<string> args =
            [
                $"-env:UserInstallation={new Uri(_profileDir + Path.DirectorySeparatorChar).AbsoluteUri}",
                "--headless", "--invisible", "--norestore", "--nolockcheck", "--nodefault", "--nologo",
            ];
            if (inFilter is not null) args.Add($"--infilter={inFilter}");
            args.AddRange(["--convert-to", convertTo, "--outdir", outDir, Path.GetFullPath(input)]);

            var (code, stdout, stderr) = await ToolProcess.RunAsync(ExePath, args, outDir, TimeSpan.FromMinutes(10), ct);
            var produced = Directory.GetFiles(outDir).FirstOrDefault();
            if (produced is null)
            {
                var why = ToolProcess.LastLines(stderr + "\n" + stdout, 2);
                throw new InvalidOperationException(
                    $"LibreOffice could not convert the file{(why.Length > 0 ? $": {why}" : $" (code {code}).")}");
            }
            File.Move(produced, outputPath);
        }
        finally
        {
            OneAtATime.Release();
            try { Directory.Delete(outDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
