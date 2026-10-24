using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Dialvert.App.Interop;

/// <summary>
/// Finds the files a right-click in Explorer or on the desktop is about, the way Windows itself
/// decides it: the item under the cursor (selecting it if it wasn't), or the whole selection
/// when the cursor is on an item that is part of it. Clicking empty space uses the selection.
/// </summary>
static class ShellSelection
{
    /// <summary>Runs on a fresh STA thread (Shell COM and UI Automation need one; the UI thread stays free).</summary>
    public static Task<List<string>> GetAsync(Native.POINT pt, IntPtr root)
    {
        var tcs = new TaskCompletionSource<List<string>>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(Get(pt, root)); }
            catch (Exception ex)
            {
                Log.Error("shell selection", ex);
                tcs.SetResult([]);
            }
        }) { IsBackground = true, Name = "Dialvert shell selection" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    const int SVSI_SELECT = 0x1, SVSI_DESELECTOTHERS = 0x4, SVSI_FOCUSED = 0x10;

    static List<string> Get(Native.POINT pt, IntPtr root)
    {
        var clicked = ItemAt(pt);
        if (FolderView(root) is not { } view) { Log.Info("selection: no shell view"); return []; }
        List<(string Name, string Path)> selection = Selection(view);
        if (clicked is null)
        {
            Log.Info($"selection: no item under cursor, using {selection.Count} selected");
            return Existing(selection.Select(s => s.Path));
        }

        var name = clicked.Current.Name;
        // Right-clicking an item that is part of the selection means the whole selection.
        if (selection.Any(s => Same(s.Name, name))) return Existing(selection.Select(s => s.Path));

        // Otherwise it's just that item. Resolve it in the folder by its display name and select it,
        // as a plain right-click would (selecting via UI Automation alone lands too late to read back).
        List<dynamic> matches = FolderItemsNamed(view, name);
        Log.Info($"selection: item '{name}' not selected, {matches.Count} match(es) in folder");
        if (matches.Count != 1) return [];
        view.SelectItem(matches[0], SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED);
        return Existing([(string)matches[0].Path]);
    }

    static List<(string Name, string Path)> Selection(dynamic view)
    {
        var result = new List<(string, string)>();
        var selected = view.SelectedItems();
        for (int i = 0, n = selected.Count; i < n; i++)
        {
            var item = selected.Item(i);
            result.Add(((string)item.Name, (string)item.Path));
        }
        return result;
    }

    /// <summary>Items whose display name equals <paramref name="name"/> (it may lack the extension).</summary>
    static List<dynamic> FolderItemsNamed(dynamic view, string name)
    {
        var folder = view.Folder;
        if (folder.ParseName(name) is { } exact) return [exact];
        var result = new List<dynamic>();
        var items = folder.Items();
        for (int i = 0, n = items.Count; i < n; i++)
        {
            var item = items.Item(i);
            if (Same((string)item.Name, name)) result.Add(item);
        }
        return result;
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

    static List<string> Existing(IEnumerable<string> paths) => paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

    /// <summary>The list item (icon or details row) under the cursor, if any.</summary>
    static AutomationElement? ItemAt(Native.POINT pt)
    {
        var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
        var walker = TreeWalker.ControlViewWalker;
        var trail = new List<string>();
        for (var depth = 0; el is not null && depth < 4; depth++, el = walker.GetParent(el))
        {
            var type = el.Current.ControlType;
            trail.Add($"{type.ProgrammaticName}:{el.Current.Name}");
            if (type == ControlType.ListItem || type == ControlType.DataItem) return el;
            if (type == ControlType.List || type == ControlType.Pane || type == ControlType.Window) break;
        }
        Log.Info($"selection: no list item at {pt.X},{pt.Y} ({string.Join(" < ", trail)})");
        return null;
    }

    // ------------------------------------------------------------------ shell

    static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid IID_IShellBrowser = typeof(IShellBrowser).GUID;
    static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
    const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SVGIO_BACKGROUND = 0;

    /// <summary>The IShellFolderViewDual of the active tab of an Explorer window, or of the desktop.</summary>
    static dynamic? FolderView(IntPtr root)
    {
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows)!)!;
        IShellBrowser? browser = null;
        if (Native.ClassName(root) == "CabinetWClass")
        {
            // Windows 11 tabs: several shell windows share one frame; the active tab's window comes first.
            var tab = Native.FindWindowEx(root, IntPtr.Zero, "ShellTabWindowClass", null);
            for (var i = 0; i < windows.Count && browser is null; i++)
            {
                var candidate = BrowserOf(windows.Item(i));
                if (candidate is null) continue;
                candidate.GetWindow(out var hwnd);
                if (hwnd == (tab != IntPtr.Zero ? tab : root)) browser = candidate;
            }
        }
        else
        {
            object loc = 0 /* CSIDL_DESKTOP */, empty = null!;
            browser = BrowserOf(windows.FindWindowSW(ref loc, ref empty, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH));
        }
        if (browser is null) return null;
        browser.QueryActiveShellView(out var view);
        view.GetItemObject(SVGIO_BACKGROUND, IID_IDispatch, out var dispatch);
        return dispatch;
    }

    static IShellBrowser? BrowserOf(object? window)
    {
        if (window is not IServiceProvider sp) return null;
        return sp.QueryService(SID_STopLevelBrowser, IID_IShellBrowser, out var obj) == 0 ? obj as IShellBrowser : null;
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        void _NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object loc, ref object locRoot, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [PreserveSig]
        int QueryService([In] in Guid service, [In] in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        void QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();
        void GetItemObject(uint item, [In] in Guid riid, [MarshalAs(UnmanagedType.IDispatch)] out object obj);
    }
}
