namespace Dialvert.Core;

/// <summary>
/// Finds external engine executables. Order: explicit override → engines\ folder next to the
/// app (or in a parent folder, for running from the source tree) → PATH.
/// </summary>
public static class EngineLocator
{
    public static string? Find(string exeName, string engineFolder, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var candidate = Directory.Exists(overridePath) ? Path.Combine(overridePath, exeName) : overridePath;
            if (File.Exists(candidate)) return candidate;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var sub in new[] { Path.Combine("engines", engineFolder, "bin"), Path.Combine("engines", engineFolder) })
            {
                var candidate = Path.Combine(dir.FullName, sub, exeName);
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
