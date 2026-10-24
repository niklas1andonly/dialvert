using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dialvert.App.Interop;

namespace Dialvert.App.Views;

/// <summary>
/// Small notice that appears only when there is something to say: next to the cursor for
/// immediate feedback ("Path copied"), or above the tray while jobs run. Click opens the history.
/// </summary>
public partial class ToastWindow : Window
{
    readonly bool _nearCursor;
    readonly DispatcherTimer _hide = new();

    public ToastWindow(bool nearCursor)
    {
        InitializeComponent();
        _nearCursor = nearCursor;
        SourceInitialized += (_, _) => Native.MakeNoActivateToolWindow(this);
        _hide.Tick += (_, _) => { _hide.Stop(); FadeOut(); };
        MouseLeftButtonUp += (_, _) => { Hide(); App.Instance.ShowMain(); };
        SizeChanged += (_, _) => Place();
    }

    /// <param name="progress">0..1 shows a bar; null hides it.</param>
    /// <param name="hideAfter">null keeps it up until the next call.</param>
    public void Show(string headline, string? subline = null, double? progress = null, TimeSpan? hideAfter = null, bool error = false)
    {
        Headline.Text = headline;
        Subline.Text = subline ?? "";
        Subline.Visibility = string.IsNullOrEmpty(subline) ? Visibility.Collapsed : Visibility.Visible;
        Progress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        Progress.Value = progress ?? 0;
        Bar.Background = (Brush)FindResource(error ? "Danger" : "Signal");

        _hide.Stop();
        if (hideAfter is { } t) { _hide.Interval = t; _hide.Start(); }
        if (IsVisible && Opacity > 0.99) return;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        if (_nearCursor || !IsVisible) Place();
        base.Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }

    void FadeOut()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (Opacity < 0.01) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    void Place()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var toDip = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformFromDevice;
        Native.GetCursorPos(out var px);
        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(px.X, px.Y)).WorkingArea;
        var area = new Rect(toDip.Transform(new Point(wa.Left, wa.Top)), toDip.Transform(new Point(wa.Right, wa.Bottom)));
        var w = ActualWidth > 0 ? ActualWidth : 260;
        var h = ActualHeight > 0 ? ActualHeight : 70;
        if (_nearCursor)
        {
            var c = toDip.Transform(new Point(px.X, px.Y));
            Left = Math.Clamp(c.X + 14, area.Left, area.Right - w);
            Top = Math.Clamp(c.Y + 18, area.Top, area.Bottom - h);
        }
        else
        {
            Left = area.Right - w - 12;
            Top = area.Bottom - h - 12;
        }
    }
}
