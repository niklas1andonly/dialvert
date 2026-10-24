using System.Diagnostics;
using System.Runtime.InteropServices;
using Dialvert.Core.Settings;

namespace Dialvert.App.Interop;

/// <summary>
/// Global low-level mouse hook that turns "trigger key held + right-click" on a file view of
/// Explorer or the desktop into a wheel request. The click is swallowed, so Explorer's own
/// context menu never opens; everywhere else right-clicks pass through untouched.
/// Runs on its own thread with its own message loop: a busy UI thread must never lag the mouse.
/// </summary>
sealed partial class MouseHook : IDisposable
{
    const int WH_MOUSE_LL = 14, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_QUIT = 0x0012;
    const int LLMHF_INJECTED = 0x1;
    const uint GA_ROOT = 2;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public Native.POINT pt; public int mouseData, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam, lParam; public int time; public Native.POINT pt; }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? name);

    readonly HookProc _proc; // kept alive: the OS holds only a raw pointer
    readonly Action<Native.POINT, IntPtr> _onTrigger;
    readonly Action _onRelease;
    readonly Thread _thread;
    volatile HotKey _trigger;
    uint _threadId;
    IntPtr _hook;
    bool _swallowUp;

    /// <param name="onTrigger">Screen point (device pixels) and the top-level shell window; called on the hook thread.</param>
    /// <param name="onRelease">The swallowed right button went up again.</param>
    public MouseHook(HotKey trigger, Action<Native.POINT, IntPtr> onTrigger, Action onRelease)
    {
        _trigger = trigger;
        _onTrigger = onTrigger;
        _onRelease = onRelease;
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "Dialvert mouse hook" };
        _thread.Start();
    }

    public HotKey Trigger { get => _trigger; set => _trigger = value; }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) { Log.Info($"mouse hook unavailable ({Marshal.GetLastPInvokeError()})"); return; }
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { /* hooks are called from inside GetMessage */ }
        UnhookWindowsHookEx(_hook);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WM_RBUTTONDOWN || wParam == WM_RBUTTONUP))
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (wParam == WM_RBUTTONUP && _swallowUp)
            {
                _swallowUp = false;
                _onRelease();
                return 1;
            }
            if (wParam == WM_RBUTTONDOWN && (info.flags & LLMHF_INJECTED) == 0 && _trigger.VirtualKey != 0
                && Native.IsDown(_trigger) && ShellViewAt(info.pt) is var root && root != IntPtr.Zero)
            {
                _swallowUp = true;
                try { _onTrigger(info.pt, root); }
                catch (Exception ex) { Log.Error("mouse hook", ex); }
                return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>
    /// The top-level Explorer/desktop window when the point is over its file view
    /// (SHELLDLL_DefView), else zero. Navigation pane, address bar and other apps don't count.
    /// </summary>
    internal static IntPtr ShellViewAt(Native.POINT pt)
    {
        var hwnd = Native.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var root = Native.GetAncestor(hwnd, GA_ROOT);
        if (Native.ClassName(root) is not ("CabinetWClass" or "Progman" or "WorkerW")) return IntPtr.Zero;
        for (var h = hwnd; h != IntPtr.Zero && h != root; h = Native.GetParent(h))
            if (Native.ClassName(h) == "SHELLDLL_DefView") return root;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
