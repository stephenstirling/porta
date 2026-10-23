using System.Diagnostics;
using System.Text.Json;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>One Battle.net game from Assets\battlenet-games.json.</summary>
/// <param name="Code">The code battlenet://&lt;code&gt; launches through the Battle.net client.</param>
/// <param name="AlsoCodes">Other spellings of the same product that the client's own files may use (matching only).</param>
public sealed record BattleNetTitle(string Name, string[] Aliases, string Code, string[] AlsoCodes, string StoreUrl);

/// <summary>
/// Battle.net games in search, from the editable list Assets\battlenet-games.json (no rebuild needed to change
/// it). Which ones are installed comes from the existing local detection (<see cref="BattleNetLibrary"/>);
/// nothing goes online and no pictures are downloaded: an installed game shows its own icon, the rest a letter tile.
/// </summary>
public static class BattleNetCatalog
{
    public static IReadOnlyList<BattleNetTitle> Titles { get; } = Load();

    private static object? _rowsFor;
    private static string? _rowsClient;
    private static readonly Dictionary<string, GameEntry> Rows = [];

    /// <summary>
    /// Up to <paramref name="max"/> catalog games matching the query by name or alias, best first. Games already
    /// shown as installed games are left out so nothing appears twice. With no Battle.net client on the PC every
    /// row is "Not installed" with View only.
    /// </summary>
    public static List<GameEntry> Search(string query, IReadOnlyList<GameEntry> detected, IEnumerable<GameEntry> alreadyShown, int max = 4)
    {
        var client = BattleNetLibrary.LauncherPath();
        var installed = client is null
            ? []
            : detected.Where(g => g.Launcher == GameLauncher.BattleNet && g.Installed).ToList();
        var shown = alreadyShown.Where(g => g.Launcher == GameLauncher.BattleNet).ToList();

        var results = new List<(BattleNetTitle Title, int Score)>();
        foreach (var title in Titles)
        {
            var score = Math.Max(
                SearchService.Score(title.Name, query),
                title.Aliases.Select(a => SearchService.Score(a, query)).DefaultIfEmpty(0).Max());
            if (score > 0) results.Add((title, score));
        }

        var rows = new List<GameEntry>();
        foreach (var (title, _) in results.OrderByDescending(r => r.Score).ThenBy(r => r.Title.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var match = installed.FirstOrDefault(g => IsSame(title, g));
            if (match is not null && shown.Any(g => IsSame(title, g))) continue; // already in the Games list above
            rows.Add(RowFor(title, match, client, detected));
            if (rows.Count == max) break;
        }
        return rows;
    }

    private static bool IsSame(BattleNetTitle title, GameEntry game) =>
        game.Name.Equals(title.Name, StringComparison.OrdinalIgnoreCase) ||
        title.Aliases.Any(a => game.Name.Equals(a, StringComparison.OrdinalIgnoreCase)) || // the client may just call it "Overwatch"
        (game.LauncherId is { } id &&
         (id.Equals(title.Code, StringComparison.OrdinalIgnoreCase) ||
          title.AlsoCodes.Any(c => c.Equals(id, StringComparison.OrdinalIgnoreCase))));

    /// <summary>One row per game, kept between searches so icons aren't reloaded on every keystroke.</summary>
    private static GameEntry RowFor(BattleNetTitle title, GameEntry? installed, string? client, IReadOnlyList<GameEntry> detected)
    {
        if (!ReferenceEquals(_rowsFor, detected) || _rowsClient != client)
        {
            Rows.Clear(); // a new scan or the client appeared/went away
            _rowsFor = detected;
            _rowsClient = client;
        }
        if (Rows.TryGetValue(title.Code, out var known)) return known;

        var row = new GameEntry
        {
            Name = title.Name,
            Launcher = GameLauncher.BattleNet,
            Installed = installed is not null,
            FromBattleNetCatalog = true,
            // Installed: start it through the Battle.net client. Not installed: its page on blizzard.com.
            Target = installed is not null ? $"battlenet://{Uri.EscapeDataString(title.Code)}" : title.StoreUrl,
            LauncherId = title.Code,
            IconSource = installed?.IconSource,
        };
        if (row.IconSource is not null) App.Icons.Request(row, row.IconSource, 64);
        Rows[title.Code] = row;
        return row;
    }

    private static List<BattleNetTitle> Load()
    {
        var titles = new List<BattleNetTitle>();
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Assets", "battlenet-games.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var g in doc.RootElement.GetProperty("games").EnumerateArray())
            {
                var name = Str(g, "name");
                var code = Str(g, "code");
                var store = Str(g, "store");
                // The store page must be on blizzard.com or battle.net (https only).
                if (name is null || code is null || store is null || !IsAllowedStore(store) ||
                    code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))) continue;
                titles.Add(new BattleNetTitle(name, Strings(g, "aliases"), code, Strings(g, "alsoCodes"), store));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Debug.WriteLine("Porta: couldn't read battlenet-games.json: " + ex.Message); // no file or a broken one: no Battle.net games
        }
        return titles;
    }

    private static bool IsAllowedStore(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("blizzard.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".blizzard.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("battle.net", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".battle.net", StringComparison.OrdinalIgnoreCase));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static string[] Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0).ToArray()
            : [];
}
