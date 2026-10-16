using System.Text;
using System.Xml.Linq;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// PC games installed through the Xbox app (including Game Pass). They're Store packages, so they
/// already appear in the Apps folder; this finds which of those are games by reading the Xbox
/// app's game folders, so Porta can show them in the Games section with the Xbox label.
///
/// Each drive that holds Xbox games has a hidden ".GamingRoot" file naming the games folder
/// (usually "XboxGames"). Each game has Content\MicrosoftGame.config with its package identity.
/// </summary>
public static class XboxLibrary
{
    public static List<GameEntry> Scan(IReadOnlyList<AppEntry> apps)
    {
        var games = new List<GameEntry>();
        foreach (var root in GameRoots())
        {
            foreach (var folder in SafeDirectories(root))
            {
                var config = Path.Combine(folder, "Content", "MicrosoftGame.config");
                if (!File.Exists(config)) continue;
                try
                {
                    var xml = XDocument.Load(config);
                    var identity = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
                    if (identity is null) continue;

                    // Store app ids look like "<IdentityName>_<publisher hash>!<AppId>".
                    var app = apps.FirstOrDefault(a => a.Id.StartsWith(identity + "_", StringComparison.OrdinalIgnoreCase));
                    if (app is null) continue;

                    games.Add(new GameEntry
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
        }
        return games;
    }

    /// <summary>Microsoft Store search, which covers Xbox PC and Game Pass games.</summary>
    public static string StoreSearchUrl(string term) => $"ms-windows-store://search/?query={Uri.EscapeDataString(term)}";

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
