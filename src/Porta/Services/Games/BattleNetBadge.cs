using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// The small Battle.net badge on Battle.net rows: the icon of the Battle.net client installed on this PC, read
/// when Porta runs. Porta ships no Blizzard logo; without the client the badge is empty and only the text shows.
/// </summary>
public sealed class BattleNetBadge : IconItem
{
    public static BattleNetBadge Instance { get; } = Create();

    private static BattleNetBadge Create()
    {
        var badge = new BattleNetBadge();
        var exe = BattleNetLibrary.LauncherPath();
        if (exe is not null) App.Icons.Request(badge, exe, 32);
        return badge;
    }
}
