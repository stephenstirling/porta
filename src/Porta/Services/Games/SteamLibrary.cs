using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// Installed Steam games, read from Steam's own files (works offline, finds every installed game,
/// even ones without a Start menu shortcut), plus optional online store search.
/// </summary>
public static partial class SteamLibrary
{
    // Steam tools that aren't games.
    private static readonly HashSet<string> SkipIds = ["228980", "1070560", "1391110", "1628350", "1493710", "2180100"];

    public static string? SteamPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var path = key?.GetValue("SteamPath") as string;
        return path is not null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    public static List<GameEntry> Scan()
    {
        var steam = SteamPath();
        if (steam is null) return [];

        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in PathLine().Matches(File.ReadAllText(vdf)))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }

        var games = new List<GameEntry>();
        var seen = new HashSet<string>();
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var id = Field(text, "appid");
                    var name = Field(text, "name");
                    if (id is null || name is null || SkipIds.Contains(id) || !seen.Add(id)) continue;
                    if (name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)) continue;

                    games.Add(new GameEntry
                    {
                        Name = name,
                        Launcher = GameLauncher.Steam,
                        Installed = true,
                        Target = $"steam://rungameid/{id}",
                        CoverPath = Cover(steam, id),
                        LauncherId = id,
                    });
                }
                catch (IOException) { /* Steam is writing it; next scan */ }
            }
        }
        return games;
    }

    /// <summary>Steam's cached header image. The folder layout changed in 2024, so try both.</summary>
    private static string? Cover(string steam, string id)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        string[] candidates =
        [
            Path.Combine(cache, $"{id}_header.jpg"),
            Path.Combine(cache, id, "header.jpg"),
            Path.Combine(cache, $"{id}_library_hero.jpg"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? Field(string acf, string key)
    {
        var m = Regex.Match(acf, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathLine();

    // ---------- Store search (online; the user can switch it off) ----------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public static async Task<List<GameEntry>> SearchStoreAsync(string term, ISet<string> installedIds, CancellationToken ct)
    {
        var cc = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc={cc}";
        var result = await Http.GetFromJsonAsync<StoreSearch>(url, ct).ConfigureAwait(false);
        return (result?.Items ?? [])
            .Where(i => i.Type == "app" && !installedIds.Contains(i.Id.ToString()))
            .Take(3)
            .Select(i => new GameEntry
            {
                Name = i.Name,
                Launcher = GameLauncher.Steam,
                Installed = false,
                Target = $"steam://store/{i.Id}",
                CoverPath = i.TinyImage,
                LauncherId = i.Id.ToString(),
            })
            .ToList();
    }

    private sealed record StoreSearch([property: JsonPropertyName("items")] List<StoreItem>? Items);

    private sealed record StoreItem(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("tiny_image")] string? TinyImage);
}
