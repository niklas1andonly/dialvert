using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Dialvert.Core.Settings;

namespace Dialvert.App.Views;

/// <summary>Settings page inside the main window. Edits a copy; nothing changes until "Save".</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Fills the controls from the live settings (also discards unsaved edits).</summary>
    public void Load()
    {
        var app = App.Instance;
        var s = app.Settings;
        TriggerKey.Binding = s.WheelTrigger;
        ToggleKey.Binding = s.ToggleDropZone;
        ConvertKey.Binding = s.ModeKeys.GetValueOrDefault(WheelMode.Convert) ?? new(0x31);
        CompressKey.Binding = s.ModeKeys.GetValueOrDefault(WheelMode.Compress) ?? new(0x32);
        ToolsKey.Binding = s.ModeKeys.GetValueOrDefault(WheelMode.Tools) ?? new(0x33);
        (s.DefaultMode switch { WheelMode.Compress => StartCompress, WheelMode.Tools => StartTools, _ => StartConvert }).IsChecked = true;

        OutputFolder.Text = s.OutputFolder;
        ImageTarget.Text = s.ImageTargetKb.ToString();
        VideoTarget.Text = s.VideoTargetMb.ToString();
        Quality.Text = s.ImageQuality.ToString();
        StripMeta.IsChecked = s.StripMetadataOnCompress;
        Hardware.IsChecked = s.UseHardwareEncoders;
        FfmpegPath.Text = s.FfmpegPath;
        ShowDrop.IsChecked = s.ShowDropZoneOnStart;
        Autostart.IsChecked = s.LaunchAtLogin;
        Error.Text = "";

        RefreshEngines();
        Paths.Text = $"{app.Store.FilePath}\n{app.History.FilePath}";
    }

    public void RefreshEngines()
    {
        var app = App.Instance;
        EngineInfo.Text = string.Join("\n", new[]
        {
            app.Ffmpeg is { } ff ? $"FFmpeg       {(app.H264Encoder is { } e ? $"H.264 via {e}" : "ready")}  ·  {ff.FfmpegPath}" : "FFmpeg       not found – video/audio disabled",
            app.Documents.Pandoc is not null ? "Pandoc       ready" : "Pandoc       missing",
            app.Documents.Office is not null ? "LibreOffice  ready" : "LibreOffice  not installed – Office ↔ PDF disabled",
        });
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ImageTarget.Text, out var imgKb) || imgKb < 5) { Error.Text = "Image target size: a number ≥ 5 KB"; return; }
        if (!int.TryParse(VideoTarget.Text, out var vidMb) || vidMb < 1) { Error.Text = "Video target size: a number ≥ 1 MB"; return; }
        if (!int.TryParse(Quality.Text, out var q) || q is < 1 or > 100) { Error.Text = "Quality: 1–100"; return; }
        var modeKeys = new[] { ConvertKey.Binding, CompressKey.Binding, ToolsKey.Binding };
        if (modeKeys.Distinct().Count() != modeKeys.Length) { Error.Text = "The three mode keys must be different."; return; }
        if (TriggerKey.Binding.VirtualKey == 0) { Error.Text = "The right-click needs a wheel key."; return; }

        // Clone via JSON so an abandoned edit never mutates the live settings.
        var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(App.Instance.Settings))!;
        s.WheelTrigger = TriggerKey.Binding;
        s.ToggleDropZone = ToggleKey.Binding;
        s.ModeKeys = new()
        {
            [WheelMode.Convert] = ConvertKey.Binding,
            [WheelMode.Compress] = CompressKey.Binding,
            [WheelMode.Tools] = ToolsKey.Binding,
        };
        s.DefaultMode = StartCompress.IsChecked == true ? WheelMode.Compress : StartTools.IsChecked == true ? WheelMode.Tools : WheelMode.Convert;
        s.OutputFolder = OutputFolder.Text.Trim();
        s.ImageTargetKb = imgKb;
        s.VideoTargetMb = vidMb;
        s.ImageQuality = q;
        s.StripMetadataOnCompress = StripMeta.IsChecked == true;
        s.UseHardwareEncoders = Hardware.IsChecked == true;
        s.FfmpegPath = FfmpegPath.Text.Trim();
        s.ShowDropZoneOnStart = ShowDrop.IsChecked == true;
        s.LaunchAtLogin = Autostart.IsChecked == true;

        Error.Text = "";
        App.Instance.SaveSettings(s);
        App.Instance.Notify("Settings saved");
    }

    void Revert_Click(object sender, RoutedEventArgs e) => Load();

    void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose output folder" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) OutputFolder.Text = dlg.FolderName;
    }

    void ClearOutput_Click(object sender, RoutedEventArgs e) => OutputFolder.Text = "";

    void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe", Title = "Choose ffmpeg.exe" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) FfmpegPath.Text = dlg.FileName;
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(App.Instance.Store.FilePath)!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
