using System.Text.Json;
using Porta.Models;

namespace Porta.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Porta");

    private string FilePath => Path.Combine(_folder, "settings.json");

    public PortaSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<PortaSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch
        {
            // Keep the damaged file for inspection and start fresh rather than crashing.
            File.Copy(FilePath, FilePath + ".damaged", overwrite: true);
            Current = new();
        }

        if (Current.Profiles.Count == 0) Current.Profiles.Add(new Profile { Name = "Personal" });
    }

    public void Save()
    {
        Directory.CreateDirectory(_folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
        File.Move(temp, FilePath, overwrite: true); // atomic replace, so a crash never leaves half a file
    }

    // ---------- Hidden apps ----------

    public bool IsHidden(string appId) =>
        Current.HiddenApps.Contains(appId, StringComparer.OrdinalIgnoreCase);

    public void SetHidden(string appId, bool hidden)
    {
        Current.HiddenApps.RemoveAll(id => string.Equals(id, appId, StringComparison.OrdinalIgnoreCase));
        if (hidden) Current.HiddenApps.Add(appId);
        Save();
    }

    // ---------- Backup and restore ----------

    /// <summary>Writes every Porta setting (pins, folders, profiles, hidden apps, preferences) to a file.</summary>
    public void ExportTo(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new Backup("Porta", 1, DateTimeOffset.Now, Current), Json));

    /// <summary>
    /// Replaces the current settings with a backup. The current settings are kept first as
    /// settings.before-restore.json, so a wrong restore can be undone by hand.
    /// Throws <see cref="InvalidDataException"/> if the file isn't a Porta backup.
    /// </summary>
    public void ImportFrom(string path)
    {
        Backup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("That file isn't a Porta settings backup.");
        }
        if (backup is null || backup.App != "Porta" || backup.Settings is null)
            throw new InvalidDataException("That file isn't a Porta settings backup.");
        if (backup.Settings.Version > new PortaSettings().Version)
            throw new InvalidDataException("That backup is from a newer Porta. Update Porta, then restore it.");

        Directory.CreateDirectory(_folder);
        if (File.Exists(FilePath)) File.Copy(FilePath, Path.Combine(_folder, "settings.before-restore.json"), overwrite: true);

        Current = backup.Settings;
        if (Current.Profiles.Count == 0) Current.Profiles.Add(new Profile { Name = "Personal" });
        Current.FirstRunDone = true; // don't re-add starter pins on top of a restore
        Save();
        Restored?.Invoke();
    }

    /// <summary>Raised after a restore so the menu can rebuild.</summary>
    public event Action? Restored;

    private sealed record Backup(string App, int BackupVersion, DateTimeOffset Created, PortaSettings Settings);

    public Profile ActiveProfile =>
        Current.Profiles.FirstOrDefault(p => p.Name == Current.ActiveProfile) ?? Current.Profiles[0];

    public bool IsPinned(string appId) =>
        ActiveProfile.Pins.Any(p => p.Kind == "App" && string.Equals(p.AppId, appId, StringComparison.OrdinalIgnoreCase));

    public void Pin(string appId)
    {
        if (IsPinned(appId)) return;
        ActiveProfile.Pins.Add(new PinItem { Kind = "App", AppId = appId });
        Save();
    }

    public void Unpin(string appId)
    {
        ActiveProfile.Pins.RemoveAll(p => p.Kind == "App" && string.Equals(p.AppId, appId, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    /// <summary>Saves a new order after the user drags pinned apps around.</summary>
    public void SetPinOrder(IEnumerable<string> appIds)
    {
        var pins = ActiveProfile.Pins;
        var ordered = appIds
            .Select(id => pins.FirstOrDefault(p => p.Kind == "App" && p.AppId == id))
            .OfType<PinItem>()
            .ToList();
        ordered.AddRange(pins.Where(p => !ordered.Contains(p)));
        ActiveProfile.Pins = ordered;
        Save();
    }

    /// <summary>On first run, pin a sensible starter set from whatever is installed.</summary>
    public void EnsureStarterPins(AppCatalog catalog)
    {
        if (Current.FirstRunDone) return;

        string[] wanted =
        [
            "File Explorer", "Microsoft Edge", "Google Chrome", "Firefox", "Outlook", "Mail", "Calendar",
            "Photos", "Settings", "Notepad", "Terminal", "Calculator", "Paint", "Snipping Tool", "Media Player", "Camera",
        ];

        foreach (var name in wanted)
        {
            var app = catalog.Apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (app is not null && !IsPinned(app.Id))
                ActiveProfile.Pins.Add(new PinItem { Kind = "App", AppId = app.Id });
            if (ActiveProfile.Pins.Count >= 15) break;
        }

        Current.FirstRunDone = true;
        Save();
    }
}
