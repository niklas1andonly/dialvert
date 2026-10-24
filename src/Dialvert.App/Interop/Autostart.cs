using Microsoft.Win32;

namespace Dialvert.App.Interop;

/// <summary>"Start with Windows" via the per-user Run key; starts silently in the tray (--tray).</summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Dialvert";

    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(Name) is not null;

    /// <summary>Writes or removes the entry; also repairs it when the app was moved to another folder.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                if (key.GetValue(Name) as string != Command) key.SetValue(Name, Command);
            }
            else if (key.GetValue(Name) is not null) key.DeleteValue(Name);
        }
        catch (Exception ex) { Log.Error("autostart", ex); }
    }
}
