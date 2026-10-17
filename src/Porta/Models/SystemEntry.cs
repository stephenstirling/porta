namespace Porta.Models;

public enum SystemKind { Tool, Settings, Power }

public enum PowerAction { Lock, SignOut, Sleep, Restart, ShutDown }

/// <summary>
/// A built-in Windows tool, Settings page or power action. Not installed apps, so they don't appear
/// in the Apps folder; Porta lists them itself (see <see cref="Services.SystemCatalog"/>).
/// </summary>
public sealed class SystemEntry
{
    public required string Name { get; init; }
    public required SystemKind Kind { get; init; }

    /// <summary>Other words people type for it: "drivers" for Device Manager, "wifi" for Wi-Fi.</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>What to open: an exe, .msc, .cpl, or an ms-settings: / windowsdefender: link. Null for power actions.</summary>
    public string? Target { get; init; }
    public string? Arguments { get; init; }
    public PowerAction? Power { get; init; }

    /// <summary>
    /// The tool declares it wants admin rights, so Windows shows its normal admin prompt when it opens.
    /// Porta doesn't elevate anything itself.
    /// </summary>
    public bool AsksForAdmin { get; init; }

    /// <summary>Ask "Are you sure?" first (Restart, Shut down).</summary>
    public bool Confirm { get; init; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; init; } = "";

    public string Subtitle => Kind switch
    {
        SystemKind.Tool => AsksForAdmin ? "System tool · Windows asks for admin" : "System tool",
        SystemKind.Settings => "Settings",
        _ => "Power",
    };
}
