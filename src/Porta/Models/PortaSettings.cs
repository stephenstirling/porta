namespace Porta.Models;

/// <summary>Everything Porta saves. Stored as JSON in %LocalAppData%\Porta\settings.json.</summary>
public sealed class PortaSettings
{
    /// <summary>Settings layout version. 2: the menu got its new look (Recent, clock card and lock button became switches, glass default changed).</summary>
    public int Version { get; set; } = 2;
    public bool FirstRunDone { get; set; }

    // General
    public bool OpenWithWinKey { get; set; } = true;

    /// <summary>A click on the taskbar's Start button opens Porta instead of Windows' Start menu. When off, Porta doesn't watch the mouse at all.</summary>
    public bool OpenFromStartButton { get; set; } = true;
    /// <summary>The Recent section under the pinned apps. Off by default; there was no switch for it before, so older files are switched off when they are read (see SettingsStore.Migrate).</summary>
    public bool ShowRecent { get; set; } = false;

    /// <summary>The user's name in the right-hand list opens their folder.</summary>
    public bool ShowUserFolderLink { get; set; } = true;

    /// <summary>Control Panel in the right-hand list.</summary>
    public bool ShowControlPanel { get; set; } = true;

    /// <summary>The clock and date card at the bottom of the right-hand panel.</summary>
    public bool ShowClockCard { get; set; } = false;

    /// <summary>A lock button beside the power button. Lock is always in the power menu.</summary>
    public bool ShowLockButton { get; set; } = false;

    /// <summary>Hide the menu when focus moves to something else (another app, the desktop, the taskbar). When off, only Esc or the Win key closes it.</summary>
    public bool CloseWhenClickedElsewhere { get; set; } = true;

    /// <summary>Apps hidden from All apps (by app id). They still appear in search.</summary>
    public List<string> HiddenApps { get; set; } = [];

    // Search
    /// <summary>Show Steam store results for games that aren't installed (asks Steam online).</summary>
    public bool SearchSteamStore { get; set; } = true;

    /// <summary>Show Wikipedia articles for what's typed (asks Wikipedia online). When off, nothing is sent.</summary>
    public bool SearchWikipedia { get; set; } = true;

    /// <summary>Show games from the Epic launcher's local catalog cache (read on this PC only, never online).</summary>
    public bool SearchEpicLibrary { get; set; } = true;

    /// <summary>Download Epic cover pictures for the Epic results being shown. When off, nothing is downloaded.</summary>
    public bool EpicCoverImages { get; set; } = true;
    /// <summary>Show people named in Ocula (read from Ocula's local people index, never online). Off until switched on.</summary>
    public bool SearchOculaPeople { get; set; } = false;

    /// <summary>Show article pictures on Wikipedia rows (downloaded from upload.wikimedia.org). Only matters while Search Wikipedia is on.</summary>
    public bool WikipediaPictures { get; set; } = true;

    /// <summary>Show Battle.net games from Porta's own list in search (installed ones play, the rest open their page). Nothing goes online.</summary>
    public bool SearchBattleNetGames { get; set; } = true;

    /// <summary>
    /// Search recent files and folders by name (read from the Windows Recent folder and Quick access on this PC, never
    /// online). Off until switched on.
    /// </summary>
    public bool SearchRecentFiles { get; set; } = false;

    /// <summary>Folders hidden from recent files and folders search, together with everything inside them.</summary>
    public List<string> ExcludedFolders { get; set; } = [];

    /// <summary>A calculation or conversion typed in search gets one answer row at the top (maths and units are fully local).</summary>
    public bool ShowQuickAnswers { get; set; } = true;

    /// <summary>Let currency conversions fetch exchange rates (Frankfurter, European Central Bank). Off until switched on.</summary>
    public bool OnlineCurrencyRates { get; set; } = false;

    /// <summary>A "Search the web" row at the bottom of search results. Nothing is sent until it is clicked.</summary>
    public bool SearchTheWeb { get; set; } = true;

    /// <summary>
    /// Which search engine that row opens in the browser: Google, DuckDuckGo or Bing. Null until the user picks one,
    /// which means Google; a saved pick is always kept.
    /// </summary>
    public string? WebSearchEngine { get; set; }

    // Appearance (used from milestone 2)
    public string Theme { get; set; } = "System";
    public string? AccentColor { get; set; }

    /// <summary>How frosted the menu's glass is: 0 clearest, 100 most frosted.</summary>
    public int GlassStrength { get; set; } = DefaultGlassStrength;
    public const int DefaultGlassStrength = 15;

    // Start button (used from milestone 2)
    public bool ReplaceStartButton { get; set; } = true;
    public string StartIcon { get; set; } = "Keyhole";
    public string StartIconSize { get; set; } = "Medium";
    public string? CustomStartIconPath { get; set; }

    // Profiles: each has its own pinned apps and folders
    public string ActiveProfile { get; set; } = "Personal";
    public List<Profile> Profiles { get; set; } = [new Profile { Name = "Personal" }];
}

public sealed class Profile
{
    public string Name { get; set; } = "";
    public List<PinItem> Pins { get; set; } = [];
}

/// <summary>A pinned app, or a folder of pinned apps (folders arrive in milestone 3).</summary>
public sealed class PinItem
{
    public string Kind { get; set; } = "App";      // "App" or "Folder"
    public string? AppId { get; set; }              // for apps
    public string? Name { get; set; }               // for folders
    public List<PinItem>? Children { get; set; }    // for folders
}
