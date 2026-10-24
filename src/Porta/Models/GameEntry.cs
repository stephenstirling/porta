using Microsoft.UI.Xaml;

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
    public string? CoverPath
    {
        get => _coverPath;
        set
        {
            _coverPath = value;
            OnChanged(nameof(CoverPath));
            OnChanged(nameof(WideArtVisibility));
            OnChanged(nameof(IconTileVisibility));
        }
    }
    private string? _coverPath;

    /// <summary>Rows with a wide cover show it filling the whole box; rows with only a small icon keep the icon tile.</summary>
    public Visibility PictureArtVisibility => _iconFromWikipedia || _pictureLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WideArtVisibility => _coverPath is not null && !(_iconFromWikipedia || _pictureLoading) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconTileVisibility => _coverPath is null && !(_iconFromWikipedia || _pictureLoading) ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>English Wikipedia article whose lead picture a Battle.net row may show when the game has no icon of its own.</summary>
    public string? WikipediaTitle { get; init; }
    /// <summary>The picture has been looked up (found or not), so it isn't asked for again while the row lives.</summary>
    public bool WikipediaPictureTried { get; set; }

    private bool _iconFromWikipedia;
    private bool _pictureLoading;

    /// <summary>The row's picture came from Wikipedia, so it can be removed again when the switch is turned off.</summary>
    public bool IconFromWikipedia
    {
        get => _iconFromWikipedia;
        set { _iconFromWikipedia = value; PictureStateChanged(); }
    }

    /// <summary>A Wikipedia picture is being fetched: the row shows nothing behind it instead of the gray letter tile.</summary>
    public bool PictureLoading
    {
        get => _pictureLoading;
        set { _pictureLoading = value; PictureStateChanged(); }
    }

    private double _pictureShiftY;

    /// <summary>
    /// How far, as a fraction of the box height, the Wikipedia picture is moved up so a tall cover is cropped from a
    /// little above its centre rather than its middle (set once the picture's real shape is known).
    /// </summary>
    public double PictureShiftY
    {
        get => _pictureShiftY;
        set { _pictureShiftY = value; OnChanged(nameof(PictureShiftY)); }
    }

    /// <summary>
    /// The shift for a picture of this width and height in the 92 x 43 box: none for a picture as wide as the box or wider
    /// (it is only trimmed at the sides); for a taller one the window starts 30% of the way down the part that doesn't
    /// fit, which is above the centre (50%) and below the very top (0%), so faces near the top of a cover aren't cut off.
    /// </summary>
    public static double ShiftFor(double pixelWidth, double pixelHeight)
    {
        const double boxWidth = 92, boxHeight = 43, bias = 0.30;
        if (pixelWidth <= 0 || pixelHeight <= 0) return 0;
        var scaledHeight = boxWidth * pixelHeight / pixelWidth; // the picture scaled to fill the box width
        var hidden = scaledHeight - boxHeight;
        return hidden <= 0 ? 0 : -(hidden * bias) / boxHeight;
    }

    private void PictureStateChanged()
    {
        OnChanged(nameof(PictureArtVisibility));
        OnChanged(nameof(WideArtVisibility));
        OnChanged(nameof(IconTileVisibility));
    }

    /// <summary>From Porta's Battle.net list (Assets\battlenet-games.json), installed or not.</summary>
    public bool FromBattleNetCatalog { get; init; }

    public string Subtitle => FromCatalogCache ? "Epic"
        : FromBattleNetCatalog ? (Installed ? "Battle.net · Installed" : "Battle.net · Not installed")
        : Installed ? $"{LauncherName} · Installed" : $"{LauncherName} store · Not installed";

    public string ActionText => FromCatalogCache ? "Open" : Installed ? "Play" : "View";

    /// <summary>The small launcher badge shown beside the label (Epic only, taken from the installed launcher).</summary>
    public IconItem? Badge => Launcher switch
    {
        GameLauncher.Epic => Services.Games.EpicBadge.Instance,
        GameLauncher.BattleNet => Services.Games.BattleNetBadge.Instance,
        _ => null,
    };

    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
}
