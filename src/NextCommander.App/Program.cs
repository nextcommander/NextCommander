using Avalonia;

namespace NextCommander.App;

internal static class Program
{
    /// <summary>Held while this process is the primary instance (named per user and settings folder).</summary>
    private static Mutex? s_primary;

    /// <summary>Pipe this process listens on as the primary instance (null: single instance off or not primary).</summary>
    public static string? PrimaryPipe { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);
        if (args.Length == 3 && args[0] == Services.Elevation.Switch)
        {
            // started with administrator rights for one operation: no window, no single-instance lock
            Environment.ExitCode = Services.Elevation.Serve(args[1], args[2]);
            return;
        }
        args = Services.AppRestart.WaitForPrevious(args); // started again by the previous process (imported settings)
        if (ForwardToRunningInstance(args))
        {
            return;
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
        finally
        {
            if (PrimaryPipe is not null)
            {
                s_primary?.ReleaseMutex();
            }
            s_primary?.Dispose();
        }
    }

    /// <summary>
    /// Single instance (setting "SingleInstance"): the first start owns a named mutex and listens on a pipe right
    /// away; a later start hands its arguments over the pipe (waiting a little while the first one is still starting).
    /// </summary>
    private static bool ForwardToRunningInstance(string[] args)
    {
        try
        {
            var store = new Core.Config.SettingsStore();
            if (args.Length > 0 && args[0] == "--find")
            {
                return false; // the separate find process runs on its own (and sends results to the main window)
            }
            if (!store.Load().SingleInstance)
            {
                return false;
            }
            var name = Services.SingleInstance.PipeName(store.Directory);
            s_primary = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (createdNew)
            {
                PrimaryPipe = name;
                return false;
            }
            // Another instance owns it: it may still be starting, so allow its listener some time to appear.
            return Services.SingleInstance.TrySend(name, args, timeoutMs: 10_000);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>
    /// A start that fails before the window (and the log) is up leaves its error in &lt;settings&gt;/logs/crash.log
    /// (macOS: also ~/Library/Logs/NextCommander/crash.log, shown by Console).
    /// </summary>
    private static void WriteCrashLog(Exception? ex)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.OSVersion} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        var folders = new List<string>();
        try
        {
            folders.Add(Path.Combine(new Core.Config.SettingsStore().Directory, "logs"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        if (OperatingSystem.IsMacOS())
        {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "NextCommander"));
        }
        foreach (var folder in folders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "crash.log"), text);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // Also used by the visual designer and headless tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
