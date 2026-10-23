namespace Porta.Models;

/// <summary>A recent file or a folder in search. Its file-type icon loads into <see cref="IconItem.Icon"/>.</summary>
public sealed class PathEntry : IconItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsFolder { get; init; }

    /// <summary>Smaller comes first among equal matches: folders, then the most recently used files.</summary>
    public int Order { get; init; }

    /// <summary>The folder the file is in; for a folder, the folder's own path.</summary>
    public string Subtitle => IsFolder ? FullPath : System.IO.Path.GetDirectoryName(FullPath) ?? FullPath;

    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

    /// <summary>Set once the row's icon has been asked for, so it is only loaded once.</summary>
    public bool IconRequested { get; set; }
}
