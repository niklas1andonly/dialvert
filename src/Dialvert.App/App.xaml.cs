using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Dialvert.App.Interop;
using Dialvert.App.Views;
using Dialvert.Core;
using Dialvert.Core.Actions;
using Dialvert.Core.Engines.Document;
using Dialvert.Core.Engines.Image;
using Dialvert.Core.Engines.Media;
using Dialvert.Core.Jobs;
using Dialvert.Core.Settings;
using Forms = System.Windows.Forms;

namespace Dialvert.App;

/// <summary>Composition root: owns settings, engines, the job runner and all top-level windows.</summary>
public partial class App : Application
{
    public static App Instance => (App)Current;

    public SettingsStore Store { get; } = new();
    public HistoryStore History { get; } = new();
    public AppSettings Settings { get; private set; } = new();
    public JobRunner Runner { get; } = new();
    public ActionCatalog Catalog { get; private set; } = null!;
    public Ffmpeg? Ffmpeg { get; private set; }
    public DocumentEngine Documents { get; private set; } = null!;
    /// <summary>Name of the H.264 encoder FFmpeg will use, once probed.</summary>
    public string? H264Encoder { get; private set; }

    SingleInstance? _single;
    HotkeyManager? _hotkeys;
    MouseHook? _mouse;
    Forms.NotifyIcon? _tray;
    DropZoneWindow? _dropZone;
    MainWindow? _main;
    WheelWindow? _wheel;
    ToastWindow? _hint, _activity;
    bool _pointerHeld;

    readonly HashSet<Job> _running = [];
    readonly DispatcherTimer _activityTick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            _single.SendToFirst(e.Args.Length > 0 ? e.Args : ["--show"]);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("unhandled", ex.Exception);
            MessageBox.Show(ex.Exception.Message, "Dialvert", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        Log.Info($"start {string.Join(' ', e.Args)}");

        Settings = Store.Load();
#if !DEBUG
        // Dev builds would otherwise point the Run key at bin\Debug.
        Autostart.Apply(Settings.LaunchAtLogin);
#endif
        BuildEngines();

        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        _mouse = new MouseHook(Settings.WheelTrigger,
            (pt, root) => Dispatcher.BeginInvoke(() => OnPointerTrigger(pt, root)),
            () => Dispatcher.BeginInvoke(OnPointerRelease));
        CreateTray();

        _hint = new ToastWindow(nearCursor: true);
        _activity = new ToastWindow(nearCursor: false);
        _activityTick.Tick += (_, _) => RefreshActivity();
        _dropZone = new DropZoneWindow();
        if (Settings.ShowDropZoneOnStart) _dropZone.Show();

        Runner.Enqueued += job => Dispatcher.Invoke(() => OnJobEnqueued(job));
        Runner.Finished += job => Dispatcher.BeginInvoke(() => OnJobFinished(job));

        _single.Listen(args => Dispatcher.BeginInvoke(() => HandleArgs(args)));
        if (e.Args.Contains("--tray")) { /* started silently with Windows */ }
        else if (e.Args.Length > 0) HandleArgs(e.Args);
        else ShowMain();
    }

    // ---------------------------------------------------------------- engines

    public void BuildEngines()
    {
        var output = new OutputPaths(Settings.OutputFolder);
        Ffmpeg = Ffmpeg.Locate(Settings.FfmpegPath);
        var media = Ffmpeg is null ? null : new MediaEngine(Ffmpeg, output, Settings.UseHardwareEncoders);
        Documents = new DocumentEngine(output, Pandoc.Locate(Settings.PandocPath), LibreOffice.Locate(Settings.LibreOfficePath),
            new DocumentOptions(Settings.PdfRenderDpi, Settings.ImageQuality));
        Catalog = new ActionCatalog(new ImageEngine(output), media, Settings, Documents);

        H264Encoder = null;
        if (media is not null)
        {
            _ = media.H264EncoderAsync().ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                H264Encoder = t.IsCompletedSuccessfully ? t.Result.ToString() : null;
                _main?.RefreshStatus();
            }));
        }
        _main?.RefreshStatus();
    }

    public void SaveSettings(AppSettings updated)
    {
        Settings = updated;
        Store.Save(updated);
        Autostart.Apply(updated.LaunchAtLogin);
        if (_mouse is not null) _mouse.Trigger = updated.WheelTrigger;
        BuildEngines();
        RegisterHotkeys();
    }

    void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterAll();
        if (!_hotkeys.Register(Settings.ToggleDropZone, ToggleDropZone))
            Notify($"Shortcut {KeyNames.Describe(Settings.ToggleDropZone)} is already in use.");
    }

    // ---------------------------------------------------------------- windows

    public MainWindow MainView => _main ??= new MainWindow();

    public void ShowMain()
    {
        var w = MainView;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public void ShowSettings()
    {
        ShowMain();
        MainView.ShowPage(settings: true);
    }

    public void ToggleDropZone()
    {
        if (_dropZone is null) return;
        if (_dropZone.IsVisible) _dropZone.Hide(); else _dropZone.Show();
    }

    /// <summary>Opens the action wheel centered on the cursor (or on <paramref name="anchor"/>).</summary>
    internal void ShowWheel(IReadOnlyList<string> files, WheelOpen open, Native.POINT? anchor = null)
    {
        if (open == WheelOpen.Drag && _wheel is not null) return; // already showing for this drag
        var expanded = ExpandFolders(files);
        Log.Info($"wheel {open} files={expanded.Count}");
        if (expanded.Count == 0) return;
        try
        {
            _wheel?.Close();
            _wheel = new WheelWindow(expanded, open);
            _wheel.Closed += (s, _) => { if (ReferenceEquals(_wheel, s)) _wheel = null; };
            _wheel.ShowAt(anchor);
        }
        catch (Exception ex)
        {
            // Exceptions inside an OLE drag callback are swallowed by COM; make them visible.
            Log.Error("ShowWheel", ex);
            _wheel = null;
        }
    }

    /// <summary>Trigger key + right-click landed on a file view; the click itself was swallowed.</summary>
    async void OnPointerTrigger(Native.POINT pt, IntPtr root)
    {
        _pointerHeld = true;
        // Releasing a lone Alt would open Explorer's menu bar, a lone Win the Start menu.
        if (Settings.WheelTrigger.VirtualKey is Native.VK_MENU or Native.VK_LWIN or Native.VK_RWIN) Native.SendDummyKey();
        var files = await ShellSelection.GetAsync(pt, root);
        Log.Info($"pointer trigger files={files.Count}");
        if (files.Count == 0) { Notify("No file under the cursor"); return; }
        ShowWheel(files, WheelOpen.Pointer, pt);
        // Released before the wheel was up: it simply stays open for clicking.
    }

    void OnPointerRelease()
    {
        if (!_pointerHeld) return;
        _pointerHeld = false;
        _wheel?.PointerReleased();
    }

    public void Execute(WheelAction action, IReadOnlyList<string> files)
    {
        if (action.BuildJobs is not null)
        {
            var jobs = action.BuildJobs(files).ToList();
            if (jobs.Count == 0)
            {
                Notify("No matching files for this action.");
                return;
            }
            foreach (var job in jobs) Runner.Enqueue(job);
            return;
        }
        switch (action.Immediate)
        {
            case ImmediateAction.CopyPath:
                Clipboard.SetText(string.Join(Environment.NewLine, files));
                Notify(files.Count == 1 ? "Path copied" : $"{files.Count} paths copied");
                break;
            case ImmediateAction.RevealInExplorer:
                Reveal(files[0]);
                break;
            case ImmediateAction.OpenTerminal:
                OpenTerminal(Directory.Exists(files[0]) ? files[0] : Path.GetDirectoryName(files[0])!);
                break;
        }
    }

    public static void Reveal(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    static void OpenTerminal(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(dir);
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = dir, UseShellExecute = true });
        }
    }

    static List<string> ExpandFolders(IReadOnlyList<string> paths)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) result.Add(p);
            else if (Directory.Exists(p))
                result.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Take(1000 - result.Count));
            if (result.Count >= 1000) break;
        }
        return result;
    }

    void HandleArgs(string[] args)
    {
#if DEBUG
        // Runs the right-click file lookup at the cursor 2 s later, without a real click (for testing).
        if (args is ["--probe-selection"]) { _ = ProbeSelectionAsync(); return; }
#endif
        var files = args.Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
        if (files.Count > 0) ShowWheel(files, WheelOpen.Click);
        else ShowMain();
    }

#if DEBUG
    async Task ProbeSelectionAsync()
    {
        await Task.Delay(2000);
        Native.GetCursorPos(out var pt);
        var root = MouseHook.ShellViewAt(pt);
        var files = root == IntPtr.Zero ? [] : await ShellSelection.GetAsync(pt, root);
        Log.Info($"probe at {pt.X},{pt.Y} root={root} files={string.Join(" | ", files)}");
    }
#endif

    /// <summary>Short notice next to the cursor.</summary>
    public void Notify(string text) => _hint?.Show(text, hideAfter: TimeSpan.FromSeconds(2.2));

    // --------------------------------------------------------------- activity

    void OnJobEnqueued(Job job)
    {
        MainView.Add(job);
        _running.Add(job);
        _activityTick.Start();
        RefreshActivity();
    }

    void OnJobFinished(Job job)
    {
        _running.Remove(job);
        MainView.Complete(job, HistoryEntry.From(job));
        if (job.State == JobState.Failed)
        {
            _activity?.Show($"Error · {job.Title}", job.Message, hideAfter: TimeSpan.FromSeconds(6), error: true);
            if (_running.Count == 0) _activityTick.Stop();
            return;
        }
        if (_running.Count > 0) { RefreshActivity(); return; }
        _activityTick.Stop();
        if (job.State == JobState.Done) _activity?.Show("Done", job.Message, hideAfter: TimeSpan.FromSeconds(4));
        else _activity?.Show("Canceled", job.Title, hideAfter: TimeSpan.FromSeconds(2.5));
    }

    /// <summary>Progress toast while anything runs; the main window need not be open.</summary>
    void RefreshActivity()
    {
        if (_running.Count == 0) return;
        var runningNow = _running.Where(j => j.State == JobState.Running).ToList();
        var progress = runningNow.Count == 0 ? 0 : runningNow.Average(j => j.Progress);
        var headline = _running.Count == 1 ? "1 task running" : $"{_running.Count} tasks running";
        _activity?.Show(headline, runningNow.FirstOrDefault()?.Title ?? "queued", progress);
    }

    // ------------------------------------------------------------------- tray

    void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/dialvert.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Dialvert",
            Visible = true,
            ContextMenuStrip = TrayMenu.Create(),
        };
        _tray.ContextMenuStrip.Items.Add("Open Dialvert", null, (_, _) => ShowMain());
        _tray.ContextMenuStrip.Items.Add("Settings", null, (_, _) => ShowSettings());
        _tray.ContextMenuStrip.Items.Add("Drop zone on/off", null, (_, _) => ToggleDropZone());
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Quit());
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMain(); };
    }

    public void Quit()
    {
        if (_dropZone is not null)
        {
            Settings.DropZoneLeft = _dropZone.Left;
            Settings.DropZoneTop = _dropZone.Top;
            Store.Save(Settings);
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _mouse?.Dispose();
        _hotkeys?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
