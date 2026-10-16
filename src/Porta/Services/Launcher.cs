using System.ComponentModel;
using System.Diagnostics;
using Porta.Interop;
using Porta.Models;

namespace Porta.Services;

public static class Launcher
{
    public static void Open(string target, string? verb = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = verb ?? "" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user said no to the admin prompt. Nothing to do.
        }
    }

    public static void OpenApp(AppEntry app, bool asAdmin = false) => Open(app.ShellPath, asAdmin ? "runas" : null);

    public static void OpenFolder(string shellFolder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", shellFolder) { UseShellExecute = true });

    public static void ShowRunDialog() =>
        OpenFolder("shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}");

    public static void Lock() => Native.LockWorkStation();
    public static void Sleep() => Native.SetSuspendState(false, false, false);
    public static void SignOut() => RunShutdown("/l");
    public static void Restart() => RunShutdown("/r /t 0");
    public static void ShutDown() => RunShutdown("/s /hybrid /t 0"); // /hybrid keeps Fast Startup working

    private static void RunShutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
}
