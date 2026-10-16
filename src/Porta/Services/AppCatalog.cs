using System.Collections.ObjectModel;
using Porta.Models;

namespace Porta.Services;

/// <summary>Every installed app, read from Windows' Apps folder (shell:AppsFolder).</summary>
public sealed class AppCatalog
{
    private const string AppsFolder = "shell:::{4234d49b-0245-4df3-b780-3893943456e1}";

    private static readonly string[] SkippedExtensions =
        [".url", ".chm", ".txt", ".pdf", ".htm", ".html", ".rtf", ".ini", ".hlp", ".md"];

    private readonly Dictionary<string, AppEntry> _byId = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<AppEntry> Apps { get; } = [];
    public bool IsLoaded { get; private set; }
    public event Action? Loaded;

    public bool TryGet(string id, out AppEntry app) => _byId.TryGetValue(id, out app!);

    /// <summary>Call from the UI thread.</summary>
    public async Task LoadAsync()
    {
        var found = await Sta.Run(Enumerate);

        Apps.Clear();
        _byId.Clear();
        foreach (var app in found)
        {
            if (_byId.TryAdd(app.Id, app)) Apps.Add(app);
        }

        IsLoaded = true;
        Loaded?.Invoke();

        foreach (var app in Apps) App.Icons.Request(app, app.ShellPath);
    }

    private static List<AppEntry> Enumerate()
    {
        var result = new List<AppEntry>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return result;

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic folder = shell.NameSpace(AppsFolder);
        foreach (dynamic item in folder.Items())
        {
            string name = item.Name;
            string id = item.Path;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
            if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
            if (SkippedExtensions.Any(ext => id.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new AppEntry { Name = name, Id = id });
        }

        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
