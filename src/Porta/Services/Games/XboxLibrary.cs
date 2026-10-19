using System.Text;
using System.Xml.Linq;
using Porta.Models;
using Windows.Management.Deployment;

namespace Porta.Services.Games;

/// <summary>
/// PC games installed through the Xbox app (including Game Pass). Xbox flat-file installs are
/// discovered through each drive's .GamingRoot marker. Packaged games are discovered through
/// Windows' registered package metadata, rather than crawling the protected WindowsApps folder.
/// </summary>
public static class XboxLibrary
{
    public static List<GameEntry> Scan(IReadOnlyList<AppEntry> apps)
    {
        var games = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);

        // Xbox flat-file installs, usually under XboxGames on one of the fixed drives.
        foreach (var root in GameRoots())
        {
            foreach (var folder in SafeDirectories(root))
                AddFromConfig(Path.Combine(folder, "Content", "MicrosoftGame.config"), apps, games);
        }

        // Some Store/Game Pass games are registered packages and do not live under XboxGames.
        // Ask Windows for package install locations instead of recursively crawling WindowsApps.
        ScanRegisteredPackages(apps, games);

        return games.Values.ToList();
    }

    /// <summary>Microsoft Store search, which covers Xbox PC and Game Pass games.</summary>
    public static string StoreSearchUrl(string term) => $"ms-windows-store://search/?query={Uri.EscapeDataString(term)}";

    private static void ScanRegisteredPackages(
        IReadOnlyList<AppEntry> apps,
        Dictionary<string, GameEntry> games)
    {
        try
        {
            var packageManager = new PackageManager();
            foreach (var package in packageManager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    var installPath = package.InstalledLocation.Path;
                    if (string.IsNullOrWhiteSpace(installPath)) continue;

                    // MicrosoftGame.config is commonly in Content for flat-file installs, but
                    // packaged titles can place it at the package root.
                    AddFromConfig(Path.Combine(installPath, "MicrosoftGame.config"), apps, games);
                    AddFromConfig(Path.Combine(installPath, "Content", "MicrosoftGame.config"), apps, games);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    // A package can be updating, removed, or inaccessible while the scan runs.
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Package enumeration is best-effort. Keep flat-file discovery working if Windows
            // denies access to package metadata on a particular machine.
        }
    }

    private static void AddFromConfig(
        string configPath,
        IReadOnlyList<AppEntry> apps,
        Dictionary<string, GameEntry> games)
    {
        if (!File.Exists(configPath)) return;

        try
        {
            var xml = XDocument.Load(configPath);
            var identity = xml.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Identity")?
                .Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(identity)) return;

            // AppsFolder IDs for Store packages generally begin with the package identity/family.
            var app = apps.FirstOrDefault(a =>
                a.Id.StartsWith(identity + "_", StringComparison.OrdinalIgnoreCase) ||
                a.Id.StartsWith(identity + "!", StringComparison.OrdinalIgnoreCase));
            if (app is null) return;

            games.TryAdd(app.Id, new GameEntry
            {
                Name = app.Name,
                Launcher = GameLauncher.Xbox,
                Installed = true,
                Target = app.ShellPath,
                IconSource = app.ShellPath,
                LauncherId = identity,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Game folders can be locked while installing or updating.
        }
    }

    private static IEnumerable<string> GameRoots()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
            var names = new List<string>();
            if (File.Exists(marker))
            {
                try
                {
                    // "RGBX", a 4-byte count, then null-terminated UTF-16 folder paths.
                    var bytes = File.ReadAllBytes(marker);
                    if (bytes.Length > 8)
                        names.AddRange(Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8)
                            .Split('\0', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            names.Add("XboxGames");

            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var full = Path.IsPathRooted(name) ? name : Path.Combine(drive.RootDirectory.FullName, name);
                if (Directory.Exists(full)) yield return full;
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
