using Microsoft.Win32;

namespace Porta.Services.Games;

/// <summary>Finds the program Windows has registered for a link scheme such as battlenet: (read from this PC's registry).</summary>
public static class ProtocolHandler
{
    /// <summary>
    /// The program file registered to open <paramref name="scheme"/> links, if it exists. The command is
    /// read as text and never run here.
    /// </summary>
    public static string? ExePath(string scheme)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme + @"\shell\open\command");
            var command = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(command)) return null;

            // "C:\...\Program.exe" "%1"   or   C:\...\Program.exe %1
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
