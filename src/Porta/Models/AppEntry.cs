namespace Porta.Models;

/// <summary>An installed app, as listed in Windows' Apps folder (desktop and Store apps alike).</summary>
public sealed class AppEntry : IconItem
{
    public required string Name { get; init; }

    /// <summary>The app's ID in shell:AppsFolder — an AUMID for Store apps or a path for desktop apps.</summary>
    public required string Id { get; init; }

    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

    public string ShellPath => $"shell:AppsFolder\\{Id}";
}

/// <summary>A letter group in All apps.</summary>
public sealed class AppGroup(string key, IEnumerable<AppEntry> apps) : List<AppEntry>(apps)
{
    public string Key { get; } = key;
}
