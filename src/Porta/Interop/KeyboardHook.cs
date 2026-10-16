using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Porta.Interop;

/// <summary>
/// Watches the keyboard system-wide. A Win key press on its own (not part of Win+E, Win+R, etc.)
/// raises <see cref="WinKeyTapped"/> instead of opening the Windows Start menu.
/// Must be installed on a thread with a message loop (the UI thread).
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _callback; // kept in a field so the GC can't collect it
    private IntPtr _hook;
    private bool _winDown;
    private bool _usedInShortcut;

    public event Action? WinKeyTapped;
    public Func<bool> ShouldIntercept { get; set; } = () => true;

    public KeyboardHook() => _callback = OnKey;

    public void Install()
    {
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _callback, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var key = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (key.flags & Native.LLKHF_INJECTED) != 0;

            if (!injected)
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                bool isWin = key.vkCode is Native.VK_LWIN or Native.VK_RWIN;

                if (isWin && down)
                {
                    if (!_winDown) { _winDown = true; _usedInShortcut = false; }
                }
                else if (isWin && up)
                {
                    bool tapped = _winDown && !_usedInShortcut;
                    _winDown = false;
                    if (tapped && ShouldIntercept())
                    {
                        // Swallow the real key-up and replay it behind a mask key, so Windows' Start stays closed.
                        Native.SendMaskedWinKeyUp(key.vkCode);
                        WinKeyTapped?.Invoke();
                        return 1;
                    }
                }
                else if (down && _winDown)
                {
                    _usedInShortcut = true; // Win+something: leave it to Windows
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
