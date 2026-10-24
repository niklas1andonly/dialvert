using System.Windows;
using System.Windows.Media;
using Dialvert.App.Interop;

namespace Dialvert.App.Views;

/// <summary>
/// Optional always-on-top plate (hidden by default). Dragging files onto it pops up the action
/// wheel under the cursor, where the mode keys work while still dragging.
/// </summary>
public partial class DropZoneWindow : Window
{
    public DropZoneWindow()
    {
        InitializeComponent();
        var s = App.Instance.Settings;
        var work = SystemParameters.WorkArea;
        Left = s.DropZoneLeft is { } l && l >= SystemParameters.VirtualScreenLeft && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            ? l : work.Right - Width - 24;
        Top = s.DropZoneTop is { } t && t >= SystemParameters.VirtualScreenTop && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40
            ? t : work.Bottom - Height - 24;

        SourceInitialized += (_, _) => Native.MakeNoActivateToolWindow(this);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { App.Instance.ShowMain(); return; }
            DragMove(); // blocks until the mouse is released
            var settings = App.Instance.Settings;
            settings.DropZoneLeft = Left;
            settings.DropZoneTop = Top;
            App.Instance.Store.Save(settings);
        };
        DragEnter += OnDragEnter;
        DragLeave += (_, _) => Plate.BorderBrush = (Brush)FindResource("StrokeHi");
        DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        Drop += (_, e) => { if (Files(e) is { } f) App.Instance.ShowWheel(f, WheelOpen.Click); };
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        Log.Info($"dropzone DragEnter formats={string.Join(",", e.Data.GetFormats())}");
        if (Files(e) is not { } files) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        Plate.BorderBrush = (Brush)FindResource("Signal");
        // The wheel window appears under the cursor and takes over as drop target.
        App.Instance.ShowWheel(files, WheelOpen.Drag);
    }

    static string[]? Files(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f ? f : null;

    void OpenMain_Click(object sender, RoutedEventArgs e) => App.Instance.ShowMain();
    void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    void Quit_Click(object sender, RoutedEventArgs e) => App.Instance.Quit();
}
