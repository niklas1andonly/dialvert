namespace Dialvert.Core;

/// <summary>Decides where results go. Never overwrites an existing file.</summary>
public sealed class OutputPaths
{
    readonly string? _fixedFolder;

    public OutputPaths(string? fixedFolder = null) =>
        _fixedFolder = string.IsNullOrWhiteSpace(fixedFolder) ? null : fixedFolder;

    public string For(string input, string newExtension, string? suffix = null)
    {
        var dir = _fixedFolder ?? Path.GetDirectoryName(Path.GetFullPath(input))!;
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(input) + (suffix is null ? "" : $" ({suffix})");
        return Unique(Path.Combine(dir, stem + newExtension));
    }

    public string ForGroup(string firstInput, string name, string extension)
    {
        var dir = _fixedFolder ?? Path.GetDirectoryName(Path.GetFullPath(firstInput))!;
        Directory.CreateDirectory(dir);
        return Unique(Path.Combine(dir, name + extension));
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Hidden temp file next to the final path, same extension so encoders pick the right muxer.</summary>
    public static string TempFor(string finalPath) =>
        Path.Combine(Path.GetDirectoryName(finalPath)!, $".dialvert-{Guid.NewGuid():N}{Path.GetExtension(finalPath)}");

    /// <summary>Moves a finished temp file into place, picking a fresh name if the target appeared meanwhile.</summary>
    public static string Commit(string tempPath, string finalPath)
    {
        var target = Unique(finalPath);
        File.Move(tempPath, target);
        return target;
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
