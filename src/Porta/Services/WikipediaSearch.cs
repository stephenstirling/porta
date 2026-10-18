using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Porta.Services;

public sealed record WikiResult(string Title, string? Description, string Url, string? ThumbnailUrl);

/// <summary>
/// Wikipedia articles for search, from Wikimedia's REST title search on the Wikipedia that matches the
/// Windows display language (English if that has none). Online; Settings can turn it off.
/// </summary>
public static class WikipediaSearch
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, (DateTime At, List<WikiResult> Results)> Cache = [];

    /// <summary>Why the last lookup failed, for diagnosis; search itself stays quiet.</summary>
    public static string? LastError { get; private set; }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        // Wikimedia asks every client to identify itself with a way to reach its author.
        var version = typeof(WikipediaSearch).Assembly.GetName().Version?.ToString(3) ?? "0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Porta/{version} (https://github.com/stephenstirling/porta)");
        return http;
    }

    /// <summary>Two- or three-letter Wikipedia language code from the Windows display language.</summary>
    public static string Language
    {
        get
        {
            var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return code.Length is 2 or 3 && code.All(char.IsAsciiLetterLower) && code != "iv" ? code : "en";
        }
    }

    /// <summary>Up to 3 articles. Empty on no network, timeouts or errors. Throws only when cancelled.</summary>
    public static async Task<List<WikiResult>> SearchAsync(string query, CancellationToken ct)
    {
        var lang = Language;
        var results = await SearchSiteAsync(lang, query, ct).ConfigureAwait(false);
        if ((results is null || results.Count == 0) && lang != "en")
            results = await SearchSiteAsync("en", query, ct).ConfigureAwait(false);
        return results ?? [];
    }

    private static async Task<List<WikiResult>?> SearchSiteAsync(string lang, string query, CancellationToken ct)
    {
        var key = lang + "|" + query.Trim().ToLowerInvariant();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Results;
        }

        try
        {
            var url = $"https://{lang}.wikipedia.org/w/rest.php/v1/search/title?q={Uri.EscapeDataString(query)}&limit=3";
            var response = await Http.GetFromJsonAsync<SearchResponse>(url, ct).ConfigureAwait(false);
            var results = (response?.Pages ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p.Title) && !string.IsNullOrWhiteSpace(p.Key))
                .Select(p => new WikiResult(
                    p.Title!,
                    string.IsNullOrWhiteSpace(p.Description) ? null : p.Description,
                    $"https://{lang}.wikipedia.org/wiki/{Uri.EscapeDataString(p.Key!)}",
                    Absolute(p.Thumbnail?.Url)))
                .ToList();
            lock (Cache)
            {
                if (Cache.Count > 200) Cache.Clear();
                Cache[key] = (DateTime.UtcNow, results);
            }
            LastError = null;
            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // a newer search replaced this one
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            LastError = $"{lang}.wikipedia.org: {ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine("Porta: Wikipedia search failed: " + LastError);
            return null;
        }
    }

    /// <summary>Thumbnails come as protocol-relative links ("//thumb.wikimedia.org/…").</summary>
    private static string? Absolute(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.StartsWith("//") ? "https:" + url : url;

    private sealed record SearchResponse([property: JsonPropertyName("pages")] List<Page>? Pages);

    private sealed record Page(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("thumbnail")] Thumb? Thumbnail);

    private sealed record Thumb([property: JsonPropertyName("url")] string? Url);
}
