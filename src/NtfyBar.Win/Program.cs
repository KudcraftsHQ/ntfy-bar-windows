using System.Runtime.InteropServices;
using Microsoft.Toolkit.Uwp.Notifications;
using NtfyBar.Core;

namespace NtfyBar.Win;

internal static class Program
{
    private const string MutexName = @"Local\Kudcrafts.ntfy-bar-windows";
    private const string ShowEventName = @"Local\Kudcrafts.ntfy-bar-windows.show";

    /// <summary>UI-thread context; toast activation and system events arrive on other threads.</summary>
    public static SynchronizationContext Ui { get; private set; } = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run(args);
        if (args.Contains("--uninstall")) return Uninstall();

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Already running: ask that instance to open its message window, then leave.
            // A toast click that cold-starts us also lands here only if another instance is up.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { /* not ready */ }
            return 0;
        }

        Storage.EnsureDirectories();
        Log.FilePath = Storage.LogPath;
        Log.RotateIfNeeded();
        Log.Write($"launch v{AppInfo.Version}");

        ApplicationConfiguration.Initialize();
        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        Ui = ui;

        Application.ThreadException += (_, e) => Log.Write($"ui: unhandled: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write($"task: unobserved: {e.Exception.GetBaseException().Message}"); e.SetObserved(); };

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var app = new TrayApp(autostart: args.Contains("--autostart"));
        var wait = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => Ui.Post(_ => app.ShowMessages(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            Application.Run(app);
        }
        finally
        {
            wait.Unregister(null);
        }
        Log.Write("quit");
        return 0;
    }

    private static int Uninstall()
    {
        ConsoleAttach.Attach();
        Autostart.Set(false);
        Credentials.Delete(Credentials.Password);
        Credentials.Delete(Credentials.Token);
        try { ToastNotificationManagerCompat.Uninstall(); } catch (Exception e) { Console.WriteLine($"toasts: {e.Message}"); }
        Console.WriteLine("ntfy-bar: removed autostart, saved credentials and the notification registration.");
        Console.WriteLine($"Settings and history are left in {Storage.RoamingDir} and {Storage.LocalDir}.");
        return 0;
    }
}

internal static class AppInfo
{
    public static string Version
    {
        get
        {
            var v = Application.ProductVersion;
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    public const string Name = "ntfy-bar";
}

internal static class ConsoleAttach
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    /// <summary>WinExe has no console; attach to the parent's so --selftest/--uninstall can print.</summary>
    public static void Attach()
    {
        try
        {
            if (!Console.IsOutputRedirected) AttachConsole(-1);
        }
        catch { /* best effort */ }
    }
}
