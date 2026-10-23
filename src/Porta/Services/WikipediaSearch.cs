using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Porta.Services;

public sealed record WikiResult(string Title, string? Description, string Url, string? ThumbnailUrl, bool IsDisambiguation = false);

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

    /// <summary>The same client (and User-Agent) for article pictures.</summary>
    internal static HttpClient Client => Http;

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
    public static async Task<List<WikiResult>> SearchAsync(string query, CancellationToken ct, bool pictures = false)
    {
        var lang = Language;
        var results = await SearchSiteAsync(lang, query, pictures, ct).ConfigureAwait(false);
        if ((results is null || results.Count == 0) && lang != "en")
            results = await SearchSiteAsync("en", query, pictures, ct).ConfigureAwait(false);
        return results ?? [];
    }

    private static async Task<List<WikiResult>?> SearchSiteAsync(string lang, string query, bool pictures, CancellationToken ct)
    {
        var key = lang + "|" + (pictures ? "p|" : "t|") + query.Trim().ToLowerInvariant();
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
                    pictures ? Absolute(p.Thumbnail?.Url) : null))
                .ToList();

            // Disambiguation pages ("Python: topics referred to by the same term") go after real articles.
            // The same request also asks for each article's picture (96 px) when pictures are on.
            var (disambiguation, thumbnails) = await DisambiguationTitlesAsync(lang, results, pictures, ct).ConfigureAwait(false);
            results = results
                .Select(r => r with
                {
                    ThumbnailUrl = PictureOrNull(thumbnails.GetValueOrDefault(r.Title) ?? r.ThumbnailUrl),
                    IsDisambiguation = disambiguation.Contains(r.Title),
                })
                .OrderBy(r => r.IsDisambiguation).ToList(); // stable: keeps the rest in order
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

    /// <summary>
    /// Which of these articles are disambiguation pages, from their "disambiguation" page property (one small
    /// request, which also asks for 96 px pictures when <paramref name="pictures"/> is on). If that fails, falls
    /// back to the title or the standard English description, and the pictures from the search itself.
    /// </summary>
    private static async Task<(HashSet<string> Disambiguation, Dictionary<string, string> Thumbnails)> DisambiguationTitlesAsync(
        string lang, List<WikiResult> results, bool pictures, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var thumbnails = new Dictionary<string, string>(StringComparer.Ordinal);
        if (results.Count == 0) return (found, thumbnails);
        try
        {
            var titles = string.Join("|", results.Select(r => r.Title));
            var url = $"https://{lang}.wikipedia.org/w/api.php?action=query&prop=pageprops{(pictures ? "|pageimages" : "")}&ppprop=disambiguation" +
                      (pictures ? "&piprop=thumbnail&pithumbsize=96&pilicense=any" : "") +
                      $"&redirects=1&format=json&formatversion=2&titles={Uri.EscapeDataString(titles)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("query", out var query) && query.TryGetProperty("pages", out var pages))
            {
                foreach (var page in pages.EnumerateArray())
                {
                    if (page.TryGetProperty("pageprops", out var props) && props.TryGetProperty("disambiguation", out _) &&
                        page.TryGetProperty("title", out var title) && title.GetString() is { } t)
                        found.Add(t);
                    if (pictures && page.TryGetProperty("title", out var pt) && pt.GetString() is { } pageTitle &&
                        page.TryGetProperty("thumbnail", out var thumb) && thumb.TryGetProperty("source", out var source) &&
                        source.GetString() is { } picture)
                        thumbnails[pageTitle] = picture;
                }
            }
            return (found, thumbnails);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            foreach (var r in results)
            {
                if (r.Title.EndsWith("(disambiguation)", StringComparison.OrdinalIgnoreCase) ||
                    r.Description == "Topics referred to by the same term")
                    found.Add(r.Title);
            }
            return (found, thumbnails);
        }
    }

    private static readonly Dictionary<string, string?> FallbackCache = [];

    /// <summary>
    /// A picture for one article the main request found none for. One small request to the article's REST
    /// summary. Disambiguation pages and articles that really have no picture give null. The link goes through
    /// the same rule as every other picture: rewritten to https://upload.wikimedia.org, anything else dropped.
    /// Silent on any failure, including offline.
    /// </summary>
    public static async Task<string?> FallbackPictureAsync(string articleUrl, CancellationToken ct)
    {
        lock (FallbackCache)
        {
            if (FallbackCache.TryGetValue(articleUrl, out var known)) return known;
        }
        try
        {
            if (!Uri.TryCreate(articleUrl, UriKind.Absolute, out var article) || article.Scheme != Uri.UriSchemeHttps ||
                !article.Host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase) ||
                !article.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
                return null;

            var summaryUrl = $"https://{article.Host}/api/rest_v1/page/summary/{article.AbsolutePath["/wiki/".Length..]}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(summaryUrl, ct).ConfigureAwait(false));
            var root = doc.RootElement;

            string? picture = null;
            if (!(root.TryGetProperty("type", out var type) && type.GetString() == "disambiguation") &&
                root.TryGetProperty("thumbnail", out var thumb) && thumb.TryGetProperty("source", out var source))
                picture = SmallPicture(source.GetString(), thumb.TryGetProperty("width", out var w) && w.TryGetInt32(out var width) ? width : 0);

            lock (FallbackCache)
            {
                if (FallbackCache.Count > 200) FallbackCache.Clear();
                FallbackCache[articleUrl] = picture;
            }
            return picture;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            Debug.WriteLine("Porta: no fallback Wikipedia picture: " + ex.Message);
            return null; // not remembered, so a later search can try again
        }
    }

    /// <summary>
    /// The summary's picture can be the full-size original. A 120 px thumbnail of the same file is asked for
    /// instead (unless the file is smaller than that), so rows never download a big image.
    /// </summary>
    private static string? SmallPicture(string? url, int width)
    {
        var picture = PictureOrNull(url);
        if (picture is null) return null;
        var path = new Uri(picture).AbsolutePath; // /wikipedia/<project>/<h>/<hh>/<File.ext> for an original
        if (path.Contains("/thumb/", StringComparison.Ordinal) || width is > 0 and < 120) return picture;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // wikipedia, project, h, hh, File.ext
        if (parts.Length != 5) return null;
        var file = parts[4];
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var thumbName = ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" => $"120px-{file}",
            ".svg" => $"120px-{file}.png", // vector files come back as PNG
            _ => null,                     // PDF, TIFF, video... no simple thumbnail: keep the letter tile
        };
        return thumbName is null
            ? null
            : PictureOrNull($"https://upload.wikimedia.org/wikipedia/{parts[1]}/thumb/{parts[2]}/{parts[3]}/{file}/{thumbName}");
    }

    /// <summary>Only https://upload.wikimedia.org pictures are ever passed on.</summary>
    private static string? PictureOrNull(string? url) => WikiPictureCache.Normalize(url);

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
