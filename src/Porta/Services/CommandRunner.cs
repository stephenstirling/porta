using System.ComponentModel;
using System.Diagnostics;

namespace Porta.Services;

/// <summary>
/// Runs what the user types after "&gt;" in search, like the Run dialog:
/// "&gt; notepad C:\notes.txt", "&gt; ping 1.1.1.1", "&gt; \\nas\photos", "&gt; ms-settings:display".
/// </summary>
public static class CommandRunner
{
    public const char Prefix = '>';

    public static bool IsCommand(string text) => text.TrimStart().StartsWith(Prefix);

    public static string CommandText(string text) => text.TrimStart().TrimStart(Prefix).Trim();

    /// <summary>Splits "program args" into the program and the rest, honouring quotes.</summary>
    internal static (string File, string Args) Split(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }

    /// <summary>Starts the command. Returns an error message for the user, or null if it started.</summary>
    public static string? Run(string command, bool asAdmin)
    {
        if (command.Length == 0) return "Type a command after >.";
        var (file, args) = Split(command);
        try
        {
            Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = true, // same lookup as the Run dialog: PATH, App Paths, folders, links
                Verb = asAdmin ? "runas" : "",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null; // the user declined the admin prompt
        }
        catch (Win32Exception)
        {
            return $"Windows can't find “{file}”.";
        }
    }
}
