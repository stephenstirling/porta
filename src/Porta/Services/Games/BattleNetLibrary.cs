using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// Installed Blizzard / Battle.net games. Prefer Battle.net Agent's product.db, which records
/// product codes and install paths, and use Windows uninstall entries as a fallback.
/// product.db is a protobuf message without localized game titles, so known product codes are
/// named explicitly and registry display names are reused when available.
/// </summary>
public static partial class BattleNetLibrary
{
    private const string AgentProductDb = "Battle.net\\\\Agent\\\\product.db";

    /// <summary>
    /// The Battle.net shop has no reliable public search endpoint; open the regional shop instead.
    /// </summary>
    public const string ShopUrl = "https://shop.battle.net/";

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wow"] = "World of Warcraft",
        ["wow_classic"] = "World of Warcraft Classic",
        ["wow_classic_era"] = "World of Warcraft Classic Era",
        ["fen"] = "Diablo IV",
        ["d3"] = "Diablo III",
        ["pro"] = "Overwatch",
        ["prometheus"] = "Overwatch 2",
        ["s1"] = "StarCraft II",
        ["s2"] = "StarCraft",
        ["hero"] = "Heroes of the Storm",
        ["osi"] = "Hearthstone",
        ["wlby"] = "Warcraft III: Reforged",
        ["odin"] = "Call of Duty",
        ["d4"] = "Diablo IV",
    };

    public static string? LauncherPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net", "Battle.net.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battle.net", "Battle.net.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static List<GameEntry> Scan()
    {
        var launcher = LauncherPath();
        if (launcher is null) return [];

        var registryEntries = UninstallEntries().ToList();
        var registryNames = registryEntries
            .Select(x => (Entry: x, Match: ProductCode().Match(x.Uninstall)))
            .Where(x => x.Match.Success)
            .GroupBy(x => x.Match.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Entry.Name, StringComparer.OrdinalIgnoreCase);

        var games = new List<GameEntry>();
        var productCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var product in ReadProductDb())
        {
            if (IsLauncherProduct(product.ProductCode) ||
                string.IsNullOrWhiteSpace(product.ProductCode) ||
                string.IsNullOrWhiteSpace(product.InstallPath) ||
                !Directory.Exists(product.InstallPath))
                continue;

            var code = product.ProductCode;
            var name = registryNames.TryGetValue(code, out var registeredName)
                ? registeredName
                : KnownNames.TryGetValue(code, out var knownName)
                    ? knownName
                    : new DirectoryInfo(product.InstallPath).Name;

            if (string.IsNullOrWhiteSpace(name) || name.Equals("Battle.net", StringComparison.OrdinalIgnoreCase))
                continue;

            productCodes.Add(code);
            games.Add(new GameEntry
            {
                Name = name,
                Launcher = GameLauncher.BattleNet,
                Installed = true,
                Target = launcher,
                Arguments = $"--exec=\\"launch {code}\\"",
                IconSource = FindIcon(product.InstallPath, name),
                LauncherId = code,
            });
        }

        // Keep older or unusual installs discoverable if their product.db entry is missing/stale.
        foreach (var entry in registryEntries)
        {
            var match = ProductCode().Match(entry.Uninstall);
            if (!match.Success || !productCodes.Add(match.Groups[1].Value)) continue;

            var code = match.Groups[1].Value;
            games.Add(new GameEntry
            {
                Name = entry.Name,
                Launcher = GameLauncher.BattleNet,
                Installed = true,
                Target = launcher,
                Arguments = $"--exec=\\"launch {code}\\"",
                IconSource = entry.Icon,
                LauncherId = code,
            });
        }

        // Registry entries without a product code are retained as a last-resort fallback.
        foreach (var entry in registryEntries.Where(x => !ProductCode().IsMatch(x.Uninstall)))
        {
            if (games.Any(g => g.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))) continue;
            games.Add(new GameEntry
            {
                Name = entry.Name,
                Launcher = GameLauncher.BattleNet,
                Installed = true,
                Target = launcher,
                IconSource = entry.Icon,
            });
        }

        return games.DistinctBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindIcon(string installPath, string name)
    {
        // Prefer the uninstall registry icon; game folders often contain many unrelated executables.
        var registryIcon = UninstallEntries()
            .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Icon;
        if (registryIcon is not null && File.Exists(registryIcon)) return registryIcon;

        string[] candidates =
        [
            Path.Combine(installPath, "Support", "BlizzardBrowser", "BlizzardBrowser.exe"),
            Path.Combine(installPath, "Wow.exe"),
            Path.Combine(installPath, "Overwatch.exe"),
            Path.Combine(installPath, "Diablo IV.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<(string Name, string Uninstall, string? Icon)> UninstallEntries()
    {
        const string path = "SOFTWARE\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\Uninstall";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("Publisher") is not string publisher ||
                    !publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.GetValue("DisplayName") is not string name ||
                    name.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;

                var uninstall = k.GetValue("UninstallString") as string ?? "";
                var icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                yield return (name, uninstall, icon is not null && File.Exists(icon) ? icon : null);
            }
        }
    }

    private static IEnumerable<ProductInstall> ReadProductDb()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AgentProductDb);
        byte[]? data;
        try
        {
            data = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Porta: couldn't read Battle.net product.db: {ex.Message}");
            data = null;
        }
        if (data is null) yield break;

        // ProductDb.product_installs is repeated field 1. Each ProductInstall uses field 2 for
        // product_code and field 3 for settings; ProductInstallSettings.install_path is field 1.
        foreach (var field in Fields(data, 0, data.Length))
        {
            if (field.Number != 1 || field.WireType != 2) continue;
            string? code = null;
            string? installPath = null;
            foreach (var item in Fields(data, field.Offset, field.Length))
            {
                if (item.WireType != 2) continue;
                if (item.Number == 2)
                    code = Utf8(data, item.Offset, item.Length);
                else if (item.Number == 3)
                {
                    foreach (var setting in Fields(data, item.Offset, item.Length))
                    {
                        if (setting.Number == 1 && setting.WireType == 2)
                        {
                            installPath = Utf8(data, setting.Offset, setting.Length);
                            break;
                        }
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(installPath))
                yield return new ProductInstall(code, Environment.ExpandEnvironmentVariables(installPath));
        }
    }

    private static IEnumerable<ProtoField> Fields(byte[] data, int offset, int length)
    {
        var end = Math.Min(data.Length, offset + length);
        var position = offset;
        while (position < end)
        {
            if (!TryReadVarint(data, ref position, end, out var tag) || tag == 0) yield break;
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (number == 0) yield break;

            switch (wire)
            {
                case 0:
                    if (!TryReadVarint(data, ref position, end, out _)) yield break;
                    break;
                case 1:
                    if (end - position < 8) yield break;
                    position += 8;
                    break;
                case 2:
                    if (!TryReadVarint(data, ref position, end, out var size) ||
                        size > (ulong)(end - position) || size > int.MaxValue) yield break;
                    yield return new ProtoField(number, wire, position, (int)size);
                    position += (int)size;
                    break;
                case 5:
                    if (end - position < 4) yield break;
                    position += 4;
                    break;
                default:
                    yield break; // unsupported group wire types: fail closed on malformed data
            }
        }
    }

    private static bool TryReadVarint(byte[] data, ref int position, int end, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && position < end; shift += 7)
        {
            var b = data[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    private static string Utf8(byte[] data, int offset, int length) => Encoding.UTF8.GetString(data, offset, length);

    private static bool IsLauncherProduct(string code) =>
        code.Equals("agent", StringComparison.OrdinalIgnoreCase) ||
        code.Equals("bna", StringComparison.OrdinalIgnoreCase);

    private sealed record ProductInstall(string ProductCode, string InstallPath);
    private readonly record struct ProtoField(int Number, int WireType, int Offset, int Length);

    [GeneratedRegex(@"--productcode=([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProductCode();
}
