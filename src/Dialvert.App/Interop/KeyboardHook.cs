using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dialvert.App.Interop;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL), active only while the wheel is open.
/// Polling GetAsyncKeyState misses quick taps; the hook sees every key-down, even while
/// another process (Explorer) runs the OLE drag loop.
/// </summary>
sealed partial class KeyboardHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr extra; }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? name);

    readonly HookProc _proc; // kept alive: the OS holds only a raw pointer
    readonly Func<int, bool> _onKeyDown;
    IntPtr _hook;

    /// <param name="onKeyDown">Gets the virtual-key code; return true to swallow the key.</param>
    public KeyboardHook(Func<int, bool> onKeyDown)
    {
        _onKeyDown = onKeyDown;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    public bool IsActive => _hook != IntPtr.Zero;

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Fold left/right modifier variants into the generic codes the settings store.
            var vk = info.vkCode switch
            {
                0xA0 or 0xA1 => Native.VK_SHIFT,
                0xA2 or 0xA3 => Native.VK_CONTROL,
                0xA4 or 0xA5 => Native.VK_MENU,
                0x5C => Native.VK_LWIN,
                _ => info.vkCode,
            };
            try { if (_onKeyDown(vk)) return 1; }
            catch (Exception ex) { Log.Error("keyboard hook", ex); }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
