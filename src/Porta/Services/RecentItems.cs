using System.Diagnostics;
using System.Runtime.InteropServices;
using Porta.Models;

namespace Porta.Services;

/// <summary>
/// "Recent files and folders" for search: the targets of the shortcuts in the Windows Recent folder, the
/// well-known folders (Documents, Downloads, Pictures, Videos, Music, Desktop) and the folders pinned in
/// Explorer's Quick access. Matched by name only. Read-only and local: no file is ever opened or read (only the
/// small shortcut files in Recent are), and nothing is sent anywhere. The list is kept in memory and rebuilt in the
/// background shortly after the Recent folder changes. Targets on network shares or unreachable drives are left out.
/// </summary>
public static class RecentItems
{
    private const int MaxRecent = 400;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);

    private static readonly object Gate = new();
    private static List<PathEntry> _entries = [];
    private static List<(string Name, string Path)> _pinned = [];
    private static bool _pinnedDirty = true;
    private static readonly Dictionary<string, (DateTime Written, string? Target)> LinkTargets = new(StringComparer.OrdinalIgnoreCase);

    private static FileSystemWatcher? _watcher;
    private static Timer? _timer;
    private static bool _enabled;
    private static bool _loading;
    private static bool _loadAgain;

    /// <summary>Raised on a background thread after the list was rebuilt.</summary>
    public static event Action? Loaded;

    /// <summary>Starts watching the Recent folder and builds the list in the background. Safe to call twice.</summary>
    public static void Enable()
    {
        lock (Gate)
        {
            if (_enabled) return;
            _enabled = true;
            _pinnedDirty = true;
        }
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (Directory.Exists(folder))
            {
                _watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true, // Quick access pins live in Recent\AutomaticDestinations
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = false,
                };
                _watcher.Created += OnRecentChanged;
                _watcher.Changed += OnRecentChanged;
                _watcher.Deleted += OnRecentChanged;
                _watcher.Renamed += (s, e) => OnRecentChanged(s, e);
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Debug.WriteLine("Porta: can't watch the Recent folder: " + ex.Message); // still works, refreshed on each switch-on
        }
        _timer = new Timer(_ => StartLoad(), null, Timeout.Infinite, Timeout.Infinite);
        StartLoad();
    }

    /// <summary>Stops watching and forgets everything, for when the switch is turned off.</summary>
    public static void Disable()
    {
        lock (Gate)
        {
            _enabled = false;
            _entries = [];
            _pinned = [];
            _pinnedDirty = true;
            LinkTargets.Clear();
        }
        _watcher?.Dispose();
        _watcher = null;
        _timer?.Dispose();
        _timer = null;
    }

    private static void OnRecentChanged(object? sender, FileSystemEventArgs e)
    {
        if (e.FullPath.Contains("AutomaticDestinations", StringComparison.OrdinalIgnoreCase)) _pinnedDirty = true;
        try { _timer?.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private static void StartLoad()
    {
        lock (Gate)
        {
            if (!_enabled) return;
            if (_loading) { _loadAgain = true; return; }
            _loading = true;
        }
        _ = Sta.Run(Build).ContinueWith(t =>
        {
            bool again;
            lock (Gate)
            {
                _loading = false;
                again = _loadAgain && _enabled;
                _loadAgain = false;
                if (_enabled && t.IsCompletedSuccessfully) _entries = t.Result;
            }
            if (t.IsFaulted) Debug.WriteLine("Porta: couldn't read recent files: " + t.Exception?.GetBaseException().Message);
            else if (_enabled) Loaded?.Invoke();
            if (again) StartLoad();
        });
    }

    /// <summary>
    /// Up to <paramref name="max"/> files and folders whose name matches, best first, never anything inside an
    /// excluded folder, and only ones that still exist. Uses the list as last built.
    /// </summary>
    public static List<PathEntry> Search(string query, IReadOnlyCollection<string> excludedFolders, int max = 4)
    {
        List<PathEntry> entries;
        lock (Gate) entries = _entries;
        var excluded = excludedFolders.Select(NormalizeFolder).OfType<string>().ToList();

        var results = new List<PathEntry>();
        foreach (var (entry, _) in entries
                     .Select(e => (Entry: e, Score: Math.Max(
                         SearchService.Score(e.Name, query),
                         SearchService.Score(e.IsFolder ? e.Name : Path.GetFileNameWithoutExtension(e.Name), query))))
                     .Where(x => x.Score > 0)
                     .OrderByDescending(x => x.Score)
                     .ThenBy(x => x.Entry.Order))
        {
            if (IsExcluded(entry.FullPath, excluded) || !StillThere(entry)) continue;
            results.Add(entry);
            if (!entry.IconRequested)
            {
                entry.IconRequested = true;
                App.Icons.Request(entry, entry.FullPath, 64);
            }
            if (results.Count == max) break;
        }
        return results;
    }

    /// <summary>True for a path that is a listed folder or inside one. Compared without regard to case.</summary>
    public static bool IsExcluded(string path, IEnumerable<string> normalizedExcluded)
    {
        var p = NormalizeFolder(path);
        if (p is null) return false;
        foreach (var e in normalizedExcluded)
            if (p.Equals(e, StringComparison.OrdinalIgnoreCase) || p.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A full path with variables expanded and no trailing slash ("D:\" becomes "D:"), or null if it isn't a usable path.</summary>
    public static string? NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            return full.TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // A last check at search time that a local target hasn't been deleted since the list was built.
    private static bool StillThere(PathEntry entry)
    {
        try
        {
            if (!IsLocalDrive(entry.FullPath)) return true; // drives were checked when the list was built; don't wait on a network again
            return entry.IsFolder ? Directory.Exists(entry.FullPath) : File.Exists(entry.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsLocalDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && !root.StartsWith(@"\\") &&
                   new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return false; }
    }

    // ---------- Building the list (on a background STA thread: the shell COM objects need one) ----------

    private static List<PathEntry> Build()
    {
        var found = new Dictionary<string, PathEntry>(StringComparer.OrdinalIgnoreCase);
        var drives = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // 1. The well-known folders.
        foreach (var (name, path) in WellKnownFolders())
            AddFolder(found, drives, name, path, -2);

        // 2. Folders pinned in Quick access (re-read only when the pins may have changed).
        List<(string Name, string Path)> pinned;
        bool dirty;
        lock (Gate) { pinned = _pinned; dirty = _pinnedDirty; }
        if (dirty)
        {
            pinned = ReadPinnedFolders();
            lock (Gate) { _pinned = pinned; _pinnedDirty = false; }
        }
        foreach (var (name, path) in pinned)
            AddFolder(found, drives, name, path, -1);

        // 3. Recent files and folders, newest first.
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (Directory.Exists(folder))
        {
            int order = 0;
            var links = new DirectoryInfo(folder).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxRecent).ToList();
            var current = new HashSet<string>(links.Select(l => l.FullName), StringComparer.OrdinalIgnoreCase);
            lock (Gate) foreach (var stale in LinkTargets.Keys.Where(k => !current.Contains(k)).ToList()) LinkTargets.Remove(stale);

            foreach (var link in links)
            {
                var target = TargetOf(link);
                if (target is null || !Path.IsPathRooted(target) || target.StartsWith(@"\\") || found.ContainsKey(target)) continue;
                if (!DriveUsable(target, drives)) continue;
                try
                {
                    if (Directory.Exists(target)) AddFolder(found, drives, Path.GetFileName(target.TrimEnd('\\')) is { Length: > 0 } n ? n : target, target, order++);
                    else if (File.Exists(target)) found[target] = new PathEntry { Name = Path.GetFileName(target), FullPath = target, IsFolder = false, Order = order++ };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return [.. found.Values];
    }

    /// <summary>The target of a Recent shortcut, remembered until the shortcut changes. Web, app and other non-file links give null.</summary>
    private static string? TargetOf(FileInfo link)
    {
        lock (Gate)
        {
            if (LinkTargets.TryGetValue(link.FullName, out var known) && known.Written == link.LastWriteTimeUtc) return known.Target;
        }
        string? target = null;
        var name = Path.GetFileNameWithoutExtension(link.Name);
        if (!name.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            target = RecentFiles.TargetPath(link.FullName);
            if (string.IsNullOrWhiteSpace(target) || target.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) target = null;
        }
        lock (Gate) LinkTargets[link.FullName] = (link.LastWriteTimeUtc, target);
        return target;
    }

    private static void AddFolder(Dictionary<string, PathEntry> found, Dictionary<string, bool> drives, string name, string path, int order)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith(@"\\")) return;
            var key = path.Length > 3 ? path.TrimEnd('\\') : path;
            if (found.ContainsKey(key) || !DriveUsable(key, drives) || !Directory.Exists(key)) return;
            found[key] = new PathEntry { Name = name, FullPath = key, IsFolder = true, Order = order };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    /// <summary>
    /// Whether the path's drive can be used: local drives yes if present; a mapped network drive only if it answers
    /// within a second (an asleep NAS must not stall search). Network shares (\\server\share) are never used.
    /// </summary>
    private static bool DriveUsable(string path, Dictionary<string, bool> cache)
    {
        string? root;
        try { root = Path.GetPathRoot(path); }
        catch (ArgumentException) { return false; }
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return false;
        if (cache.TryGetValue(root, out var known)) return known;

        bool ok;
        try
        {
            ok = new DriveInfo(root).DriveType switch
            {
                DriveType.Fixed or DriveType.Removable => Directory.Exists(root),
                DriveType.Network => AnswersQuickly(root),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { ok = false; }
        cache[root] = ok;
        return ok;
    }

    private static bool AnswersQuickly(string root)
    {
        var check = Task.Run(() => Directory.Exists(root));
        return check.Wait(TimeSpan.FromSeconds(1)) && check.Result;
    }

    private static IEnumerable<(string Name, string Path)> WellKnownFolders()
    {
        yield return ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (DownloadsFolder() is { } downloads) yield return ("Downloads", downloads);
        yield return ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        yield return ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        yield return ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        yield return ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? DownloadsFolder()
    {
        if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>
    /// Folders the user pinned to Explorer's Quick access: the shell marks them with System.Home.IsPinned, which does
    /// not depend on the Windows language. Frequent (unpinned) items are ignored.
    /// </summary>
    private static List<(string Name, string Path)> ReadPinnedFolders()
    {
        var pinned = new List<(string, string)>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return pinned;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic? quickAccess = shell.Namespace("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}");
                if (quickAccess is null) return pinned;
                dynamic items = quickAccess.Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    try
                    {
                        if (!Convert.ToBoolean(item.ExtendedProperty("System.Home.IsPinned")) || !Convert.ToBoolean(item.IsFolder)) continue;
                        string path = item.Path;
                        string name = item.Name;
                        if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)) pinned.Add((name, path));
                    }
                    catch (Exception ex) when (ex is COMException or FormatException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    finally { if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
                }
            }
            finally { if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            Debug.WriteLine("Porta: couldn't read Quick access: " + ex.Message);
        }
        return pinned;
    }
}
