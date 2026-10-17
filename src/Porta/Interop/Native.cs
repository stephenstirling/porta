using System.Runtime.InteropServices;
using System.Text;

namespace Porta.Interop;

/// <summary>Win32 functions Porta needs that WinUI doesn't expose.</summary>
internal static class Native
{
    // ---- Keyboard hook ----
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const uint LLKHF_INJECTED = 0x10;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ---- SendInput ----
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Sends an unused "mask" key (0xE8) followed by the Win key release.
    /// Windows then thinks the Win key was part of a shortcut and doesn't open its own Start menu.
    /// </summary>
    public static void SendMaskedWinKeyUp(uint winVk)
    {
        var inputs = new INPUT[3];
        inputs[0].type = INPUT_KEYBOARD; inputs[0].U.ki.wVk = 0xE8;
        inputs[1].type = INPUT_KEYBOARD; inputs[1].U.ki.wVk = 0xE8; inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[2].type = INPUT_KEYBOARD; inputs[2].U.ki.wVk = (ushort)winVk; inputs[2].U.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // ---- Windows and monitors ----
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>Brings a window to the front even when Windows would normally refuse.</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(foreground, out _);
        uint ourThread = GetCurrentThreadId();
        if (fgThread != ourThread && fgThread != 0)
        {
            AttachThreadInput(ourThread, fgThread, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            AttachThreadInput(ourThread, fgThread, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
        }
    }

    public static double ScaleAtPoint(POINT pt)
    {
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    // ---- Window frame (Windows 11; ignored on Windows 10) ----
    public const int DWMWA_NCRENDERING_POLICY = 2, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// No Windows border at all. Needed for a window cut to a custom shape: Windows draws its border
    /// around the full window rectangle, ignoring the cut, which shows as a box around the menu.
    /// </summary>
    public static void HideBorder(IntPtr hwnd)
    {
        int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
        // Also no rounded frame rim: it outlines the full window rectangle too.
        int doNotRound = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));
        int ncDisabled = 1; // DWMNCRP_DISABLED: no Windows-drawn frame or shadow
        DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref ncDisabled, sizeof(int));
    }

    // ---- Window shape ----
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    public const int RGN_OR = 2;

    // ---- Power and session ----
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // ---- User ----
    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserNameEx(int nameFormat, StringBuilder name, ref uint size);

    public static string GetDisplayName()
    {
        uint size = 256;
        var sb = new StringBuilder((int)size);
        // 3 = NameDisplay. Often empty for Microsoft accounts, so fall back to the sign-in name.
        if (GetUserNameEx(3, sb, ref size) && sb.Length > 0) return sb.ToString();
        return Environment.UserName;
    }
}
