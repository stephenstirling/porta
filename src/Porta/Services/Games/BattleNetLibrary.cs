using System.Text.RegularExpressions;
using Microsoft.Win32;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// Installed Blizzard / Battle.net games, from their Windows uninstall entries.
///
/// Battle.net keeps its own list in a binary file, so Porta reads the uninstall entries instead:
/// each game's uninstall command names its Battle.net product code (--productcode=wow), and
/// <c>Battle.net.exe --exec="launch &lt;code&gt;"</c> starts it through the launcher.
/// VERIFY on a PC with Battle.net games installed: product codes vary between games (wow, fen,
/// pro, d3 …); if one doesn't launch, Porta falls back to opening Battle.net.
/// </summary>
public static partial class BattleNetLibrary
{
    /// <summary>
    /// The Battle.net shop has no public search page (every /search URL tried returns 404), so the
    /// "Search stores" link opens the shop home, which redirects to the visitor's region.
    /// </summary>
    public const string ShopUrl = "https://shop.battle.net/";

    public static string? LauncherPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net", "Battle.net.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battle.net", "Battle.net.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static List<GameEntry> Scan()
    {
        var launcher = LauncherPath();
        if (launcher is null) return [];

        var games = new List<GameEntry>();
        foreach (var (name, uninstall, icon) in UninstallEntries())
        {
            var code = ProductCode().Match(uninstall);
            games.Add(new GameEntry
            {
                Name = name,
                Launcher = GameLauncher.BattleNet,
                Installed = true,
                Target = launcher,
                Arguments = code.Success ? $"--exec=\"launch {code.Groups[1].Value}\"" : null,
                IconSource = icon,
                LauncherId = code.Success ? code.Groups[1].Value : null,
            });
        }
        return games.DistinctBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<(string Name, string Uninstall, string? Icon)> UninstallEntries()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("Publisher") is not string publisher || !publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.GetValue("DisplayName") is not string name || name.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
                var uninstall = k.GetValue("UninstallString") as string ?? "";
                var icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                yield return (name, uninstall, icon is not null && File.Exists(icon) ? icon : null);
            }
        }
    }

    [GeneratedRegex(@"--productcode=([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProductCode();
}
