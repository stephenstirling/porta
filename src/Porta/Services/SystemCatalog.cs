using Porta.Models;

namespace Porta.Services;

/// <summary>
/// Windows tools, Settings pages and power actions for search. They aren't installed apps, so the
/// Apps folder doesn't list them ("device manager" used to find nothing).
/// </summary>
public static class SystemCatalog
{
    private static SystemEntry Tool(string name, string target, string glyph, bool admin = false, string? args = null, params string[] aliases) =>
        new() { Name = name, Kind = SystemKind.Tool, Target = target, Arguments = args, Glyph = glyph, AsksForAdmin = admin, Aliases = aliases };

    private static SystemEntry Page(string name, string uri, string glyph, params string[] aliases) =>
        new() { Name = name, Kind = SystemKind.Settings, Target = uri, Glyph = glyph, Aliases = aliases };

    private static SystemEntry PowerItem(string name, PowerAction action, string glyph, bool confirm, params string[] aliases) =>
        new() { Name = name, Kind = SystemKind.Power, Power = action, Glyph = glyph, Confirm = confirm, Aliases = aliases };

    // AsksForAdmin follows each tool's own manifest: mmc.exe (every .msc console), regedit.exe and
    // perfmon.exe declare "highestAvailable", so Windows shows its admin prompt for administrators.
    public static IReadOnlyList<SystemEntry> All { get; } =
    [
        // ---- System tools ----
        Tool("Device Manager", "devmgmt.msc", "", admin: true, aliases: ["devices", "hardware", "drivers", "devmgmt"]),
        Tool("Disk Management", "diskmgmt.msc", "", admin: true, aliases: ["partitions", "drives", "disks", "format", "diskmgmt"]),
        Tool("Computer Management", "compmgmt.msc", "", admin: true, aliases: ["compmgmt"]),
        Tool("Services", "services.msc", "", admin: true, aliases: ["services.msc", "windows services"]),
        Tool("Event Viewer", "eventvwr.msc", "", admin: true, aliases: ["events", "logs", "event log", "eventvwr"]),
        Tool("Task Scheduler", "taskschd.msc", "", admin: true, aliases: ["scheduled tasks", "taskschd"]),
        Tool("Resource Monitor", "resmon.exe", "", aliases: ["resmon"]),
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

        // ---- Settings pages ----
        Page("Display", "ms-settings:display", "", "screen", "resolution", "monitor", "brightness", "scaling"),
        Page("Sound", "ms-settings:sound", "", "audio", "volume", "speakers", "microphone"),
        Page("Bluetooth", "ms-settings:bluetooth", "", "bluetooth devices", "pair"),
        Page("Wi-Fi", "ms-settings:network-wifi", "", "wifi", "wi-fi", "wireless", "internet"),
        Page("Network", "ms-settings:network-status", "", "internet", "network and internet", "ethernet"),
        Page("VPN", "ms-settings:network-vpn", "", "vpn"),
        Page("Apps", "ms-settings:appsfeatures", "", "installed apps", "uninstall", "apps and features"),
        Page("Default apps", "ms-settings:defaultapps", "", "default browser", "file associations", "open with"),
        Page("Startup apps", "ms-settings:startupapps", "", "startup", "run at startup"),
        Page("Windows Update", "ms-settings:windowsupdate", "", "update", "updates"),
        Page("Privacy", "ms-settings:privacy", "", "privacy and security", "permissions"),
        Page("Personalization", "ms-settings:personalization", "", "dark mode", "light mode", "wallpaper", "background", "theme", "colours", "colors"),
        Page("Taskbar", "ms-settings:taskbar", "", "taskbar settings"),
        Page("Power and battery", "ms-settings:powersleep", "", "power", "battery", "power plan", "sleep settings"),
        Page("Storage", "ms-settings:storagesense", "", "storage sense", "disk space"),
        Page("Accounts", "ms-settings:accounts", "", "account", "your info", "sign-in options", "password"),
        Page("Time and language", "ms-settings:dateandtime", "", "date", "time", "time zone", "language", "region", "keyboard"),
        Page("Accessibility", "ms-settings:easeofaccess", "", "ease of access", "narrator", "magnifier", "high contrast"),
        Page("About", "ms-settings:about", "", "about this pc", "pc name", "windows version", "system specs"),

        // ---- Power ----
        PowerItem("Lock", PowerAction.Lock, "", confirm: false, "lock pc", "lock screen"),
        PowerItem("Sign out", PowerAction.SignOut, "", confirm: false, "log off", "log out", "logout", "sign off"),
        PowerItem("Sleep", PowerAction.Sleep, "", confirm: false, "suspend"),
        PowerItem("Restart", PowerAction.Restart, "", confirm: true, "reboot"),
        PowerItem("Shut down", PowerAction.ShutDown, "", confirm: true, "shutdown", "power off", "turn off"),
    ];

    /// <summary>Best matches by name or alias, using the same rules as app search.</summary>
    public static List<SystemEntry> Search(string query, int max = 4)
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
