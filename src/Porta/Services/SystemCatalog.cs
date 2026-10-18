using System.Text.Json;
using Porta.Models;

namespace Porta.Services;

/// <summary>
/// Windows tools, Settings pages and power actions for search. They aren't installed apps, so the
/// Apps folder doesn't list them ("device manager" used to find nothing). Settings pages come from
/// Assets\settings-pages.json so new ones can be added without code changes.
/// </summary>
public static class SystemCatalog
{
    private static SystemEntry Tool(string name, string target, string glyph, bool admin = false, string? args = null, params string[] aliases) =>
        new() { Name = name, Kind = SystemKind.Tool, Target = target, Arguments = args, Glyph = glyph, AsksForAdmin = admin, Aliases = aliases };

    private static SystemEntry PowerItem(string name, PowerAction action, string glyph, bool confirm, params string[] aliases) =>
        new() { Name = name, Kind = SystemKind.Power, Power = action, Glyph = glyph, Confirm = confirm, Aliases = aliases };

    // AsksForAdmin follows each tool's own manifest: mmc.exe (every .msc console), regedit.exe and
    // perfmon.exe declare "highestAvailable", so Windows shows its admin prompt for administrators
    // (or elevates silently, if UAC is set to "Elevate without prompting").
    private static readonly SystemEntry[] Tools =
    [
        Tool("Device Manager", "devmgmt.msc", "", admin: true, aliases: ["devices", "hardware", "drivers", "devmgmt"]),
        Tool("Disk Management", "diskmgmt.msc", "", admin: true, aliases: ["partitions", "drives", "disks", "format", "diskmgmt"]),
        Tool("Computer Management", "compmgmt.msc", "", admin: true, aliases: ["compmgmt"]),
        Tool("Services", "services.msc", "", admin: true, aliases: ["services.msc", "windows services"]),
        Tool("Event Viewer", "eventvwr.msc", "", admin: true, aliases: ["events", "logs", "event log", "eventvwr"]),
        Tool("Task Scheduler", "taskschd.msc", "", admin: true, aliases: ["scheduled tasks", "taskschd"]),
        // resmon.exe itself runs as the user but hands off to "perfmon.exe /res", which asks for admin.
        Tool("Resource Monitor", "resmon.exe", "", admin: true, aliases: ["resmon"]),
        Tool("System Information", "msinfo32.exe", "", aliases: ["msinfo", "msinfo32", "system info", "specs"]),
        Tool("Performance Monitor", "perfmon.msc", "", admin: true, aliases: ["perfmon"]),
        Tool("Registry Editor", "regedit.exe", "", admin: true, aliases: ["regedit", "registry"]),
        Tool("Control Panel", "control.exe", "", aliases: ["control"]),
        Tool("Network Connections", "ncpa.cpl", "", aliases: ["network adapters", "adapters", "ncpa"]),
        Tool("Programs and Features", "appwiz.cpl", "", aliases: ["uninstall", "uninstall a program", "add or remove programs", "appwiz"]),
        Tool("Environment Variables", "rundll32.exe", "", args: "sysdm.cpl,EditEnvironmentVariables", aliases: ["env", "environment", "path"]),
        Tool("Windows Firewall", "firewall.cpl", "", aliases: ["firewall", "defender firewall"]),
        Tool("Disk Cleanup", "cleanmgr.exe", "", aliases: ["cleanup", "cleanmgr", "free up space"]),
        Tool("Windows Security", "windowsdefender:", "", aliases: ["defender", "antivirus", "virus"]),
    ];

    private static readonly SystemEntry[] PowerActions =
    [
        PowerItem("Lock", PowerAction.Lock, "", confirm: false, "lock pc", "lock screen"),
        PowerItem("Sign out", PowerAction.SignOut, "", confirm: false, "log off", "log out", "logout", "sign off"),
        PowerItem("Sleep", PowerAction.Sleep, "", confirm: false, "suspend"),
        PowerItem("Restart", PowerAction.Restart, "", confirm: true, "reboot"),
        PowerItem("Shut down", PowerAction.ShutDown, "", confirm: true, "shutdown", "power off", "turn off"),
    ];

    /// <summary>Icon for a Settings page that has none of its own, by the first part of its path.</summary>
    private static readonly Dictionary<string, string> SectionGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "",
        ["Bluetooth & devices"] = "",
        ["Network & internet"] = "",
        ["Personalization"] = "",
        ["Apps"] = "",
        ["Accounts"] = "",
        ["Time & language"] = "",
        ["Gaming"] = "",
        ["Accessibility"] = "",
        ["Privacy & security"] = "",
        ["Windows Update"] = "",
    };

    // Declared after Tools, PowerActions and SectionGlyphs: static fields initialise in this order.
    public static IReadOnlyList<SystemEntry> All { get; } = [.. Tools, .. LoadSettingsPages(), .. PowerActions];

    /// <summary>Settings pages from Assets\settings-pages.json. A missing or broken file just means no pages.</summary>
    private static List<SystemEntry> LoadSettingsPages()
    {
        var pages = new List<SystemEntry>();
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Assets", "settings-pages.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var p in doc.RootElement.GetProperty("pages").EnumerateArray())
            {
                var name = Str(p, "name");
                var uri = Str(p, "uri");
                if (name is null || uri is null || !uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Str(p, "path");
                var aliases = p.TryGetProperty("aliases", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                    : [];
                pages.Add(new SystemEntry
                {
                    Name = name,
                    Kind = SystemKind.Settings,
                    Target = uri,
                    Path = path,
                    Aliases = aliases,
                    Glyph = Glyph(Str(p, "glyph"), path),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Porta: couldn't read settings-pages.json: " + ex.Message);
        }
        return pages;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Glyph(string? hex, string? path)
    {
        if (hex is not null && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
            return char.ConvertFromUtf32(code);
        var section = path?.Split(" > ")[0];
        return section is not null && SectionGlyphs.TryGetValue(section, out var g) ? g : ""; // Settings gear
    }

    /// <summary>Best matches by name or alias, using the same rules as app search.</summary>
    public static List<SystemEntry> Search(string query, int max = 5)
    {
        query = query.Trim();
        if (query.Length < 2) return [];
        return All
            .Select(e => (Entry: e, Score: e.Aliases.Select(a => SearchService.Score(a, query)).Prepend(SearchService.Score(e.Name, query)).Max()))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name.Length)
            .Take(max)
            .Select(x => x.Entry)
            .ToList();
    }
}
