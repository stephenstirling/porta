namespace Porta.Models;

/// <summary>A Wikipedia article in search. The thumbnail loads from the web into <see cref="IconItem.Icon"/>.</summary>
public sealed class WikiEntry : IconItem
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required string Url { get; init; }
    public string? ThumbnailUrl { get; init; }

    public string Subtitle => Description ?? "Wikipedia article";
    public string Initial => Title.Length > 0 ? char.ToUpperInvariant(Title[0]).ToString() : "W";
}
