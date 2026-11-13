using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Porta.Services;

/// <summary>
/// Whether Windows has updates waiting to be installed when the PC restarts or shuts down, so the power menu can
/// offer "Update and restart" and "Update and shut down" the way the Windows Start menu does. Everything read here
/// is readable without admin rights. Checked when the menu or the power menu opens, at most once a minute.
/// </summary>
public static class UpdateStatus
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private static readonly object Gate = new();
    private static Task<bool>? _check;
    private static DateTime _checkedAt;

    /// <summary>Set by the hidden --simulate-pending-update switch: always "pending", and the update actions only show a message.</summary>
    public static bool Simulated { get; set; }

    /// <summary>The last answer (false until the first check finishes).</summary>
    public static bool IsPending { get; private set; }

    /// <summary>The cached answer if it's under a minute old, otherwise a fresh check on a background thread.</summary>
    public static Task<bool> CheckAsync()
    {
        if (Simulated) return Task.FromResult(IsPending = true);
        lock (Gate)
        {
            if (_check is not null && (!_check.IsCompleted || DateTime.UtcNow - _checkedAt < CacheFor)) return _check;
            _checkedAt = DateTime.UtcNow;
            return _check = Task.Run(() => IsPending = Check());
        }
    }

    /// <summary>
    /// True when any of these says so (each is only set by Windows Update, so any one is enough):
    /// 1. Update Orchestrator's ShutdownFlyoutOptions is non-zero: the value the Windows Start menu itself follows to
    ///    show its "Update and ..." items. Undocumented, but it is exactly the signal being imitated.
    /// 2. The Windows Update Agent's ISystemInformation.RebootRequired: documented; true once installed updates need a restart.
    /// 3. The "Auto Update\RebootRequired" key exists: the same state as 2, kept in the registry by Windows Update.
    /// Component Based Servicing's RebootPending is not used: turning a Windows feature on or off sets it too, so it
    /// doesn't mean "updates" and "Update and restart" would be offered when there are none.
    /// </summary>
    private static bool Check() => OrchestratorSaysPending() || AgentSaysRebootRequired() || RebootRequiredKeyExists();

    private static bool OrchestratorSaysPending()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\Orchestrator");
            return key?.GetValue("ShutdownFlyoutOptions") is int options && options != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool RebootRequiredKeyExists()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool AgentSaysRebootRequired()
    {
        object? info = null;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo");
            if (type is null) return false;
            info = Activator.CreateInstance(type);
            return info is not null && type.InvokeMember("RebootRequired", BindingFlags.GetProperty, null, info, null) is true;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException or MissingMethodException)
        {
            System.Diagnostics.Debug.WriteLine("Porta: couldn't ask Windows Update: " + ex.Message);
            return false;
        }
        finally
        {
            if (info is not null && Marshal.IsComObject(info)) Marshal.FinalReleaseComObject(info);
        }
    }
}
