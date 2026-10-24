using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dialvert.Core.Settings;

[Flags]
public enum KeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 }

/// <summary>A key (Win32 virtual-key code) plus modifiers. Display text is resolved by the UI.</summary>
public sealed record HotKey(int VirtualKey, KeyModifiers Modifiers = KeyModifiers.None);

public enum WheelMode { Convert, Compress, Tools }

public sealed class AppSettings
{
    // Hotkeys ---------------------------------------------------------------
    /// <summary>Held while right-clicking a file in Explorer or on the desktop to open the wheel there.</summary>
    public HotKey WheelTrigger { get; set; } = new(0x11 /* Ctrl */, KeyModifiers.Control);

    /// <summary>Global hotkey that shows/hides the drop zone.</summary>
    public HotKey ToggleDropZone { get; set; } = new(0x55 /* U */, KeyModifiers.Control | KeyModifiers.Alt);

    /// <summary>Keys held while dragging over the wheel to switch its mode.</summary>
    public Dictionary<WheelMode, HotKey> ModeKeys { get; set; } = new()
    {
        [WheelMode.Convert] = new(0x31 /* 1 */),
        [WheelMode.Compress] = new(0x32 /* 2 */),
        [WheelMode.Tools] = new(0x33 /* 3 */),
    };

    public WheelMode DefaultMode { get; set; } = WheelMode.Convert;

    // Output ----------------------------------------------------------------
    /// <summary>Empty = next to the source file.</summary>
    public string OutputFolder { get; set; } = "";
    public bool StripMetadataOnCompress { get; set; } = true;
    public int ImageTargetKb { get; set; } = 500;
    public int VideoTargetMb { get; set; } = 25;
    public int ImageQuality { get; set; } = 88;

    // Engines ---------------------------------------------------------------
    public bool UseHardwareEncoders { get; set; } = true;
    public string FfmpegPath { get; set; } = "";
    /// <summary>Empty = engines\pandoc next to the app, then PATH.</summary>
    public string PandocPath { get; set; } = "";
    /// <summary>Empty = detect the installed LibreOffice (registry, Program Files, PATH).</summary>
    public string LibreOfficePath { get; set; } = "";
    /// <summary>Resolution for PDF → JPG/PNG.</summary>
    public int PdfRenderDpi { get; set; } = 150;

    // Window ----------------------------------------------------------------
    /// <summary>The floating drop zone is optional; the right-click wheel is the main way in.</summary>
    public bool ShowDropZoneOnStart { get; set; }
    /// <summary>Starts silently in the tray with Windows (HKCU Run key, kept in sync on every start).</summary>
    public bool LaunchAtLogin { get; set; } = true;
    public double? DropZoneLeft { get; set; }
    public double? DropZoneTop { get; set; }
}

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    public SettingsStore(string? filePath = null) =>
        FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dialvert", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (JsonException) { /* corrupt file: fall back to defaults, it gets rewritten on next save */ }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
