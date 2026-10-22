namespace Porta.Models;

public enum GameLauncher { Steam, Epic, BattleNet, Xbox }

/// <summary>A game from one of the PC launchers, installed or from a store search.</summary>
public sealed class GameEntry : IconItem
{
    public required string Name { get; init; }
    public required GameLauncher Launcher { get; init; }
    public required bool Installed { get; init; }
    public required string Target { get; init; }
    public string? Arguments { get; init; }
    /// <summary>Wide cover art on disk or on the web, if the launcher has one.</summary>
    public string? CoverPath { get; set; }
    /// <summary>Where to load a square icon from when there's no cover (exe, shell path).</summary>
    public string? IconSource { get; init; }
    public string? LauncherId { get; init; }
    /// <summary>Epic catalog namespace.</summary>
    public string? CatalogNamespace { get; init; }
    /// <summary>Epic catalog item identifier, used to find the game's cover link in the catalog cache.</summary>
    public string? CatalogItemId { get; init; }
    /// <summary>Web link to the wide cover picture, from Epic's local catalog cache. Not downloaded unless shown.</summary>
    public string? CoverUrl { get; set; }

    public string LauncherName => Launcher switch
    {
        GameLauncher.Steam => "Steam",
        GameLauncher.Epic => "Epic Games",
        GameLauncher.BattleNet => "Battle.net",
        GameLauncher.Xbox => "Xbox",
        _ => "",
    };

    /// <summary>From Epic's local catalog cache: may not be owned, so labelled just "Epic" and opened in the launcher.</summary>
    public bool FromCatalogCache { get; init; }

    public string Subtitle => FromCatalogCache ? "Epic"
        : Installed ? $"{LauncherName} · Installed" : $"{LauncherName} store · Not installed";

    public string ActionText => FromCatalogCache ? "Open" : Installed ? "Play" : "View";

    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
}
