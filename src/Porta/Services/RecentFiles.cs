using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Porta.Models;

namespace Porta.Services;

/// <summary>Recently opened files, from the same Recent folder Explorer's Quick access uses.</summary>
public static class RecentFiles
{
    public static List<RecentItem> Load(int count)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(folder)) return [];

        return Directory.EnumerateFiles(folder, "*.lnk")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTime)
            .Where(f => IsRealFileOrFolder(f))
            .Take(count)
            .Select(f => new RecentItem
            {
                Name = Path.GetFileNameWithoutExtension(f.Name),
                LinkPath = f.FullName,
                When = Ago(f.LastWriteTime),
            })
            .ToList();
    }

    /// <summary>
    /// Skips app and web links that Windows also drops in Recent (ms-gamingoverlay, ms-screenclip,
    /// .url files, other protocol shortcuts): only shortcuts to files and folders that exist are kept.
    /// </summary>
    private static bool IsRealFileOrFolder(FileInfo link)
    {
        var name = Path.GetFileNameWithoutExtension(link.Name);
        if (name.StartsWith("ms-", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return false;

        var target = TargetPath(link.FullName);
        if (string.IsNullOrEmpty(target)) return false; // protocol shortcuts have no file path
        if (target.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return false;
        if (target.StartsWith(@"\\")) return true;   // network: don't wait on a NAS that's asleep
        return File.Exists(target) || Directory.Exists(target);
    }

    private static string? TargetPath(string linkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(linkPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            Marshal.ReleaseComObject(link);
            return sb.ToString();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    // Only GetPath is called; it's the first method, so the rest of the interface can be left out.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
    }

    private static string Ago(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (time.Date == DateTime.Today) return $"{(int)span.TotalHours}h ago";
        if (time.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        if (span.TotalDays < 7) return time.ToString("dddd");
        return time.ToString("d MMM");
    }
}
