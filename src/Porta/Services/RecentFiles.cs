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
            .Take(count)
            .Select(f => new RecentItem
            {
                Name = Path.GetFileNameWithoutExtension(f.Name),
                LinkPath = f.FullName,
                When = Ago(f.LastWriteTime),
            })
            .ToList();
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
