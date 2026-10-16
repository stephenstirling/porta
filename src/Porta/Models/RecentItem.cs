namespace Porta.Models;

public sealed class RecentItem : IconItem
{
    public required string Name { get; init; }
    public required string LinkPath { get; init; }
    public required string When { get; init; }
}
