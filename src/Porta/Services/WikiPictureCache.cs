using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Porta.Services;

/// <summary>
/// Article pictures for Wikipedia rows. Only https://upload.wikimedia.org thumbnail links are ever fetched, one
/// small picture per article shown, with the same User-Agent as the Wikipedia search. Pictures are kept in memory
/// and in %LOCALAPPDATA%\Porta\WikiPictures (about 20 MB at most, oldest dropped first); turning the
/// "Wikipedia pictures" switch off deletes that folder. Any failure is silent: the row keeps its letter tile.
/// </summary>
public static class WikiPictureCache
{
    private const string AllowedHost = "upload.wikimedia.org";
    private const long MaxFileBytes = 1024 * 1024;
    private const long MaxCacheBytes = 20 * 1024 * 1024;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Porta", "WikiPictures");

    private static readonly ConcurrentDictionary<string, string> Known = new(); // link -> file on disk
    private static readonly SemaphoreSlim OneAtATime = new(3);
    private static int _pruned;

    /// <summary>True only for https links on upload.wikimedia.org.</summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Wikipedia now lists thumbnails on thumb.wikimedia.org; the same file is served by upload.wikimedia.org, so a
    /// link from either host is rewritten to https://upload.wikimedia.org/wikipedia/... (no query part). Anything
    /// else gives null and is never fetched.
    /// </summary>
    public static string? Normalize(string? url)
    {
        if (url is not null && url.StartsWith("//")) url = "https:" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return null;
        if (!uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("thumb.wikimedia.org", StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.AbsolutePath.StartsWith("/wikipedia/", StringComparison.Ordinal)) return null;
        return "https://" + AllowedHost + uri.AbsolutePath;
    }

    /// <summary>The picture's file on disk (downloading it first if needed), or null when it can't be had.</summary>
    public static async Task<string?> GetFileAsync(string url, CancellationToken ct)
    {
        if (!IsAllowed(url)) return null;
        if (Known.TryGetValue(url, out var known) && File.Exists(known)) return known;

        try
        {
            var path = Path.Combine(Folder, Hash(url) + ".img");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 0)
            {
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } // recently used stays longer
                Known[url] = path;
                return path;
            }

            await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!App.Settings.Current.SearchWikipedia || !App.Settings.Current.WikipediaPictures) return null; // switched off while waiting
                await DownloadAsync(url, path, ct).ConfigureAwait(false);
            }
            finally { OneAtATime.Release(); }

            Known[url] = path;
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or
                                   UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine("Porta: no Wikipedia picture: " + ex.Message);
            return null;
        }
    }

    /// <summary>Forgets everything and deletes the folder, for when the switch is turned off.</summary>
    public static void Clear()
    {
        Known.Clear();
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine("Porta: couldn't clear Wikipedia pictures: " + ex.Message); // a picture in use; gone at the next clear
        }
    }

    private static async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        using var response = await WikipediaSearch.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // A redirect must not lead anywhere else.
        if (!IsAllowed(response.RequestMessage?.RequestUri?.AbsoluteUri) ||
            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
            response.Content.Headers.ContentLength > MaxFileBytes)
            throw new InvalidOperationException("not a small Wikimedia picture");

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxFileBytes) throw new InvalidOperationException("picture too large");
        }
        if (buffer.Length == 0) throw new InvalidOperationException("empty picture");

        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, buffer.ToArray(), ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        if (Interlocked.Exchange(ref _pruned, 1) == 0 || Random.Shared.Next(10) == 0) Prune();
    }

    /// <summary>Deletes the oldest pictures until the folder is under the size limit.</summary>
    private static void Prune()
    {
        try
        {
            long total = 0;
            foreach (var f in new DirectoryInfo(Folder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc))
            {
                total += f.Length;
                if (total > MaxCacheBytes) f.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file in use; the next prune gets it.
        }
    }

    private static string Hash(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
}
