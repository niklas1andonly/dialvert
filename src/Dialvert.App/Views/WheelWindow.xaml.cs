using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Dialvert.App.Interop;
using Dialvert.Core.Actions;
using Dialvert.Core.Formats;
using Dialvert.Core.Settings;

namespace Dialvert.App.Views;

/// <summary>How the wheel was opened, which decides how it is operated.</summary>
public enum WheelOpen
{
    /// <summary>Files dragged onto the drop zone: the wheel is the drop target, release over a segment.</summary>
    Drag,
    /// <summary>Files from "Send to" / file picker: an activated window, segments are clicked.</summary>
    Click,
    /// <summary>Trigger key + right-click in Explorer: never takes focus; release the right button over a
    /// segment (flick gesture) or let go in the middle and click.</summary>
    Pointer,
}

/// <summary>
/// Radial action menu. Hit testing is pure geometry on the cursor position, which avoids the
/// DragEnter/DragLeave flicker WPF produces when moving between child elements during an OLE drag.
/// </summary>
public partial class WheelWindow : Window
{
    const double C = 240, OuterR = 206, InnerR = 84, HubRowHeight = 24;

    static readonly WheelMode[] Modes = [WheelMode.Convert, WheelMode.Compress, WheelMode.Tools];
    static readonly Dictionary<WheelMode, string> ModeNames = new()
    {
        [WheelMode.Convert] = "Convert",
        [WheelMode.Compress] = "Compress",
        [WheelMode.Tools] = "Tools",
    };

    readonly IReadOnlyList<string> _files;
    readonly WheelOpen _open;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(30) };
    readonly Dictionary<WheelMode, bool> _keyWasDown = new();
    readonly List<Segment> _segments = [];
    readonly List<(Border Row, TextBlock Name, TextBlock Key)> _modeRows = [];
    readonly string _defaultHint;
    readonly Brush _segmentFill, _signal, _onSignal, _text, _dim, _faint;

    sealed record Segment(Path Shape, Line Tick, TextBlock[] Texts, WheelAction Action);

    WheelMode _mode;
    int _hover = -1;
    WheelMode? _pendingMode;
    DateTime _pendingSince;
    DateTime _buttonUpSince = DateTime.MaxValue;
    bool _outsideWasDown = true;
    bool _done;
    KeyboardHook? _hook;

    public WheelWindow(IReadOnlyList<string> files, WheelOpen open)
    {
        InitializeComponent();
        _files = files;
        _open = open;
        _mode = App.Instance.Settings.DefaultMode;
        ShowActivated = open == WheelOpen.Click;
        _segmentFill = (Brush)FindResource("Surface");
        _signal = (Brush)FindResource("Signal");
        _onSignal = (Brush)FindResource("OnSignal");
        _text = (Brush)FindResource("Text");
        _dim = (Brush)FindResource("TextDim");
        _faint = (Brush)FindResource("TextFaint");

        var kind = FileKinds.Dominant(files);
        var kindName = kind switch
        {
            FileKind.Image => "images", FileKind.Video => "videos", FileKind.Audio => "audio files",
            FileKind.Document => "documents", FileKind.Spreadsheet => "spreadsheets", FileKind.Subtitle => "subtitle files", _ => "files",
        };
        FileSummary.Text = files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Count} {kindName}";
        _defaultHint = open switch
        {
            WheelOpen.Drag => "Release over a segment · a key switches mode",
            WheelOpen.Pointer => "Move to a segment and release · or click · Esc",
            _ => "Click a segment · Esc closes",
        };
        Hint.Text = _defaultHint;

        BuildModeList();
        Rebuild();

        SourceInitialized += (_, _) => { if (open != WheelOpen.Click) Native.MakeNoActivateToolWindow(this); };
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetHover(-1);
        Drop += OnDrop;
        MouseMove += (_, e) => { if (_open != WheelOpen.Drag) UpdateHover(e.GetPosition(Root)); };
        MouseLeftButtonUp += (_, e) => { if (_open != WheelOpen.Drag) Click(e.GetPosition(Root)); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(); };
        Deactivated += (_, _) => { if (_open == WheelOpen.Click) Finish(); };
        _poll.Tick += Poll;
    }

    /// <param name="anchor">Center in screen pixels; defaults to the current cursor position.</param>
    internal void ShowAt(Native.POINT? anchor = null)
    {
        // Position before showing so it doesn't flash elsewhere. Device pixels → DIPs via this window's own DPI.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var toDip = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformFromDevice;
        Native.POINT px;
        if (anchor is { } a) px = a; else Native.GetCursorPos(out px);
        var center = toDip.Transform(new Point(px.X, px.Y));
        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(px.X, px.Y)).WorkingArea;
        var area = new Rect(toDip.Transform(new Point(wa.Left, wa.Top)), toDip.Transform(new Point(wa.Right, wa.Bottom)));
        Left = Math.Clamp(center.X - C, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(center.Y - C, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Show();
        if (_open == WheelOpen.Click) { Activate(); Focus(); }
        _hook = new KeyboardHook(OnHookKey);
        if (!_hook.IsActive) Log.Info("keyboard hook unavailable, polling mode keys");
        _poll.Start();

        Zoom.ScaleX = Zoom.ScaleY = 0.9;
        Root.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
    }

    // ------------------------------------------------------------------ build

    void BuildModeList()
    {
        var keys = App.Instance.Settings.ModeKeys;
        foreach (var mode in Modes)
        {
            var name = new TextBlock { Text = ModeNames[mode], FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 1) };
            var key = new TextBlock
            {
                Text = keys.TryGetValue(mode, out var b) ? KeyNames.Describe(b) : "",
                FontFamily = (FontFamily)FindResource("Mono"), FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
            };
            var row = new Border
            {
                Height = HubRowHeight - 2,
                Width = 124,
                Margin = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(3),
                Child = new Grid { Children = { name, key } },
            };
            _modeRows.Add((row, name, key));
            ModeList.Children.Add(row);
        }
    }

    void Rebuild()
    {
        Ring.Children.Clear();
        Ticks.Children.Clear();
        _segments.Clear();
        _hover = -1;
        for (var i = 0; i < Modes.Length; i++)
        {
            var active = Modes[i] == _mode;
            var (row, name, key) = _modeRows[i];
            row.Background = active ? _signal : Brushes.Transparent;
            name.Foreground = active ? _onSignal : _dim;
            name.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            key.Foreground = active ? _onSignal : _faint;
        }

        var actions = App.Instance.Catalog.For(_mode, _files);
        // A missing external program (LibreOffice) is explained right in the wheel, not only after a drop.
        var blocked = actions.Where(a => a.Unavailable is not null).ToList();
        Hint.Text = blocked.Count == 0 ? _defaultHint
            : $"{string.Join(" / ", blocked.Select(a => a.Label))} {blocked[0].Unavailable}";
        if (actions.Count == 0)
        {
            Ring.Children.Add(Wedge(0, 359.99));
            var msg = new TextBlock
            {
                Text = _mode == WheelMode.Convert && !App.Instance.Catalog.HasMedia && FileKinds.Dominant(_files) is FileKind.Video or FileKind.Audio
                    ? "FFmpeg missing" : "No actions",
                Foreground = _dim,
                FontSize = 13,
            };
            Place(msg, 0, (OuterR + InnerR) / 2);
            return;
        }

        var sweep = 360.0 / actions.Count;
        for (var i = 0; i < actions.Count; i++)
        {
            var start = -90 - sweep / 2 + i * sweep;
            var path = Wedge(start, sweep);
            Ring.Children.Add(path);
            var tick = Tick(start + sweep / 2);
            Ticks.Children.Add(tick);
            _segments.Add(new Segment(path, tick, AddLabel(actions[i], start + sweep / 2), actions[i]));
        }
    }

    static Point P(double r, double deg) => new(C + r * Math.Cos(deg * Math.PI / 180), C + r * Math.Sin(deg * Math.PI / 180));

    Path Wedge(double startDeg, double sweepDeg)
    {
        var large = sweepDeg > 180;
        var fig = new PathFigure { StartPoint = P(InnerR, startDeg), IsClosed = true };
        fig.Segments.Add(new LineSegment(P(OuterR, startDeg), true));
        fig.Segments.Add(new ArcSegment(P(OuterR, startDeg + sweepDeg), new Size(OuterR, OuterR), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(InnerR, startDeg + sweepDeg), true));
        fig.Segments.Add(new ArcSegment(P(InnerR, startDeg), new Size(InnerR, InnerR), 0, large, SweepDirection.Counterclockwise, true));
        // The ink-colored stroke doubles as the gap between segments.
        return new Path { Data = new PathGeometry([fig]), Fill = _segmentFill, Stroke = (Brush)FindResource("Ink"), StrokeThickness = 3 };
    }

    Line Tick(double deg)
    {
        var a = P(OuterR + 5, deg);
        var b = P(OuterR + 11, deg);
        return new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = (Brush)FindResource("StrokeHi"), StrokeThickness = 2,
                          StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square };
    }

    TextBlock[] AddLabel(WheelAction action, double angleDeg)
    {
        var panel = new StackPanel { Width = 96, IsHitTestVisible = false, Opacity = action.Unavailable is null ? 1 : 0.4 };
        var texts = new List<TextBlock>();
        if (action.Glyph is not null)
        {
            texts.Add(new TextBlock { Text = action.Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 17,
                                      HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
        }
        texts.Add(new TextBlock { Text = action.Label, FontSize = action.Glyph is null ? 18 : 13.5, FontWeight = FontWeights.SemiBold,
                                  HorizontalAlignment = HorizontalAlignment.Center });
        texts.Add(new TextBlock { Text = action.Detail, FontSize = 10, FontStretch = FontStretches.SemiCondensed,
                                  TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
        foreach (var t in texts) panel.Children.Add(t);
        Paint(texts.ToArray(), hover: false);
        Place(panel, angleDeg, (OuterR + InnerR) / 2 + 2);
        return texts.ToArray();
    }

    void Paint(TextBlock[] texts, bool hover)
    {
        for (var i = 0; i < texts.Length; i++)
            texts[i].Foreground = hover ? _onSignal : i == texts.Length - 1 ? _dim : _text;
    }

    void Place(FrameworkElement el, double angleDeg, double radius)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var p = P(radius, angleDeg);
        Canvas.SetLeft(el, p.X - el.DesiredSize.Width / 2);
        Canvas.SetTop(el, p.Y - el.DesiredSize.Height / 2);
        if (!Ring.Children.Contains(el)) Ring.Children.Add(el);
    }

    // -------------------------------------------------------------- hit tests

    int SegmentAt(Point p)
    {
        if (_segments.Count == 0) return -1;
        var dx = p.X - C; var dy = p.Y - C;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < InnerR - 2 || dist > OuterR + 18) return -1;
        var sweep = 360.0 / _segments.Count;
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;      // -180..180, 0 = east
        var fromStart = (angle + 90 + sweep / 2 + 720) % 360; // 0 at start of segment 0
        return (int)(fromStart / sweep) % _segments.Count;
    }

    WheelMode? ModeAt(Point p)
    {
        for (var i = 0; i < _modeRows.Count; i++)
        {
            var row = _modeRows[i].Row;
            if (!row.IsVisible) continue;
            var topLeft = row.TranslatePoint(new Point(0, 0), Root);
            if (new Rect(topLeft, new Size(row.ActualWidth, row.ActualHeight)).Contains(p)) return Modes[i];
        }
        return null;
    }

    void UpdateHover(Point p)
    {
        SetHover(SegmentAt(p));
        var mode = ModeAt(p);
        if (mode is null) { _pendingMode = null; return; }
        // Dwell to switch (so a quick pass across the hub doesn't flip modes).
        if (_pendingMode != mode) { _pendingMode = mode; _pendingSince = DateTime.UtcNow; }
        else if ((DateTime.UtcNow - _pendingSince).TotalMilliseconds > 220) SwitchMode(mode.Value);
    }

    void SetHover(int index)
    {
        if (index == _hover) return;
        if (_hover >= 0 && _hover < _segments.Count) Highlight(_segments[_hover], false);
        _hover = index;
        if (_hover >= 0) Highlight(_segments[_hover], true);
    }

    void Highlight(Segment s, bool on)
    {
        var usable = s.Action.Unavailable is null;
        s.Shape.Fill = on && usable ? _signal : on ? (Brush)FindResource("SurfaceHi") : _segmentFill;
        s.Tick.Stroke = on ? _signal : (Brush)FindResource("StrokeHi");
        Paint(s.Texts, on && usable);
    }

    void SwitchMode(WheelMode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        Rebuild();
    }

    // ----------------------------------------------------------------- events

    void OnDragOver(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(Root);
        UpdateHover(p);
        e.Effects = SegmentAt(p) >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var index = SegmentAt(e.GetPosition(Root));
        e.Effects = index >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Run(index);
    }

    void Click(Point p)
    {
        if (ModeAt(p) is { } mode) { SwitchMode(mode); return; }
        var index = SegmentAt(p);
        if (index >= 0) Run(index);
        else if (Math.Sqrt(Math.Pow(p.X - C, 2) + Math.Pow(p.Y - C, 2)) > OuterR + 18) Finish();
    }

    /// <summary>Pointer mode: the right button that opened the wheel went up (reported by the mouse hook).</summary>
    public void PointerReleased()
    {
        if (_done || _open != WheelOpen.Pointer) return;
        var index = SegmentAt(CursorInRoot());
        if (index >= 0) Run(index); // a flick onto a segment; anywhere else the wheel stays open for clicking
    }

    /// <summary>Works while the cursor is outside the window too (Mouse.GetPosition does not).</summary>
    Point CursorInRoot()
    {
        Native.GetCursorPos(out var px);
        return Root.PointFromScreen(new Point(px.X, px.Y));
    }

    void Run(int index)
    {
        if (index < 0 || index >= _segments.Count) { Finish(); return; }
        var action = _segments[index].Action;
        var files = _files;
        Finish();
        if (action.Unavailable is not null)
        {
            Dispatcher.BeginInvoke(() => App.Instance.Notify($"{action.Label} {action.Unavailable}"));
            return;
        }
        // Leave the OLE drop callback before doing work (clipboard access inside it fails).
        Dispatcher.BeginInvoke(() => App.Instance.Execute(action, files));
    }

    /// <summary>Hook callback: switch mode on a matching key and swallow it so Explorer doesn't type-select files.</summary>
    bool OnHookKey(int vk)
    {
        if (vk == Native.VK_ESCAPE && _open != WheelOpen.Click)
        {
            Dispatcher.BeginInvoke(Finish);
            return true;
        }
        foreach (var (mode, binding) in App.Instance.Settings.ModeKeys)
        {
            if (binding.VirtualKey != vk || !Native.ModifiersMatch(binding)) continue;
            Dispatcher.BeginInvoke(() => SwitchMode(mode));
            return true;
        }
        return false;
    }

    void Poll(object? sender, EventArgs e)
    {
        // Fallback when the hook could not be installed: poll (may miss very short taps).
        if (_hook is not { IsActive: true })
        {
            foreach (var (mode, binding) in App.Instance.Settings.ModeKeys)
            {
                var down = Native.IsDown(binding);
                if (down && !_keyWasDown.GetValueOrDefault(mode)) SwitchMode(mode);
                _keyWasDown[mode] = down;
            }
            if (Native.IsDown(Native.VK_ESCAPE) && _open != WheelOpen.Click) { Finish(); return; }
        }

        if (_open == WheelOpen.Pointer)
        {
            // Not activated, so no Deactivated event: a press anywhere outside the wheel closes it.
            var down = Native.IsDown(Native.VK_LBUTTON) || Native.IsDown(Native.VK_RBUTTON);
            var p = CursorInRoot();
            var outside = Math.Sqrt(Math.Pow(p.X - C, 2) + Math.Pow(p.Y - C, 2)) > OuterR + 18;
            if (down && outside && !_outsideWasDown) { Finish(); return; }
            _outsideWasDown = down;
            return;
        }
        if (_open != WheelOpen.Drag) return;
        // Drag ended somewhere else (released outside the wheel): close after a short grace period.
        var held = Native.IsDown(Native.VK_LBUTTON) || Native.IsDown(Native.VK_RBUTTON);
        if (held) _buttonUpSince = DateTime.MaxValue;
        else if (_buttonUpSince == DateTime.MaxValue) _buttonUpSince = DateTime.UtcNow;
        else if ((DateTime.UtcNow - _buttonUpSince).TotalMilliseconds > 250) Finish();
    }

    void Finish()
    {
        if (_done) return;
        _done = true;
        _poll.Stop();
        _hook?.Dispose();
        Close();
    }
}
