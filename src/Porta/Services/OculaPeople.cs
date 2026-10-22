using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using Porta.Models;

namespace Porta.Services;

/// <summary>
/// "People in Ocula": the names Ocula has put in people-index.json (format version 1: id, name, aliases,
/// description, photoCount, thumbnail). Read-only and offline. Only those six fields are ever read. The list is
/// kept in memory and read again only when the file's timestamp or size changes. A missing, unreadable or odd
/// file means no people and never an error.
///
/// Where the file is: %LocalAppData%\Stirling\Ocula\people-index.json. A packaged Ocula (the Store or a
/// sideloaded build) has its writes under %LocalAppData% redirected by Windows into its private package folder,
/// so there the file is really in
/// %LocalAppData%\Packages\StephenStirling.Ocula_*\LocalCache\Local\Stirling\Ocula\people-index.json. Both
/// places are looked at and the most recently written file wins (a stale file left in one place never hides a
/// fresh one in the other). Ocula writes the thumbnails' paths in the file as paths that work from outside it.
/// </summary>
public static class OculaPeople
{
    private const int MaxPeople = 20000;
    private const long MaxFileBytes = 64 * 1024 * 1024;
    private const long MaxThumbnailBytes = 2 * 1024 * 1024;

    /// <summary>%LocalAppData%; settable inside the app only so a check can point it at a scratch folder.</summary>
    internal static string LocalAppData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The package-family prefix of every Ocula build from this publisher; the suffix is the publisher id and differs after Store signing.</summary>
    private const string OculaPackagePrefix = "StephenStirling.Ocula_";

    /// <summary>
    /// The people file to read: the newest of the real location and each packaged Ocula's package folder, or
    /// null if there is none. Directory problems (a locked or odd folder) just mean that place is skipped.
    /// </summary>
    internal static string? FindIndexFile()
    {
        var candidates = new List<string> { Path.Combine(LocalAppData, "Stirling", "Ocula", "people-index.json") };
        try
        {
            var packages = Path.Combine(LocalAppData, "Packages");
            if (Directory.Exists(packages))
                foreach (var dir in Directory.EnumerateDirectories(packages, OculaPackagePrefix + "*"))
                    candidates.Add(Path.Combine(dir, "LocalCache", "Local", "Stirling", "Ocula", "people-index.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The real location still counts.
        }

        string? best = null;
        var bestWritten = DateTime.MinValue;
        foreach (var path in candidates)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.LastWriteTimeUtc >= bestWritten) { best = path; bestWritten = info.LastWriteTimeUtc; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip this candidate.
            }
        }
        return best;
    }

    private static readonly object Gate = new();
    private static List<PersonEntry> _people = [];
    private static (string Path, DateTime Written, long Length) _loadedVersion;
    private static Task? _loading;

    /// <summary>Ocula counts as installed when it has registered its ocula: link for this user.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\ocula");
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The ocula: link that opens Ocula on this person.</summary>
    public static string OpenLink(PersonEntry person) => $"ocula:person?id={Uri.EscapeDataString(person.Id)}";

    /// <summary>
    /// Up to <paramref name="max"/> people whose name or an alias matches, best first. Uses the list as last
    /// read; if the file changed, a background reload starts and the next search sees it.
    /// </summary>
    public static List<PersonEntry> Search(string query, int max = 3)
    {
        RefreshIfChanged();
        List<PersonEntry> people;
        lock (Gate) people = _people;

        return people
            .Select(p => (Person: p, Score: Math.Max(
                SearchService.Score(p.Name, query),
                p.Aliases.Select(a => SearchService.Score(a, query)).DefaultIfEmpty(0).Max())))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Person.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.Person)
            .ToList();
    }

    /// <summary>Forgets the list, for when the switch is turned off.</summary>
    public static void Clear()
    {
        lock (Gate) { _people = []; _loadedVersion = default; }
    }

    private static void RefreshIfChanged()
    {
        (string, DateTime, long) version;
        try
        {
            if (FindIndexFile() is not { } file) { Clear(); return; }
            var info = new FileInfo(file);
            version = (file, info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        bool first;
        lock (Gate)
        {
            if (version == _loadedVersion || _loading is { IsCompleted: false }) return;
            first = _loadedVersion == default;
            if (!first) _loading = Task.Run(() => Load(version));
        }
        // The very first read happens right now so the first search already sees people; later changes reload in the background.
        if (first) Load(version);
    }

    private static void Load((string Path, DateTime Written, long Length) version)
    {
        try
        {
            if (version.Length > MaxFileBytes) return;
            string text;
            using (var stream = new FileStream(version.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                text = reader.ReadToEnd();

            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;

            // The list is either the top-level array or the "people" array of an object that says its version.
            JsonElement list;
            if (root.ValueKind == JsonValueKind.Array) list = root;
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("version", out var v) && !(v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n == 1))
                {
                    lock (Gate) { _people = []; _loadedVersion = version; } // a newer format Porta doesn't know
                    return;
                }
                if (!root.TryGetProperty("people", out list) || list.ValueKind != JsonValueKind.Array) return;
            }
            else return;

            var people = new List<PersonEntry>();
            foreach (var item in list.EnumerateArray())
            {
                if (people.Count >= MaxPeople) break;
                var person = ReadPerson(item);
                if (person is not null) people.Add(person);
            }
            lock (Gate) { _people = people; _loadedVersion = version; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Mid-write by Ocula, or not what we expect: keep the previous list and try again at the next change.
            Debug.WriteLine("Porta: couldn't read Ocula's people index: " + ex.Message);
        }
    }

    private static PersonEntry? ReadPerson(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var id = item.TryGetProperty("id", out var idProp) ? idProp.ValueKind switch
        {
            JsonValueKind.String => idProp.GetString(),
            JsonValueKind.Number => idProp.GetRawText(),
            _ => null,
        } : null;
        var name = Clean(Str(item, "name"), 120);
        if (string.IsNullOrEmpty(id) || id.Length > 200 || string.IsNullOrEmpty(name)) return null;

        var aliases = new List<string>();
        if (item.TryGetProperty("aliases", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var alias in a.EnumerateArray().Take(50))
                if (alias.ValueKind == JsonValueKind.String && Clean(alias.GetString(), 120) is { Length: > 0 } clean)
                    aliases.Add(clean);

        var count = item.TryGetProperty("photoCount", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) && n > 0 ? n : 0;

        return new PersonEntry
        {
            Id = id,
            Name = name,
            Aliases = [.. aliases],
            Description = Clean(Str(item, "description"), 200),
            PhotoCount = count,
            ThumbnailPath = SafeThumbnail(Str(item, "thumbnail")),
        };
    }

    /// <summary>
    /// The thumbnail path only if it is an absolute path on a local drive to an existing, small PNG. Network
    /// paths are refused: opening one could reach out to another computer.
    /// </summary>
    private static string? SafeThumbnail(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
            var info = new FileInfo(path);
            return info.Exists && info.Length is > 0 and <= MaxThumbnailBytes ? info.FullName : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Trims, drops control characters (a "weird" name must never break the row) and caps the length.</summary>
    private static string? Clean(string? text, int max)
    {
        if (text is null) return null;
        var clean = new string(text.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (clean.Length > max) clean = clean[..max];
        return clean.Length == 0 ? null : clean;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
