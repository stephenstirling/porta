namespace Porta.Services;

/// <summary>
/// The "Search the web" row: which search engine, and the address that opens it in the default browser.
/// Porta sends nothing itself; the browser opens the address only when the row is clicked or Enter is pressed.
/// </summary>
public static class WebSearch
{
    /// <summary>The engine used until the user picks another; same order as the pop-up's dropdown.</summary>
    public const string Default = "Google";

    public static readonly string[] Engines = [Default, "DuckDuckGo", "Bing"];

    /// <summary>The engine's name if it is one we know (or none was ever chosen), otherwise Google.</summary>
    public static string Normalize(string? engine) =>
        Engines.FirstOrDefault(e => e.Equals(engine, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public static string UrlFor(string? engine, string query)
    {
        var q = Uri.EscapeDataString(query.Trim());
        return Normalize(engine) switch
        {
            "Google" => $"https://www.google.com/search?q={q}",
            "Bing" => $"https://www.bing.com/search?q={q}",
            _ => $"https://duckduckgo.com/?q={q}",
        };
    }
}
