using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// Cover pictures for Epic results. Only the covers of the results being shown are downloaded, from the
/// links in Epic's local catalog cache. Files are kept in %LOCALAPPDATA%\Porta\EpicCovers with a size and
/// an age limit. Any failure is silent: the tile just keeps the game's own icon or the letter. With the
/// "Epic cover images" switch off, nothing is downloaded.
/// </summary>
public static class EpicCoverCache
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const long MaxCacheBytes = 40 * 1024 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Porta", "EpicCovers");

    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, byte> Busy = new();
    private static readonly ConcurrentDictionary<string, byte> Failed = new(); // not retried until Porta restarts
    private static readonly SemaphoreSlim OneAtATime = new(2);
    private static int _pruned;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        var version = typeof(EpicCoverCache).Assembly.GetName().Version?.ToString(3) ?? "0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Porta/{version} (https://github.com/stephenstirling/porta)");
        return http;
    }

    /// <summary>Shows the cover for an Epic game on its tile, downloading it first if needed and allowed.</summary>
    public static void Request(GameEntry game)
    {
        if (game.Launcher != GameLauncher.Epic || !App.Settings.Current.EpicCoverImages) return;
        var url = game.CoverUrl ??= EpicCatalogCache.CoverUrlFor(game.CatalogItemId);
        if (url is null || game.CoverPath is not null || Failed.ContainsKey(url) || !Busy.TryAdd(url, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var path = Path.Combine(Folder, Hash(url) + ".img");
                if (!IsFresh(path))
                {
                    await OneAtATime.WaitAsync();
                    try
                    {
                        if (!App.Settings.Current.EpicCoverImages) return; // switched off while waiting
                        await Download(url, path);
                    }
                    finally { OneAtATime.Release(); }
                }
                Apply(game, path);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or
                                       UnauthorizedAccessException or InvalidOperationException)
            {
                Failed[url] = 0;
                Debug.WriteLine($"Porta: no Epic cover for {game.Name}: {ex.Message}");
            }
            finally { Busy.TryRemove(url, out _); }
        });
    }

    /// <summary>Back to the game's own icon or the letter, for when the switch is turned off.</summary>
    public static void ForgetCovers(IEnumerable<GameEntry> games)
    {
        foreach (var game in games.Where(g => g.Launcher == GameLauncher.Epic && g.CoverPath is not null).ToList())
        {
            game.CoverPath = null;
            game.Icon = null;
            if (game.IconSource is not null) App.Icons.Request(game, game.IconSource, 64);
        }
    }

    private static bool IsFresh(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0 && DateTime.UtcNow - info.LastWriteTimeUtc < MaxAge;
    }

    private static async Task Download(string url, string path)
    {
        Directory.CreateDirectory(Folder);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
            response.Content.Headers.ContentLength > MaxFileBytes)
            throw new InvalidOperationException("not a small picture");

        // Read at most the limit, in case the length header was missing or wrong.
        await using var body = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxFileBytes) throw new InvalidOperationException("picture too large");
        }
        if (buffer.Length == 0) throw new InvalidOperationException("empty picture");

        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, buffer.ToArray());
        File.Move(temp, path, overwrite: true);
        if (Interlocked.Exchange(ref _pruned, 1) == 0 || Random.Shared.Next(10) == 0) Prune();
    }

    /// <summary>Deletes covers older than the age limit, then the oldest ones until the folder is under the size limit.</summary>
    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (DateTime.UtcNow - f.LastWriteTimeUtc > MaxAge || total > MaxCacheBytes) f.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file in use; the next prune gets it.
        }
    }

    private static void Apply(GameEntry game, string path)
    {
        App.UiDispatcher.TryEnqueue(() =>
        {
            if (!App.Settings.Current.EpicCoverImages) return; // switched off while it was loading
            game.CoverPath = path;
            game.Icon = new BitmapImage(new Uri(path)) { DecodePixelWidth = 184 };
        });
    }

    private static string Hash(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
}
