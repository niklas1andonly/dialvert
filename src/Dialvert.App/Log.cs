using System.IO;

namespace Dialvert.App;

/// <summary>Tiny append-only diagnostics log in %LOCALAPPDATA%\Dialvert\dialvert.log (rotated at 1 MB).</summary>
static class Log
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dialvert", "dialvert.log");
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* logging must never take the app down */ }
    }
}
