using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Dialvert.Core.Settings;

namespace Dialvert.App.Interop;

static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(IntPtr hWnd, char* buffer, int max);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? title);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    public static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_ESCAPE = 0x1B;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_NOACTIVATE = 0x08000000L, WS_EX_TOOLWINDOW = 0x00000080L;

    public static unsafe string ClassName(IntPtr hwnd)
    {
        var buffer = stackalloc char[64];
        var n = GetClassName(hwnd, buffer, 64);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    /// <summary>
    /// A press+release of an unassigned key code. Sent after a swallowed click with Alt/Win held,
    /// so releasing the modifier does not open Explorer's menu bar or the Start menu.
    /// </summary>
    public static void SendDummyKey()
    {
        const byte VK_UNASSIGNED = 0xE8;
        keybd_event(VK_UNASSIGNED, 0, 0, 0);
        keybd_event(VK_UNASSIGNED, 0, 2 /* KEYEVENTF_KEYUP */, 0);
    }

    /// <summary>Windows 11: dark, rounded frame with a border in the app's own color (ignored on Windows 10).</summary>
    public static void StyleFrame(Window w, Color border)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int dark = 1, round = 2 /* DWMWCP_ROUND */, colorRef = border.R | border.G << 8 | border.B << 16;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref colorRef, sizeof(int));
    }

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>True when the key and exactly its modifiers are held right now (works during OLE drags).</summary>
    public static bool IsDown(HotKey binding) => IsDown(binding.VirtualKey) && ModifiersMatch(binding);

    /// <summary>True when exactly the binding's modifiers are held (the key itself is not checked).</summary>
    public static bool ModifiersMatch(HotKey binding)
    {
        var m = binding.Modifiers;
        if (binding.VirtualKey is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN) return true; // bare modifier as mode key
        return IsDown(VK_CONTROL) == m.HasFlag(KeyModifiers.Control)
            && IsDown(VK_MENU) == m.HasFlag(KeyModifiers.Alt)
            && (IsDown(VK_SHIFT) == m.HasFlag(KeyModifiers.Shift) || binding.VirtualKey == VK_SHIFT)
            && (IsDown(VK_LWIN) || IsDown(VK_RWIN)) == m.HasFlag(KeyModifiers.Win);
    }

    /// <summary>Cursor position in WPF device-independent units of the given visual's monitor.</summary>
    public static Point CursorDip(Visual reference)
    {
        GetCursorPos(out var p);
        var source = PresentationSource.FromVisual(reference);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return m.Transform(new Point(p.X, p.Y));
    }

    /// <summary>Overlay windows must not steal focus from Explorer mid-drag, nor show in Alt+Tab.</summary>
    public static void MakeNoActivateToolWindow(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }
}

static class KeyNames
{
    public static string Describe(HotKey b)
    {
        if (b.VirtualKey is Native.VK_SHIFT or Native.VK_CONTROL or Native.VK_MENU or Native.VK_LWIN) return Key(b.VirtualKey);
        var parts = new List<string>();
        if (b.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (b.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (b.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (b.Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key(b.VirtualKey));
        return string.Join("+", parts);
    }

    const uint MAPVK_VK_TO_CHAR = 2;

    public static string Key(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key switch
        {
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => "Num " + (key - System.Windows.Input.Key.NumPad0),
            System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift => "Shift",
            System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl => "Ctrl",
            System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt => "Alt",
            System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin => "Win",
            System.Windows.Input.Key.Space => "Space",
            // OEM keys (;, ', [, Ö, Ä, Ü …) are named after the character they type in the current layout.
            _ when vk is >= 0xBA and <= 0xE2 && Native.MapVirtualKey((uint)vk, MAPVK_VK_TO_CHAR) is var ch and not 0 =>
                char.ToUpperInvariant((char)(ch & 0x7FFF)).ToString(),
            _ => key.ToString(),
        };
    }

    public static KeyModifiers FromWpf(ModifierKeys m) =>
        (m.HasFlag(ModifierKeys.Control) ? KeyModifiers.Control : 0) |
        (m.HasFlag(ModifierKeys.Alt) ? KeyModifiers.Alt : 0) |
        (m.HasFlag(ModifierKeys.Shift) ? KeyModifiers.Shift : 0) |
        (m.HasFlag(ModifierKeys.Windows) ? KeyModifiers.Win : 0);
}
