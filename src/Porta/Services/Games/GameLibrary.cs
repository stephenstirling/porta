using System.Diagnostics;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// Every installed game from Steam, Epic, Battle.net and the Xbox app, for Porta's search.
/// Scanned in the background at startup and again when the menu opens if the last scan is
/// more than five minutes old. Reads only the launchers' own files on this PC.
/// </summary>
public sealed class GameLibrary
{
    private static readonly TimeSpan RescanAfter = TimeSpan.FromMinutes(5);

    private List<GameEntry> _games = [];
    private DateTime _scannedAt = DateTime.MinValue;
    private Task? _scan;

    public IReadOnlyList<GameEntry> Games => _games;
    public HashSet<GameLauncher> Launchers { get; } = [];

    public void RefreshIfStale(IReadOnlyList<AppEntry> apps)
    {
        if (DateTime.UtcNow - _scannedAt < RescanAfter || _scan is { IsCompleted: false }) return;
        _scan = Task.Run(() => Scan(apps.ToList()));
    }

    private void Scan(List<AppEntry> apps)
    {
        var found = new List<GameEntry>();
        var launchers = new HashSet<GameLauncher>();

        void From(GameLauncher launcher, Func<List<GameEntry>> scan, bool launcherPresent)
        {
            try
            {
                if (launcherPresent) launchers.Add(launcher);
                found.AddRange(scan());
            }
            catch (Exception ex)
            {
                // One launcher's odd files must never break search.
                Debug.WriteLine($"Porta: {launcher} scan failed: {ex.Message}");
            }
        }

        From(GameLauncher.Steam, SteamLibrary.Scan, SteamLibrary.SteamPath() is not null);
        From(GameLauncher.Epic, EpicLibrary.Scan, EpicLibrary.IsInstalled());
        From(GameLauncher.BattleNet, BattleNetLibrary.Scan, BattleNetLibrary.LauncherPath() is not null);
        From(GameLauncher.Xbox, () => XboxLibrary.Scan(apps), true);

        _games = found;
        lock (Launchers) { Launchers.Clear(); Launchers.UnionWith(launchers); }
        _scannedAt = DateTime.UtcNow;

        foreach (var game in found)
        {
            RequestIcon(game);
            if (game.Launcher == GameLauncher.Epic &&
                game.CatalogNamespace is not null && game.CatalogItemId is not null)
                EpicArtworkService.Request(game, game.CatalogNamespace, game.CatalogItemId);
        }
    }

    private static void RequestIcon(GameEntry game)
    {
        if (game.CoverPath is not null || game.IconSource is null) return;
        App.Icons.Request(game, game.IconSource, 64);
    }

    public List<GameEntry> Search(string query, int max = 3) => Rank(_games, query, max);

    /// <summary>Best matches by name or initials ("rdr2"); also used for Epic's catalog cache.</summary>
    public static List<GameEntry> Rank(IEnumerable<GameEntry> games, string query, int max = 3) =>
        games
            .Select(g => (Game: g, Score: Math.Max(SearchService.Score(g.Name, query), SearchService.Score(Acronym(g.Name), query))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => x.Game)
            .ToList();

    /// <summary>"Red Dead Redemption 2" → "rdr2", so short names people actually type still match.</summary>
    internal static string Acronym(string name)
    {
        var words = name.Split([' ', ':', '-', '–'], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select(w => char.IsDigit(w[0]) ? w : w[..1])).ToLowerInvariant();
    }

    public static void Launch(GameEntry game)
    {
        try
        {
            if (game.Arguments is not null)
                Process.Start(new ProcessStartInfo(game.Target, game.Arguments) { UseShellExecute = true });
            else
                Launcher.Open(game.Target);
        }
        catch (Exception) when (game.Launcher == GameLauncher.BattleNet)
        {
            // Unknown product code: at least open Battle.net.
            Launcher.Open(game.Target);
        }
    }
}
