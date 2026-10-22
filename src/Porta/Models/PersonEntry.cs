using Microsoft.UI.Xaml;

namespace Porta.Models;

/// <summary>A person named in Ocula, from Ocula's people index. The face thumbnail loads into <see cref="IconItem.Icon"/>.</summary>
public sealed class PersonEntry : IconItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string[] Aliases { get; init; } = [];
    public string? Description { get; init; }
    public int PhotoCount { get; init; }
    /// <summary>Absolute local path to the face thumbnail, already checked to exist; null when there isn't one.</summary>
    public string? ThumbnailPath { get; init; }

    /// <summary>Set when the row is added to the results: false when Ocula isn't installed, so there's nothing to open.</summary>
    public bool CanOpen { get; set; }

    public string Subtitle
    {
        get
        {
            var photos = PhotoCount == 1 ? "1 photo" : $"{PhotoCount} photos";
            return string.IsNullOrWhiteSpace(Description) ? photos : $"{photos} · {Description}";
        }
    }

    public string ActionText => CanOpen ? "Open" : "";
    public Visibility ActionVisibility => CanOpen ? Visibility.Visible : Visibility.Collapsed;
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
}
