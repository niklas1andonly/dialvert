using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Dialvert.App.Interop;
using Dialvert.Core;
using Dialvert.Core.Jobs;

namespace Dialvert.App.Views;

/// <summary>
/// The organizer: persistent history (live jobs on top), totals, search, and the settings page.
/// Custom title bar (WindowChrome), so there is no white system caption.
/// </summary>
public partial class MainWindow : Window
{
    // Live Job objects while running, HistoryEntry once finished.
    readonly ObservableCollection<object> _items = [];
    readonly ICollectionView _view;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var entry in App.Instance.History.Load()) _items.Add(entry);
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = Matches;
        Queue.ItemsSource = _view;
        _items.CollectionChanged += (_, _) => Refresh();

        SourceInitialized += (_, _) => Native.StyleFrame(this, (Color)FindResource("StrokeColor"));
        StateChanged += (_, _) =>
        {
            // A maximized chrome-less window overhangs the screen by the resize border.
            var b = SystemParameters.WindowResizeBorderThickness;
            Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(b.Left + 4, b.Top + 4, b.Right + 4, b.Bottom + 4) : default;
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        };

        DragEnter += (_, e) => { if (AcceptFiles(e)) DropOverlay.Visibility = Visibility.Visible; };
        DragOver += (_, e) => AcceptFiles(e);
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += (_, e) =>
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                App.Instance.ShowWheel(files, WheelOpen.Click);
        };
        Refresh();
        RefreshStatus();
    }

    static bool AcceptFiles(DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        return ok;
    }

    // --------------------------------------------------------------- history

    public void Add(Job job) => _items.Insert(0, job);

    /// <summary>Swaps the live job for its history record in place.</summary>
    public void Complete(Job job, HistoryEntry entry)
    {
        var i = _items.IndexOf(job);
        if (i >= 0) _items[i] = entry; else _items.Insert(0, entry);
        Save();
    }

    IEnumerable<HistoryEntry> Entries => _items.OfType<HistoryEntry>();

    void Save()
    {
        try { App.Instance.History.Save(Entries); }
        catch (IOException ex) { Log.Error("history save", ex); }
    }

    void Refresh()
    {
        var done = Entries.Where(e => e.State == JobState.Done).ToList();
        StatCount.Text = done.Sum(e => Math.Max(1, e.Outputs.Count)).ToString("N0");
        StatSaved.Text = CompressSpec.Describe(done.Sum(e => Math.Max(0, e.BytesBefore - e.BytesAfter)));
        StatToday.Text = done.Count(e => e.Time.Date == DateTime.Today).ToString("N0");
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Visibility = _items.Count > 0 && _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    bool Matches(object item)
    {
        var q = Search.Text.Trim();
        if (q.Length == 0) return true;
        bool Has(string? s) => s?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;
        return item switch
        {
            HistoryEntry e => Has(e.Title) || Has(e.SubText) || e.Inputs.Any(p => Has(Path.GetFileName(p))),
            Job j => Has(j.Title),
            _ => true,
        };
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        Refresh();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in Entries.ToList()) _items.Remove(entry);
        Save();
    }

    static T? TagOf<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    void Cancel_Click(object sender, RoutedEventArgs e) => TagOf<Job>(sender)?.Cancel();

    void Open_Click(object sender, RoutedEventArgs e) => Open(TagOf<HistoryEntry>(sender));

    void Row_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Open(TagOf<HistoryEntry>(sender));
    }

    void Open(HistoryEntry? entry)
    {
        if (entry?.FirstOutput is not { } path) { App.Instance.Notify("File no longer exists"); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var entry = TagOf<HistoryEntry>(sender);
        var path = entry?.FirstOutput ?? entry?.Inputs.FirstOrDefault(File.Exists);
        if (path is null) { App.Instance.Notify("File no longer exists"); return; }
        App.Reveal(path);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<HistoryEntry>(sender) is not { } entry) return;
        _items.Remove(entry);
        Save();
    }

    // ----------------------------------------------------------------- pages

    public void ShowPage(bool settings)
    {
        (settings ? TabSettings : TabHistory).IsChecked = true;
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HistoryPage is null) return; // during InitializeComponent
        var settings = TabSettings.IsChecked == true;
        HistoryPage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        if (settings) SettingsPage.Load();
    }

    public void RefreshStatus()
    {
        var app = App.Instance;
        var trigger = KeyNames.Describe(app.Settings.WheelTrigger);
        EmptyKey.Text = trigger;
        FooterHint.Text = $"{trigger.ToUpperInvariant()} + RIGHT-CLICK  ·  WHEEL";
        EngineDots.Children.Clear();
        AddDot(app.Ffmpeg is not null, app.H264Encoder is { } enc ? $"FFmpeg · {enc}" : "FFmpeg");
        AddDot(app.Documents.Pandoc is not null, "Pandoc");
        AddDot(app.Documents.Office is not null, "LibreOffice");
        if (SettingsPage.IsVisible) SettingsPage.RefreshEngines();
    }

    void AddDot(bool ok, string name)
    {
        EngineDots.Children.Add(new Border
        {
            Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = ok ? (Brush)FindResource("Signal") : Brushes.Transparent,
            BorderBrush = ok ? (Brush)FindResource("Signal") : (Brush)FindResource("TextFaint"), BorderThickness = new Thickness(1),
        });
        EngineDots.Children.Add(new TextBlock
        {
            Text = name, Style = (Style)FindResource("MonoText"), FontSize = 10.5, Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource(ok ? "TextDim" : "TextFaint"),
            ToolTip = ok ? "ready" : name == "LibreOffice" ? "not installed – Office formats ↔ PDF are disabled" : "missing",
        });
    }

    // --------------------------------------------------------------- chrome

    void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Choose files for Dialvert" };
        if (dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0)
            App.Instance.ShowWheel(dlg.FileNames, WheelOpen.Click);
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // Closing hides to tray; quitting is explicit (tray menu).
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
