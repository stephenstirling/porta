using Porta.Models;

namespace Porta.Services;

/// <summary>
/// Ranks apps against what the user typed. Forgiving on purpose: "vsc" finds Visual Studio Code,
/// "calc" finds Calculator, and one typo ("calculater") still matches.
/// </summary>
public static class SearchService
{
    private static readonly char[] WordBreaks = [' ', '-', '_', '.', '(', ')'];

    /// <summary>
    /// Shorthands and other words people type for an app. Keys are what's typed (lower case);
    /// values are app names as Windows lists them. A synonym match ranks just below an exact name.
    /// </summary>
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rdp"] = ["Remote Desktop Connection", "Windows App", "Remote Desktop"],
        ["mstsc"] = ["Remote Desktop Connection"],
        ["remote desktop"] = ["Remote Desktop Connection", "Windows App"],
        ["cmd"] = ["Command Prompt"],
        ["terminal"] = ["Terminal", "Windows Terminal", "Command Prompt"],
        ["powershell"] = ["Windows PowerShell", "PowerShell"],
        ["taskmgr"] = ["Task Manager"],
        ["regedit"] = ["Registry Editor"],
        ["control"] = ["Control Panel"],
        ["devmgmt"] = ["Device Manager"],
        ["services"] = ["Services"],
        ["snip"] = ["Capta", "Snipping Tool"],
        ["screenshot"] = ["Capta", "Snipping Tool"],
        ["browser"] = ["Google Chrome", "Microsoft Edge", "Firefox"],
        ["text editor"] = ["Notepad", "Visual Studio Code"],
        ["photo viewer"] = ["Ocula", "Photos"],
        ["email"] = ["Outlook", "Mail"],
        ["calc"] = ["Calculator"],
        ["vscode"] = ["Visual Studio Code"],
        ["vs code"] = ["Visual Studio Code"],
        ["explorer"] = ["File Explorer"],
        ["files"] = ["File Explorer"],
        ["store"] = ["Microsoft Store"],
    };

    public static List<AppEntry> Rank(IEnumerable<AppEntry> apps, string query, int max = 8)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        Synonyms.TryGetValue(query, out var synonymTargets);

        return apps
            .Select(a => (App: a, Score: Math.Max(Score(a.Name, query), SynonymScore(a.Name, synonymTargets))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.App.Name.Length)
            .Take(max)
            .Select(x => x.App)
            .ToList();
    }

    private static int SynonymScore(string name, string[]? targets)
    {
        if (targets is null) return 0;
        var i = Array.FindIndex(targets, t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : 950 - i; // keep the list's order of preference
    }

    public static int Score(string name, string query)
    {
        var n = name.ToLowerInvariant();
        var q = query.ToLowerInvariant();

        if (n == q) return 1000;
        if (n.StartsWith(q)) return 900 - Math.Min(n.Length - q.Length, 100);

        var words = n.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q))) return 750;

        if (q.Length > 1 && words.Length > 1)
        {
            var initials = string.Concat(words.Select(w => w[0]));
            if (initials.StartsWith(q)) return 650;
        }

        int index = n.IndexOf(q, StringComparison.Ordinal);
        if (index >= 0) return 500 - index;

        // Letters in order with gaps: "phts" -> "Photos"
        int qi = 0, gaps = 0, last = -1;
        for (int i = 0; i < n.Length && qi < q.Length; i++)
        {
            if (n[i] != q[qi]) continue;
            if (last >= 0) gaps += i - last - 1;
            last = i;
            qi++;
        }
        if (qi == q.Length && q.Length >= 2) return Math.Max(10, 300 - gaps * 15);

        // One typo against the start of any word
        if (q.Length >= 4)
        {
            foreach (var w in words)
            {
                var head = w.Length > q.Length ? w[..q.Length] : w;
                if (Distance(head, q) <= 1) return 150;
            }
        }

        return 0;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
