using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;

namespace Porta.Interop;

/// <summary>
/// Turns a left click on the taskbar's Start button into <see cref="StartClicked"/> and keeps Windows from opening
/// its own Start menu. Works like the Win-key hook: a low-level hook, no admin rights, and Windows drops it by itself
/// if Porta exits or crashes, which brings the normal Start button back.
///
/// The hook is kept as light as it can be. It runs on its own thread (not the UI thread, so a busy menu can never
/// delay the mouse), and inside it only compares the pointer with rectangles that were worked out beforehand. Finding
/// the button (UI Automation, which is slow and talks to Explorer) happens on a second thread, about once a second,
/// so it is found again after Explorer restarts, a display changes or the taskbar alignment changes. Until a button
/// is known, or if looking fails, every click goes through to Windows untouched.
/// </summary>
internal sealed class StartButtonHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_QUIT = 0x0012;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint TouchPenSignature = 0xFF515700, SignatureMask = 0xFFFFFF00; // touch and pen clicks Windows turns into mouse clicks
    private const uint GA_ROOT = 2;
    private const int UIA_AutomationIdPropertyId = 30011, UIA_NamePropertyId = 30005;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public Native.POINT pt; public uint mouseData, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public Native.POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? window);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>Raised on the hook's own thread; the receiver must hand the work to the UI thread.</summary>
    public event Action? StartClicked;

    private readonly HookProc _callback; // kept in a field so the GC can't collect it
    private readonly ManualResetEventSlim _stopFinder = new();
    private IntPtr _hook;
    private Thread? _hookThread, _finderThread;
    private uint _hookThreadId;
    private bool _swallowNextUp; // we swallowed a press, so its release goes too
    private volatile Box[] _buttons = []; // where the Start buttons are, in screen pixels

    public StartButtonHook() => _callback = OnMouse;

    public bool IsRunning => _hookThread is not null;

    public void Start()
    {
        if (IsRunning) return;
        _stopFinder.Reset();
        var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() => HookLoop(ready)) { IsBackground = true, Name = "Porta Start button hook" };
        _hookThread.Start();
        ready.Wait(2000);
        _finderThread = new Thread(FinderLoop) { IsBackground = true, Name = "Porta Start button finder" };
        _finderThread.Start();
    }

    /// <summary>Removes the hook completely; Windows' own Start button works as normal again.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        _stopFinder.Set();
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _hookThread?.Join(2000);
        _finderThread?.Join(2000);
        _hookThread = _finderThread = null;
        _hookThreadId = 0;
        _buttons = [];
    }

    public void Dispose() => Stop();

    // ---------- The hook (kept tiny) ----------

    private void HookLoop(ManualResetEventSlim ready)
    {
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _callback, Native.GetModuleHandle(null), 0);
        ready.Set();
        if (_hook == IntPtr.Zero) return;
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // the hook is called from inside this wait
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int message = (int)wParam;
                if (message == WM_LBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (IsRealClick(info) && IsOverStartButton(info.pt))
                    {
                        _swallowNextUp = true;
                        StartClicked?.Invoke();
                        return 1;
                    }
                }
                else if (message == WM_LBUTTONUP && _swallowNextUp)
                {
                    _swallowNextUp = false;
                    return 1;
                }
            }
        }
        catch
        {
            // Never let anything here stop the mouse: fall through to Windows.
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Mouse, touch or pen; not clicks that another program made up with SendInput.</summary>
    private static bool IsRealClick(MSLLHOOKSTRUCT info) =>
        (info.flags & LLMHF_INJECTED) == 0 || ((uint)info.dwExtraInfo & SignatureMask) == TouchPenSignature;

    private bool IsOverStartButton(Native.POINT pt)
    {
        bool inside = false;
        foreach (var r in _buttons)
            if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom) { inside = true; break; }
        if (!inside) return false;
        // Something else may cover the taskbar (a full-screen window): only a click that really lands on a taskbar counts.
        var root = GetAncestor(WindowFromPoint(pt), GA_ROOT);
        return root != IntPtr.Zero && IsTaskbar(root);
    }

    private static bool IsTaskbar(IntPtr hwnd) => ClassOf(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(32);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    // ---------- Finding the button (off the hook thread) ----------

    private sealed class Found(IntPtr taskbar, IUIAutomationElement button)
    {
        public IntPtr Taskbar { get; } = taskbar;
        public IUIAutomationElement Button { get; } = button;
        public Box Rect;
    }

    private void FinderLoop()
    {
        var found = new List<Found>();
        IUIAutomation? automation = null;
        try
        {
            automation = new CUIAutomation();
            do
            {
                try { Refresh(automation, found); }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // Explorer is busy or restarting: know nothing for now, so clicks go through, and look again.
                    found.Clear();
                    _buttons = [];
                }
            }
            while (!_stopFinder.Wait(1000));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            System.Diagnostics.Debug.WriteLine("Porta: UI Automation isn't available: " + ex.Message);
        }
        finally
        {
            _buttons = [];
            if (automation is not null) Marshal.ReleaseComObject(automation);
        }
    }

    private void Refresh(IUIAutomation automation, List<Found> found)
    {
        var taskbars = FindTaskbars();

        // Drop buttons whose taskbar is gone (Explorer restarted, a display went away) or whose button has gone;
        // re-reading the rectangle also follows the button when the taskbar is switched between centred and left.
        found.RemoveAll(f => !taskbars.Contains(f.Taskbar) || !TryReadRect(f));
        foreach (var taskbar in taskbars)
        {
            if (found.Any(f => f.Taskbar == taskbar)) continue;
            var button = FindStartButton(automation, taskbar);
            if (button is null) continue;
            var f = new Found(taskbar, button);
            if (TryReadRect(f)) found.Add(f);
        }
        _buttons = found.Select(f => f.Rect).ToArray();
    }

    private static List<IntPtr> FindTaskbars()
    {
        var list = new List<IntPtr>();
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) list.Add(primary);
        EnumWindows((hwnd, _) =>
        {
            if (ClassOf(hwnd) == "Shell_SecondaryTrayWnd") list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static IUIAutomationElement? FindStartButton(IUIAutomation automation, IntPtr taskbar)
    {
        var byId = automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, "StartButton");
        var byName = automation.CreatePropertyCondition(UIA_NamePropertyId, "Start");
        var either = automation.CreateOrCondition(byId, byName);
        return automation.ElementFromHandle(taskbar).FindFirst(TreeScope.TreeScope_Descendants, either);
    }

    /// <summary>Reads where the button is now. False if it is gone or has no size (the taskbar is hidden).</summary>
    private static bool TryReadRect(Found f)
    {
        try
        {
            var r = f.Button.CurrentBoundingRectangle;
            f.Rect = new Box { Left = r.left, Top = r.top, Right = r.right, Bottom = r.bottom };
            return r.right > r.left && r.bottom > r.top;
        }
        catch (COMException)
        {
            return false; // the element no longer exists
        }
    }
}
