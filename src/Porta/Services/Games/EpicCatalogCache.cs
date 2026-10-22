using System.Text;
using System.Text.Json;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// Epic games from the Epic Games Launcher's local catalog cache
/// (C:\ProgramData\Epic\EpicGamesLauncher\Data\Catalog\catcache.bin: base64-encoded JSON).
///
/// Read-only and offline: the file is opened for reading only, never written, and nothing goes
/// online here. The cache holds cover pictures as web links; <see cref="CoverUrlFor"/> hands those out
/// and EpicCoverCache downloads only the ones being shown. No login, token or account file is touched. The cache can list games the user doesn't own, so results are labelled just "Epic".
/// Only base games are kept: Epic's own category fields mark add-ons, engines, tools and extras.
/// </summary>
public static class EpicCatalogCache
{
    private static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");

    /// <summary>Top-level categories that mean "not a base game".</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "addons", "engines", "software", "digitalextras", "audience", "subscription", "hidden", "appproxy", "developer",
    };

    private static readonly object Gate = new();
    private static List<GameEntry> _games = [];
    private static Dictionary<string, string> _coverUrls = new(StringComparer.OrdinalIgnoreCase);
    private static (DateTime Written, long Length) _loadedVersion;
    private static Task? _loading;

    /// <summary>
    /// The current list. Checks the file's timestamp and size (cheap) and reloads in the background when
    /// it changed; until then the previous list is returned. Missing or unreadable file: empty list.
    /// </summary>
    public static IReadOnlyList<GameEntry> Games
    {
        get
        {
            RefreshIfChanged();
            lock (Gate) return _games;
        }
    }

    /// <summary>The wide cover link for a catalog item id (installed games use this too), or null.</summary>
    public static string? CoverUrlFor(string? catalogItemId)
    {
        if (catalogItemId is null) return null;
        RefreshIfChanged();
        lock (Gate) return _coverUrls.GetValueOrDefault(catalogItemId);
    }

    public static void RefreshIfChanged()
    {
        (DateTime, long) version;
        try
        {
            var info = new FileInfo(CacheFile);
            if (!info.Exists) { lock (Gate) { _games = []; _loadedVersion = default; } return; }
            version = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        lock (Gate)
        {
            if (version == _loadedVersion || _loading is { IsCompleted: false }) return;
            _loading = Task.Run(() => Load(version));
        }
    }

    private static void Load((DateTime Written, long Length) version)
    {
        try
        {
            string text;
            using (var stream = new FileStream(CacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                text = reader.ReadToEnd();

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim()));
            using var doc = JsonDocument.Parse(json);
            var games = new List<GameEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var covers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!IsBaseGame(item)) continue;
                var title = Str(item, "title");
                var ns = Str(item, "namespace");
                var id = Str(item, "id");
                var appId = AppId(item);
                if (title is null || ns is null || id is null || appId is null || !seen.Add(title)) continue;

                var cover = WideCoverUrl(item);
                if (cover is not null) covers[id] = cover;

                games.Add(new GameEntry
                {
                    Name = title,
                    Launcher = GameLauncher.Epic,
                    Installed = false,
                    FromCatalogCache = true,
                    // The same link Epic's own desktop shortcuts use: the launcher opens the game and offers
                    // to install it (or launches it if it's installed).
                    Target = $"com.epicgames.launcher://apps/{ns}%3A{id}%3A{appId}?action=launch",
                    LauncherId = appId,
                    CatalogNamespace = ns,
                    CatalogItemId = id,
                    CoverUrl = cover,
                });
            }
            lock (Gate) { _games = games; _coverUrls = covers; _loadedVersion = version; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException or InvalidOperationException)
        {
            // Mid-write by the launcher, or a format change: keep the previous list and try again later.
            System.Diagnostics.Debug.WriteLine("Porta: couldn't read the Epic catalog cache: " + ex.Message);
        }
    }

    /// <summary>In the "games" category, no category marking it as an add-on/engine/tool/extra, and not tied to another game.</summary>
    private static bool IsBaseGame(JsonElement item)
    {
        if (!item.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Array) return false;
        bool game = false;
        foreach (var c in cats.EnumerateArray())
        {
            var path = Str(c, "path");
            if (path is null) continue;
            if (NotGames.Contains(path.Split('/')[0])) return false;
            if (path.Equals("games", StringComparison.OrdinalIgnoreCase)) game = true;
        }
        if (!game) return false;

        // DLC points at the game it belongs to; base games leave this empty (or point at themselves).
        if (item.TryGetProperty("mainGameItem", out var main) && main.ValueKind == JsonValueKind.Object &&
            Str(main, "id") is { Length: > 0 } mainId && mainId != Str(item, "id"))
            return false;
        return true;
    }

    /// <summary>
    /// The picture that fits Porta's wide 92x43 tile: Epic's wide store art first, then the landscape game box
    /// (most games only have this one). Portrait pictures are never used.
    /// </summary>
    private static string? WideCoverUrl(JsonElement item)
    {
        if (!item.TryGetProperty("keyImages", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        foreach (var wanted in new[] { "DieselStoreFrontWide", "OfferImageWide", "Thumbnail", "DieselGameBox" })
        {
            foreach (var image in images.EnumerateArray())
            {
                if (Str(image, "type") != wanted) continue;
                if (image.TryGetProperty("width", out var w) && image.TryGetProperty("height", out var h) &&
                    w.TryGetInt32(out var width) && h.TryGetInt32(out var height) && width <= height) continue;
                if (Uri.TryCreate(Str(image, "url"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    return uri.ToString();
            }
        }
        return null;
    }

    private static string? AppId(JsonElement item)
    {
        if (!item.TryGetProperty("releaseInfo", out var releases) || releases.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in releases.EnumerateArray())
            if (Str(r, "appId") is { Length: > 0 } appId) return appId;
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
