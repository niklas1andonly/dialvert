using System.Windows.Interop;
using Dialvert.Core.Settings;

namespace Dialvert.App.Interop;

/// <summary>System-wide hotkeys via RegisterHotKey on a message-only window.</summary>
sealed class HotkeyManager : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;

    readonly HwndSource _source;
    readonly Dictionary<int, Action> _handlers = new();
    int _nextId = 1;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("DialvertHotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _source.AddHook(WndProc);
    }

    /// <summary>Returns false when another app already owns the combination.</summary>
    public bool Register(HotKey binding, Action handler)
    {
        var id = _nextId++;
        uint mods = MOD_NOREPEAT;
        if (binding.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= 0x1;
        if (binding.Modifiers.HasFlag(KeyModifiers.Control)) mods |= 0x2;
        if (binding.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= 0x4;
        if (binding.Modifiers.HasFlag(KeyModifiers.Win)) mods |= 0x8;
        if (!Native.RegisterHotKey(_source.Handle, id, mods, (uint)binding.VirtualKey)) return false;
        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var h))
        {
            h();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
