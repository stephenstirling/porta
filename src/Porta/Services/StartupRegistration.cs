using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Porta.Services;

/// <summary>
/// "Start Porta when I sign in": a value named Porta in HKCU\...\Run (this user only, no admin rights). Its command
/// carries --startup, so Porta starts quietly and a second copy started that way just exits.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Porta";
    public const string StartupSwitch = "--startup";

    /// <summary>Settings > Apps > Startup, where Windows' own switch for Porta is.</summary>
    public const string WindowsStartupSettings = "ms-settings:startupapps";

    /// <summary>
    /// Writes the entry so it points at the Porta that is running now (an update moves Porta to a new folder), or
    /// removes it. A development build (bin\Debug or bin\Release) never takes over an entry that points at an
    /// installed Porta that still exists, so building Porta doesn't change what starts at sign-in.
    /// </summary>
    public static void Apply(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!on)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            if (IsDevBuild(exe) && ExeIn(key.GetValue(ValueName) as string) is { } registered
                && !IsDevBuild(registered) && File.Exists(registered)) return;
            var command = $"\"{exe}\" {StartupSwitch}";
            if (!command.Equals(key.GetValue(ValueName) as string, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, command);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Debug.WriteLine("Porta: couldn't change the sign-in entry: " + ex.Message);
        }
    }

    /// <summary>
    /// The user switched Porta off in Settings > Apps > Startup. Windows keeps that in StartupApproved: the first byte
    /// is even (2) for on, odd (3) for off. Porta never changes it back.
    /// </summary>
    public static bool IsTurnedOffInWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Integra installs each version in its own folder and doesn't start Porta again after an update, so the next
    /// sign-in still starts the old version. That copy hands over: if Integra's list names another Porta.exe as the
    /// current one (after an update or a rollback), this returns it, so it can be started instead.
    /// </summary>
    public static string? IntegraCurrentCopy()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appsFolder = Path.Combine(local, "Integra", "Apps", "porta") + Path.DirectorySeparatorChar;
            var exe = Environment.ProcessPath;
            if (exe is null || !Path.GetFullPath(exe).StartsWith(appsFolder, StringComparison.OrdinalIgnoreCase)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(local, "Integra", "installed.json")));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var record in doc.RootElement.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object
                    || !record.TryGetProperty("Id", out var id) || id.GetString() != "porta"
                    || !record.TryGetProperty("Path", out var path) || path.GetString() is not { Length: > 0 } current) continue;
                current = Path.GetFullPath(current);
                return current.StartsWith(appsFolder, StringComparison.OrdinalIgnoreCase)
                       && !current.Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)
                       && File.Exists(current) ? current : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine("Porta: couldn't read Integra's list: " + ex.Message);
        }
        return null;
    }

    private static bool IsDevBuild(string exe) =>
        exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The exe path in a Run command like <c>"C:\...\Porta.exe" --startup</c>.</summary>
    private static string? ExeIn(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }
}
