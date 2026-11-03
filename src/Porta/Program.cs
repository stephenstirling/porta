using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Porta;

/// <summary>
/// Custom entry point so only one copy of Porta ever runs.
/// Launching Porta a second time just opens the menu in the running copy.
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static int Main(string[] args)
    {
        // --version, --status and --quit answer and exit before any UI or WinRT start-up.
        if (Cli.TryRun(args) is int exitCode) return exitCode;

        WinRT.ComWrappersSupport.InitializeComWrappers();

        var instance = AppInstance.FindOrRegisterForKey("Porta.Main");
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Redirect on a worker thread; waiting on the STA thread can deadlock.
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }

        instance.Activated += (_, _) => App.ShowMenuFromAnotherLaunch();

        // From here on `Porta.exe --status` sees Porta as running and `--quit` can reach it. The UI queue is
        // created a moment later; a quit that arrives first waits for it.
        DispatcherQueue? ui = null;
        Cli.ListenForQuit(() =>
        {
            while (Volatile.Read(ref ui) is null) Thread.Sleep(50);
            ui!.TryEnqueue(App.Quit);
        });

        XamlCheckProcessRequirements();
        Application.Start(p =>
        {
            Volatile.Write(ref ui, DispatcherQueue.GetForCurrentThread());
            var context = new DispatcherQueueSynchronizationContext(ui!);
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
