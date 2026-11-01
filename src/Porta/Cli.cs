using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Porta;

/// <summary>
/// The few command-line switches other programs (Integra) use to manage Porta:
///   Porta.exe --version   prints the version
///   Porta.exe --status    prints the version and whether Porta is running (exit code 0 = running, 1 = not)
///   Porta.exe --quit      closes a running Porta cleanly and waits for it to go (exit code 0 = closed or wasn't running, 1 = still running)
/// Anything else starts Porta as usual.
/// </summary>
internal static class Cli
{
    /// <summary>Named signal the running Porta waits on. "Local\" = this Windows sign-in session.</summary>
    private const string QuitSignalName = @"Local\Porta.Quit";

    /// <summary>Handles a switch and returns the exit code, or null when <paramref name="args"/> holds no switch (start Porta).</summary>
    public static int? TryRun(string[] args)
    {
        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Print($"Porta {Version}");
            return 0;
        }
        if (args.Any(a => a.Equals("--status", StringComparison.OrdinalIgnoreCase)))
        {
            bool running = IsRunning();
            Print($"Porta {Version}\nStatus: {(running ? "running" : "not running")}");
            return running ? 0 : 1;
        }
        if (args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase)))
            return Quit();
        return null;
    }

    /// <summary>The version in this exe's file properties, e.g. "0.1.0" (any "+commit" suffix dropped).</summary>
    public static string Version
    {
        get
        {
            var v = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").ProductVersion ?? "0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    /// <summary>Called once by the running copy: from now on, <c>Porta.exe --quit</c> reaches <paramref name="onQuit"/>.</summary>
    public static void ListenForQuit(Action onQuit)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName);
        var thread = new Thread(() =>
        {
            signal.WaitOne();
            onQuit();
        }) { IsBackground = true, Name = "Porta quit listener" };
        thread.Start();
        // `signal` stays referenced by the thread; Windows removes the name when this process ends.
    }

    private static bool IsRunning()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitSignalName, out var signal)) return false;
        signal.Dispose();
        return true;
    }

    private static int Quit()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitSignalName, out var signal))
        {
            Print("Porta is not running.");
            return 0;
        }
        using (signal) signal.Set();

        // The name disappears once the running copy has really exited.
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            if (!IsRunning())
            {
                Print("Porta closed.");
                return 0;
            }
        }
        Print("Porta did not close within 10 seconds.");
        return 1;
    }

    // Porta is a windowed program, so it has no console of its own. Output goes to the caller's pipe when
    // one is attached (Integra, `| more`), otherwise to the console of the terminal that started it.
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int processId);
    private const int StdOutput = -11;
    private const int AttachParentProcess = -1;

    private static void Print(string text)
    {
        try
        {
            var handle = GetStdHandle(StdOutput);
            Stream stream;
            // GetFileType is 0 (unknown) for a handle that points at nothing usable.
            if (handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != 0)
            {
                stream = Console.OpenStandardOutput();
            }
            else if (AttachConsole(AttachParentProcess))
            {
                stream = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
            }
            else
            {
                return; // started with no console and no pipe: nowhere to print
            }
            using (stream)
            {
                var bytes = new UTF8Encoding(false).GetBytes(text + "\r\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
