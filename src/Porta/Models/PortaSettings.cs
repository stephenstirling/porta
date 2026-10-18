namespace Porta.Models;

/// <summary>Everything Porta saves. Stored as JSON in %LocalAppData%\Porta\settings.json.</summary>
public sealed class PortaSettings
{
    public int Version { get; set; } = 1;
    public bool FirstRunDone { get; set; }

    // General
    public bool OpenWithWinKey { get; set; } = true;
    public bool ShowRecent { get; set; } = true;

    /// <summary>Apps hidden from All apps (by app id). They still appear in search.</summary>
    public List<string> HiddenApps { get; set; } = [];

    // Search
    /// <summary>Show Steam store results for games that aren't installed (asks Steam online).</summary>
    public bool SearchSteamStore { get; set; } = true;

    /// <summary>Show Wikipedia articles for what's typed (asks Wikipedia online). When off, nothing is sent.</summary>
    public bool SearchWikipedia { get; set; } = true;

    // Appearance (used from milestone 2)
    public string Theme { get; set; } = "System";
    public string? AccentColor { get; set; }

    /// <summary>How frosted the menu's glass is: 0 clearest, 100 most frosted.</summary>
    public int GlassStrength { get; set; } = 30;

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
