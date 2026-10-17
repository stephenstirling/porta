using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
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

    /// <summary>
    /// Steam's cached header image. The folder layout has changed over time: flat files, then a folder
    /// per game, and now most images sit one level deeper in hash-named subfolders as library_header.jpg.
    /// </summary>
    private static string? Cover(string steam, string id)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        string[] candidates =
        [
            Path.Combine(cache, $"{id}_header.jpg"),
            Path.Combine(cache, id, "header.jpg"),
            Path.Combine(cache, $"{id}_library_hero.jpg"),
        ];
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;

        var folder = Path.Combine(cache, id);
        if (!Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "library_header.jpg", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
        var candidates = (result?.Items ?? [])
            .Where(i => i.Type == "app" && !installedIds.Contains(i.Id.ToString()))
            .Take(6) // extra, so a few can be dropped as adult and still leave 3
            .ToList();
        var adult = await Task.WhenAll(candidates.Select(i => IsAdultAsync(i.Id, ct))).ConfigureAwait(false);
        return candidates
            .Where((_, index) => !adult[index])
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

    // Steam content descriptors: 3 adult only sexual content, 4 frequent nudity or sexual content.
    // (1 some nudity, 2 violence/gore and 5 general mature are allowed.)
    private static readonly int[] AdultDescriptors = [3, 4];
    private static readonly ConcurrentDictionary<long, bool> AdultCache = new();

    /// <summary>
    /// Whether a store game has adult-only or frequent nudity content descriptors. The quick store search doesn't say,
    /// so this asks Steam's app details. If Steam can't be reached, the game is treated as adult and hidden.
    /// </summary>
    private static async Task<bool> IsAdultAsync(long id, CancellationToken ct)
    {
        if (AdultCache.TryGetValue(id, out var cached)) return cached;
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={id}&filters=content_descriptors";
            using var doc = await JsonDocument.ParseAsync(
                await Http.GetStreamAsync(url, ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty(id.ToString(), out var app) ||
                !app.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
                return true; // not returned for this region or anonymous visitors: don't show it

            bool adult = false;
            if (app.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("content_descriptors", out var cd) &&
                cd.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                adult = ids.EnumerateArray().Any(x => x.TryGetInt32(out var n) && AdultDescriptors.Contains(n));
            }
            AdultCache[id] = adult;
            return adult;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return true;
        }
    }

    private sealed record StoreSearch([property: JsonPropertyName("items")] List<StoreItem>? Items);

    private sealed record StoreItem(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("tiny_image")] string? TinyImage);
}
