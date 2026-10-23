using Microsoft.UI.Xaml;

namespace Porta.Models;

/// <summary>A row for the quick answer at the top of search, or for the "Search the web" row at the bottom.</summary>
public sealed class QuickRow
{
    /// <summary>Segoe Fluent Icons character shown in the round tile.</summary>
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    /// <summary>"Copy" or "Search"; empty when the row is only a message.</summary>
    public string ActionText { get; init; } = "";
    /// <summary>What Enter or a click copies (quick answers); null when there is nothing to copy.</summary>
    public string? CopyText { get; init; }
    /// <summary>The query a web search row opens; null for other rows.</summary>
    public string? WebQuery { get; init; }

    public Visibility ActionVisibility => ActionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}
