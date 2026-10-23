namespace Porta.Services;

/// <summary>
/// The "Search the web" row: which search engine, and the address that opens it in the default browser.
/// Porta sends nothing itself; the browser opens the address only when the row is clicked or Enter is pressed.
/// </summary>
public static class WebSearch
{
    public static readonly string[] Engines = ["DuckDuckGo", "Google", "Bing"];

    /// <summary>The engine's name if it is one we know, otherwise DuckDuckGo.</summary>
    public static string Normalize(string? engine) =>
        Engines.FirstOrDefault(e => e.Equals(engine, StringComparison.OrdinalIgnoreCase)) ?? Engines[0];

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
