using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// Fetches Epic catalog key images in the background and caches them locally.
/// Epic does not provide a supported, unauthenticated public catalog API, so failures are
/// intentionally silent and the executable icon/letter placeholder remains available.
/// </summary>
public static class EpicArtworkService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Porta", "EpicArtwork");

    public static void Request(GameEntry game, string catalogNamespace, string catalogItemId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);
                var cachePath = Path.Combine(CacheFolder, SafeFileName(catalogItemId) + ".img");

                if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                {
                    Apply(game, cachePath);
                    return;
                }

                var imageUrl = await FindImageUrlAsync(catalogNamespace, catalogItemId);
                if (imageUrl is null) return;

                using var response = await Http.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024) return;

                var temporaryPath = cachePath + ".tmp";
                await File.WriteAllBytesAsync(temporaryPath, bytes);
                File.Move(temporaryPath, cachePath, overwrite: true);
                Apply(game, cachePath);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or
                                       UnauthorizedAccessException or JsonException or InvalidOperationException or
                                       UriFormatException)
            {
                Debug.WriteLine($"Porta: Epic artwork unavailable for {game.Name}: {ex.Message}");
            }
        });
    }

    private static async Task<string?> FindImageUrlAsync(string catalogNamespace, string catalogItemId)
    {
        // The Epic storefront GraphQL catalog query is an undocumented integration, not a stable
        // public API. Keep the request isolated here so a future endpoint change is easy to make.
        var query = $$"""
            query {
              Catalog {
                catalogItem(namespace: "{{EscapeGraphQl(catalogNamespace)}}", id: "{{EscapeGraphQl(catalogItemId)}}", locale: "en-US") {
                  keyImages { type url }
                }
              }
            }
            """;
        var url = "https://www.epicgames.com/graphql?query=" + Uri.EscapeDataString(query);
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Catalog", out var catalog) ||
            !catalog.TryGetProperty("catalogItem", out var item) ||
            item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("keyImages", out var images) ||
            images.ValueKind != JsonValueKind.Array)
            return null;

        // Prefer a square game box, then generic thumbnail, portrait box, and wide storefront art.
        foreach (var preferredType in new[] { "DieselGameBox", "Thumbnail", "DieselGameBoxTall", "DieselStoreFrontWide" })
        {
            foreach (var image in images.EnumerateArray())
            {
                if (image.TryGetProperty("type", out var type) &&
                    type.GetString() == preferredType &&
                    image.TryGetProperty("url", out var imageUrl) &&
                    Uri.TryCreate(imageUrl.GetString(), UriKind.Absolute, out var uri) &&
                    uri.Scheme is "https" or "http")
                    return uri.ToString();
            }
        }

        return null;
    }

    private static void Apply(GameEntry game, string path)
    {
        App.UiDispatcher.TryEnqueue(() =>
        {
            game.CoverPath = path;
            game.Icon = new BitmapImage(new Uri(path)) { DecodePixelWidth = 184 };
        });
    }

    private static string SafeFileName(string value) =>
        string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static string EscapeGraphQl(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
