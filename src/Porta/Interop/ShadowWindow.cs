using System.Runtime.InteropServices;

namespace Porta.Interop;

/// <summary>
/// A soft drop shadow for the menu, drawn in a window of its own just behind it. The menu window is cut to its
/// rounded shape (which is what removes Windows' own shadow), so a shadow can't be part of it. This window is
/// see-through, never takes focus and lets every click and mouse move through to what is underneath.
/// The shadow is only drawn outside the menu's shape; behind the menu itself nothing is drawn, so the glass
/// shows the wallpaper and not a dark patch.
/// </summary>
internal sealed class ShadowWindow : IDisposable
{
    /// <summary>Where the menu is on screen, in pixels: the rounded panel and the account picture's circle.</summary>
    public readonly record struct Shape(int Left, int Top, int Right, int Bottom, double CornerRadius, double CircleX, double CircleY, double CircleRadius);

    private const string ClassName = "PortaMenuShadow";
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const int SW_HIDE = 0, ULW_ALPHA = 2;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProc _proc = (hwnd, msg, wParam, lParam) => DefWindowProc(hwnd, msg, wParam, lParam); // kept in a field so the GC can't collect it

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref Native.POINT ptDst, ref SIZE size, IntPtr src, ref Native.POINT ptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    private static bool _classRegistered;
    private IntPtr _hwnd;

    /// <summary>Draws the shadow around <paramref name="shape"/> and shows it directly behind <paramref name="menu"/>.</summary>
    public void Show(IntPtr menu, Shape shape, double scale, bool lightTheme)
    {
        double blur = 22 * scale, offset = 6 * scale;
        double peak = lightTheme ? 0.22 : 0.34;
        int pad = (int)Math.Ceiling(blur + offset) + 2;

        // The window covers the panel and the picture's circle, plus room for the soft edge all round.
        int left = Math.Min(shape.Left, (int)Math.Floor(shape.CircleX - shape.CircleRadius)) - pad;
        int top = (int)Math.Floor(Math.Min(shape.Top, shape.CircleY - shape.CircleRadius)) - pad;
        int right = Math.Max(shape.Right, (int)Math.Ceiling(shape.CircleX + shape.CircleRadius)) + pad;
        int bottom = shape.Bottom + pad;
        int width = right - left, height = bottom - top;
        if (width <= 0 || height <= 0) return;

        EnsureWindow();
        if (_hwnd == IntPtr.Zero) return;

        // Worked out in the window's own coordinates, so the same picture serves every time the menu opens at this size.
        double halfW = (shape.Right - shape.Left) / 2.0, halfH = (shape.Bottom - shape.Top) / 2.0;
        double centreX = shape.Left + halfW - left, centreY = shape.Top + halfH - top;
        double circleX = shape.CircleX - left, circleY = shape.CircleY - top;
        var key = (width, height, centreX, centreY, halfW, halfH, shape.CornerRadius, circleX, circleY, shape.CircleRadius, peak);
        if (_cachedKey != key || _cachedPixels is null)
        {
            _cachedPixels = Render(width, height, centreX, centreY, halfW, halfH, shape.CornerRadius, circleX, circleY, shape.CircleRadius, blur, offset, peak);
            _cachedKey = key;
        }
        var pixels = _cachedPixels;

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero) return;
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var old = SelectObject(memory, bitmap);
            var position = new Native.POINT { X = left, Y = top };
            var size = new SIZE { cx = width, cy = height };
            var origin = new Native.POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
            UpdateLayeredWindow(_hwnd, screen, ref position, ref size, memory, ref origin, 0, ref blend, ULW_ALPHA);
            SelectObject(memory, old);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }

        // Directly behind the menu in the stacking order, without taking focus.
        SetWindowPos(_hwnd, menu, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private (int, int, double, double, double, double, double, double, double, double, double) _cachedKey;
    private byte[]? _cachedPixels;

    /// <summary>Premultiplied BGRA, black: only the alpha channel is set. Nothing is drawn inside the menu's own shape.</summary>
    private static byte[] Render(int width, int height, double centreX, double centreY, double halfW, double halfH, double corner,
                                 double circleX, double circleY, double circleRadius, double blur, double offset, double peak)
    {
        var pixels = new byte[width * height * 4];
        double bx = halfW - corner, by = halfH - corner;

        double Distance(double px, double py)
        {
            // Signed distance to the menu's shape: negative inside, positive outside.
            double qx = Math.Abs(px - centreX) - bx, qy = Math.Abs(py - centreY) - by;
            double panel = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0))
                           + Math.Min(Math.Max(qx, qy), 0) - corner;
            double dx = px - circleX, dy = py - circleY;
            double circle = Math.Sqrt(dx * dx + dy * dy) - circleRadius;
            return Math.Min(panel, circle);
        }

        for (int y = 0; y < height; y++)
        {
            double py = y + 0.5;
            for (int x = 0; x < width; x++)
            {
                double px = x + 0.5;
                if (Distance(px, py) <= 0) continue; // behind the menu: nothing, so the glass isn't darkened
                double t = Math.Clamp(Distance(px, py - offset) / blur, 0, 1); // the shadow is the shape moved down a little, then softened
                double fall = 1 - t * t * (3 - 2 * t);
                pixels[(y * width + x) * 4 + 3] = (byte)Math.Round(peak * fall * fall * 255);
            }
        }
        return pixels;
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        var instance = Native.GetModuleHandle(null);
        if (!_classRegistered)
        {
            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = instance,
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref cls) == 0) return;
            _classRegistered = true;
        }
        _hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            ClassName, null, WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
