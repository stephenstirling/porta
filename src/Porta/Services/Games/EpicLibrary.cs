using System.Text.Json;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>Installed Epic Games Store games, from the launcher's install manifests.</summary>
public static class EpicLibrary
{
    private static string ManifestFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static List<GameEntry> Scan()
    {
        if (!Directory.Exists(ManifestFolder)) return [];
        var games = new List<GameEntry>();

        foreach (var file in Directory.EnumerateFiles(ManifestFolder, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                if (Bool(r, "bIsIncompleteInstall")) continue;
                if (r.TryGetProperty("AppCategories", out var cats) &&
                    cats.EnumerateArray().Any(c => c.GetString() is "addons" or "plugins")) continue;

                var name = Str(r, "DisplayName");
                var appName = Str(r, "AppName");
                var ns = Str(r, "CatalogNamespace");
                var item = Str(r, "CatalogItemId");
                if (name is null || appName is null || ns is null || item is null) continue;

                var install = Str(r, "InstallLocation");
                var exe = Str(r, "LaunchExecutable");
                var exePath = install is not null && exe is not null ? Path.Combine(install, exe) : null;

                games.Add(new GameEntry
                {
                    Name = name,
                    Launcher = GameLauncher.Epic,
                    Installed = true,
                    // Launch through the Epic launcher so sign-in, overlays and updates work.
                    Target = $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{appName}?action=launch&silent=true",
                    IconSource = exePath is not null && File.Exists(exePath) ? exePath : null,
                    LauncherId = appName,
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                // Skip a manifest that's mid-write or unexpected.
            }
        }
        return games;
    }

    public static bool IsInstalled() => Directory.Exists(ManifestFolder);

    /// <summary>Epic has no simple public search API, so Porta offers a link to the store's search page.</summary>
    public static string StoreSearchUrl(string term) => $"https://store.epicgames.com/browse?q={Uri.EscapeDataString(term)}";

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
