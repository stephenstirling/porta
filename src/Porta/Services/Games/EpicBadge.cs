using Microsoft.Win32;
using Porta.Models;

namespace Porta.Services.Games;

/// <summary>
/// The small Epic badge on Epic rows. It is the icon of the Epic Games Launcher program installed on this PC,
/// read when Porta runs (the program is found through the com.epicgames.launcher link Windows registers).
/// Porta ships no Epic logo; with no launcher installed the badge is simply empty and the text label remains.
/// </summary>
public sealed class EpicBadge : IconItem
{
    public static EpicBadge Instance { get; } = Create();

    private static EpicBadge Create()
    {
        var badge = new EpicBadge();
        var exe = LauncherExe();
        if (exe is not null) App.Icons.Request(badge, exe, 32);
        return badge;
    }

    private static string? LauncherExe()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"com.epicgames.launcher\shell\open\command");
            var command = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(command)) return null;

            // "C:\...\EpicGamesLauncher.exe" %1
            command = command.Trim();
            var path = command.StartsWith('"')
                ? command[1..Math.Max(1, command.IndexOf('"', 1))]
                : command.Split(' ')[0];
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
