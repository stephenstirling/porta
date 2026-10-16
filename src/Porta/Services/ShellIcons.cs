using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Porta.Services;

/// <summary>Gets the real Windows icon for an app, file or shortcut, the same one Explorer shows.</summary>
internal static class ShellIcons
{
    public sealed record Pixels(byte[] Bgra, int Width, int Height);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
        public uint c0, c1, c2, c3; // room for colour masks
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr h, int c, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int SIIGBF_ICONONLY = 0x4;

    /// <summary>Call from an STA thread. Returns null if Windows has no icon for the path.</summary>
    public static Pixels? GetPixels(string parsingName, int size)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0) return null;
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out hbmp);
            Marshal.ReleaseComObject(factory);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;

            GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm);
            int w = bm.bmWidth, h = bm.bmHeight;
            var info = new BITMAPINFO
            {
                biSize = 40, biWidth = w, biHeight = -h, // negative = top-down rows
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            var data = new byte[w * h * 4];
            var hdc = GetDC(IntPtr.Zero);
            GetDIBits(hdc, hbmp, 0, (uint)h, data, ref info, 0);
            ReleaseDC(IntPtr.Zero, hdc);

            // Some older icons come back with no alpha at all; make them opaque instead of invisible.
            bool anyAlpha = false;
            for (int i = 3; i < data.Length; i += 4) if (data[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < data.Length; i += 4) data[i] = 255;

            return new Pixels(data, w, h);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
        }
    }

    /// <summary>Call on the UI thread.</summary>
    public static WriteableBitmap ToBitmap(Pixels px)
    {
        var bitmap = new WriteableBitmap(px.Width, px.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(px.Bgra, 0, px.Bgra.Length);
        }
        bitmap.Invalidate();
        return bitmap;
    }
}
