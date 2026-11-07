using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Porta.Interop;

/// <summary>
/// Reports a mouse button press on a window that belongs to another program, wherever it is (any monitor, the
/// taskbar, the desktop). Windows doesn't always change the active window for those clicks (for example a
/// taskbar from a screen add-on), so the menu can't rely on losing focus alone. It never blocks the click.
/// Must be installed on a thread with a message loop (the UI thread).
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _callback; // kept in a field so the GC can't collect it
    private readonly uint _ourProcess = (uint)Environment.ProcessId;
    private IntPtr _hook;

    /// <summary>Raised on the thread that installed the hook, after the click has already gone through.</summary>
    public event Action? ClickedElsewhere;

    public MouseHook() => _callback = OnMouse;

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (IsInstalled) return;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _callback, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN
                or Native.WM_XBUTTONDOWN or Native.WM_NCLBUTTONDOWN or Native.WM_NCRBUTTONDOWN)
        {
            var point = Marshal.PtrToStructure<Native.POINT>(lParam); // the hook struct starts with the screen position
            var window = Native.WindowFromPoint(point);
            // Our own windows (the menu, its pop-ups and right-click menus) are not "elsewhere".
            if (window != IntPtr.Zero && Native.GetWindowThreadProcessId(window, out uint process) != 0 && process != _ourProcess)
                ClickedElsewhere?.Invoke();
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
