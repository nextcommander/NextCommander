using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.App.Views;
using NextCommander.Core.Commands;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Vfs.Local;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace NextCommander.App;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Headless tests have no desktop lifetime: they build their own view models and never touch user settings.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new SettingsStore();
            var settings = store.Load();
            s_logDirectory = Path.Combine(store.Directory, "logs");
            ApplyProgramLog(settings.ProgramLog);
            NextCommander.App.Services.OperationLog.DefaultFile = Path.Combine(s_logDirectory, "operations.log");
            // each connection's own log; its errors go to the program log too
            NextCommander.Core.Net.ConnectionLog.Folder = Path.Combine(s_logDirectory, "connections");
            NextCommander.Core.Net.ConnectionLog.Written += (_, entry) =>
            {
                if (entry.Level == NextCommander.Core.Net.ConnectionLogLevel.Error)
                {
                    Log.Warning("Connection {Connection} ({Address}): {Message} {Details}", entry.Connection, entry.Address, entry.Message, entry.Details);
                }
            };
            NextCommander.App.Services.ColorThemes.UserDirectory = Path.Combine(store.Directory, "themes");
            NextCommander.App.Services.Icons.IconPacks.UserDirectory = Path.Combine(store.Directory, "iconpacks");
            ThumbnailCachePath = ThumbnailCacheDirectory(store);
            NextCommander.App.Services.UiLocalization.UserDirectory = Path.Combine(store.Directory, "languages");

            Services = ConfigureServices(store, settings);
            ApplyAppearance(settings);
            NextCommander.App.Services.TitleBarTheme.Install(this);
            ApplyPanelFont(settings);
            ApplyIconSettings(settings);
            NextCommander.App.Services.UiFonts.Apply(this, settings);
            NextCommander.App.Services.UiLocalization.Load(settings.Language);
            NextCommander.Vfs.Archive.SevenZipCli.ExecutableOverride = string.IsNullOrWhiteSpace(settings.SevenZipPath) ? null : settings.SevenZipPath;

            if (TryStartStandalone(desktop, Services))
            {
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var vm = Services.GetRequiredService<MainWindowViewModel>();
            vm.Remote = Services.GetRequiredService<RemoteSession>();
            var link = Services.GetRequiredService<NextCommander.App.Services.LinkService>();
            vm.Link = link;
            link.SaveSettings = vm.SaveSettings;
            _ = link.ApplyAsync(); // sharing again when it was on at the last exit
            LoadPlugins(vm, Services.GetRequiredService<ProviderRegistry>(), store, settings);
            NextCommander.App.Services.FileIndexService.Apply(settings, store.Directory); // the own file index, in the background
            NextCommander.App.Services.MediaSupport.Positions = new NextCommander.App.Services.MediaPositions(Path.Combine(store.Directory, "media-positions.json"));
            var window = new MainWindow { DataContext = vm };
            Services.GetRequiredService<DialogService>().Owner = window;
            desktop.MainWindow = window;
            _ = vm.Tunnels.StartAutomaticAsync(); // the tunnels that start with the program (dialogs need the owner)
            desktop.ShutdownRequested += (_, _) => vm.SaveSettings();
            desktop.Exit += (_, _) =>
            {
                vm.Tunnels.StopAll();
                // Write buffered archive changes and remove temp copies.
                Services.GetRequiredService<NextCommander.Vfs.Archive.ArchiveFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.SftpFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.Link.LinkFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.App.Services.LinkService>().Dispose(); // stops sharing
                Services.GetRequiredService<NextCommander.Vfs.Remote.FtpFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.WebDavFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.S3FileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.AzureBlobFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.AzureFilesFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.Cloud.GoogleDriveFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.Cloud.OneDriveFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.Cloud.DropboxFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.Cloud.BoxFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.SmbFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                Services.GetRequiredService<NextCommander.Vfs.Remote.ScpFileSystemProvider>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                foreach (var disposable in vm.Plugins.SelectMany(p => p.FileSystems).OfType<IDisposable>())
                {
                    disposable.Dispose(); // e.g. the rclone plugin stops its API server
                }
                s_listen.Cancel();
                NextCommander.App.Services.FileIndexService.Stop();
                Log.CloseAndFlush();
            };
            _ = StartAsync(vm, window, desktop.Args ?? [], store, settings);
        }

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// <c>NextCommander --lister file</c> / <c>--edit file</c>: only the Lister or the editor, without the main window.
    /// Settings are not written back (a running main window owns them).
    /// </summary>
    private static bool TryStartStandalone(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services)
    {
        var args = desktop.Args ?? [];
        if (args.Length >= 1 && args[0] == "--find")
        {
            StartStandaloneFind(desktop, services, args.Length > 1 ? args[1] : Environment.CurrentDirectory);
            return true;
        }
        if (args.Length < 2 || args[0] is not ("--lister" or "--edit"))
        {
            return false;
        }
        var path = Path.GetFullPath(args[1]);
        if (!File.Exists(path))
        {
            Log.Warning("Standalone file not found: {Path}", path);
            return false;
        }
        var registry = services.GetRequiredService<ProviderRegistry>();
        var dialogs = services.GetRequiredService<DialogService>();
        var entryPath = VfsPath.Local(path);
        var entry = registry.Resolve(entryPath).GetEntryAsync(entryPath, CancellationToken.None).GetAwaiter().GetResult();
        if (entry is null)
        {
            return false;
        }
        ToolContext? context = null;
        context = new ToolContext(registry, services.GetRequiredService<AppSettings>(), services.GetRequiredService<IPlatformService>(), dialogs,
            e =>
            {
                dialogs.ShowEditor(e, context!);
                return Task.CompletedTask;
            });
        Window window = args[0] == "--lister"
            ? new Views.Lister.ListerWindow(new ListerRequest([entry], 0, context))
            : new Views.Editor.EditorWindow(entry, context);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        desktop.MainWindow = window;
        desktop.Exit += (_, _) => Log.CloseAndFlush();
        return true;
    }

    private static readonly CancellationTokenSource s_listen = new();

    /// <summary>
    /// Listens for later starts at once (they may come while this window is still starting: they wait for it), then
    /// restores the panels and opens the folders / files given on the command line.
    /// </summary>
    private static async Task StartAsync(MainWindowViewModel vm, MainWindow window, string[] args, SettingsStore store, AppSettings settings)
    {
        var ready = new TaskCompletionSource();
        if (Program.PrimaryPipe is { } pipe)
        {
            _ = Task.Run(() => SingleInstance.ListenAsync(pipe, async received =>
            {
                await ready.Task;
                Log.Information("Arguments from another start: {Args}", received);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await vm.HandleArgumentsAsync(received);
                    if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
                    {
                        window.WindowState = Avalonia.Controls.WindowState.Normal;
                    }
                    window.Activate();
                });
            }, s_listen.Token));
        }
        await vm.InitializeAsync();
        vm.StartDriveWatch();
        if (store.RestoredBackup is { } backup)
        {
            // the settings file was damaged: say which backup the settings came from
            Log.Warning("The settings file was damaged; restored from {Backup}", backup);
            await vm.ShowSettingsRestoredAsync(store.FilePath, backup);
        }
        if (args.Length > 0)
        {
            await vm.HandleArgumentsAsync(args);
        }
        ready.TrySetResult();
    }

    /// <summary>Plugins from &lt;settings&gt;/plugins and &lt;program&gt;/plugins: file systems join the registry, columns the column list.</summary>
    private static void LoadPlugins(MainWindowViewModel vm, ProviderRegistry registry, SettingsStore store, AppSettings settings)
    {
        var roots = new[] { Path.Combine(store.Directory, "plugins"), Path.Combine(AppContext.BaseDirectory, "plugins") };
        foreach (var plugin in NextCommander.Core.Plugins.PluginLoader.LoadAll(roots, settings.DisabledPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase)))
        {
            vm.Plugins.Add(plugin);
            if (plugin.Error is not null)
            {
                Log.Warning("Plugin {Name} not loaded: {Error}", plugin.Name, plugin.Error);
            }
            foreach (var fileSystem in plugin.FileSystems)
            {
                registry.Register(fileSystem);
                NextCommander.App.Services.OperationLog.PluginSchemes.Add(fileSystem.Scheme);
            }
            foreach (var columns in plugin.Columns)
            {
                vm.ColumnValues.Columns.Register(columns);
            }
            NextCommander.Core.Plugins.PluginCatalog.Add(plugin);
            Log.Information("Plugin {Name}: {FileSystems} file systems, {Columns} column providers, {Packers} packers, {Viewers} viewers",
                plugin.Name, plugin.FileSystems.Count, plugin.Columns.Count, plugin.Packers.Count, plugin.Viewers.Count);
        }
    }

    /// <summary>Icon settings: loaders, file list icon size and row height, menu icon size (Configuration > Icons).</summary>
    /// <summary>The thumbnail cache folder of this run (null in tests).</summary>
    public static string? ThumbnailCachePath { get; private set; }

    /// <summary>Where thumbnails are kept on disk (Configuration > Thumbnails).</summary>
    public static string ThumbnailCacheDirectory(SettingsStore store) => Path.Combine(store.Directory, "thumbs");

    public static void ApplyIconSettings(AppSettings settings)
    {
        NextCommander.App.Services.Icons.IconSettings.Apply(settings);
        NextCommander.App.Services.Icons.IconPacks.Apply(settings.IconPack);
        var size = settings.IconSize is 16 or 24 or 32 or 48 ? settings.IconSize : 16;
        var h = Math.Clamp(settings.IconSpacingH, 0, 12);
        var v = Math.Clamp(settings.IconSpacingV, 0, 12);
        if (Current is { } app)
        {
            app.Resources["FmIconSize"] = (double)size;
            app.Resources["FmIconMargin"] = new Thickness(4 + h, 0, h, 0);
            app.Resources["FmRowHeight"] = Math.Max(20d, size + (2 * v) + 2);
            app.Resources["FmMenuIconSize"] = (double)(settings.MenuIconSize is 16 or 20 or 24 ? settings.MenuIconSize : 16);
        }
        Controls.BriefListControl.IconSize = size;
        NextCommander.App.Services.MenuIcons.Frame = settings.MenuIconFrame;
        NextCommander.App.Services.MenuIcons.PressedChecks = settings.MenuChecksAsPressed;
    }

    /// <summary>File list font (settings window): DataGrid via resources, brief view via its static typeface.</summary>
    public static void ApplyPanelFont(AppSettings settings)
    {
        var family = string.IsNullOrWhiteSpace(settings.PanelFontFamily) ? Avalonia.Media.FontFamily.Default : new Avalonia.Media.FontFamily(settings.PanelFontFamily);
        var size = Math.Clamp(settings.PanelFontSize, 8, 32);
        if (Current is { } app)
        {
            app.Resources["FmPanelFontFamily"] = family;
            app.Resources["FmPanelFontSize"] = size;
        }
        Controls.BriefListControl.Font = new Avalonia.Media.Typeface(family);
        Controls.BriefListControl.PanelFontSize = size;
    }

    /// <summary>
    /// "Search in separate process": only the find window. "Feed to listbox" and "go to file" are handed to the main
    /// window over the single-instance pipe.
    /// </summary>
    private static void StartStandaloneFind(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services, string searchIn)
    {
        var registry = services.GetRequiredService<ProviderRegistry>();
        var dialogs = services.GetRequiredService<DialogService>();
        var store = services.GetRequiredService<SettingsStore>();
        var pipe = SingleInstance.PipeName(store.Directory);
        ToolContext? context = null;
        context = new ToolContext(registry, services.GetRequiredService<AppSettings>(), services.GetRequiredService<IPlatformService>(), dialogs,
            e =>
            {
                dialogs.ShowEditor(e, context!);
                return Task.CompletedTask;
            });
        Task Send(string[] message) =>
            Task.Run(() =>
            {
                if (!SingleInstance.TrySend(pipe, message, timeoutMs: 3000))
                {
                    Log.Warning("Main window not running: results not sent");
                }
            });
        var window = new Views.Search.FindFilesWindow(new FindFilesRequest(context, searchIn, null, false,
            async (_, entries) =>
            {
                var list = Path.Combine(Path.GetTempPath(), "NextCommander-results-" + Guid.NewGuid().ToString("N")[..8] + ".lst");
                await File.WriteAllLinesAsync(list, entries.Select(e => e.Path.ToString()));
                await Send(["--loadlist", list]);
            },
            entry => Send([entry.Path.ToString()])))
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Title = "Find files (separate process)",
        };
        desktop.MainWindow = window;
        desktop.Exit += (_, _) => Log.CloseAndFlush();
    }

    /// <summary>The color theme file of the settings (empty: system colors), see <see cref="Services.ColorThemes"/>.</summary>
    public static void ApplyAppearance(AppSettings settings) => ApplyColorTheme(settings.ColorThemeFile);

    /// <summary>Applies a color theme file by id ("dark"); null or empty: the system colors.</summary>
    public static void ApplyColorTheme(string? themeId)
    {
        if (Current is not null)
        {
            NextCommander.App.Services.ColorThemes.Apply(Current, themeId);
        }
    }

    private static ServiceProvider ConfigureServices(SettingsStore store, AppSettings settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(settings);
        services.AddSingleton<IPlatformService>(_ => PlatformServiceFactory.Create());
        services.AddSingleton<IFileSystemProvider, LocalFileSystemProvider>();
        services.AddSingleton<IFileSystemProvider>(sp => new ComputerFileSystemProvider(sp.GetRequiredService<IPlatformService>()));
        services.AddSingleton<IFileSystemProvider>(sp => new NetworkFileSystemProvider(sp.GetRequiredService<IPlatformService>(), () => settings.ShowAdminShares));
        services.AddSingleton(sp => new NextCommander.Platform.Mtp.MtpFileSystemProvider(sp.GetRequiredService<IPlatformService>().PortableDevices));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Platform.Mtp.MtpFileSystemProvider>());
        services.AddSingleton<IFileSystemProvider>(_ => new NextCommander.Platform.Adb.AdbFileSystemProvider(NextCommander.Platform.Adb.AdbBridge.Shared));
        services.AddSingleton<IFileSystemProvider>(sp => new NextCommander.Platform.Trash.TrashFileSystemProvider(sp.GetRequiredService<IPlatformService>().TrashStore));
        services.AddSingleton(_ => new SecretStore(store.Directory));
        services.AddSingleton<RemoteSession>();
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.SftpFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.FtpFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks) { ShowHidden = settings.FtpShowHidden });
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.SftpFileSystemProvider>());
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.ScpFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.ScpFileSystemProvider>());
        services.AddSingleton<IFileSystemProvider>(_ => new NextCommander.Vfs.Remote.HttpListingFileSystemProvider()); // http(s):// folder listings
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.FtpFileSystemProvider>());
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.WebDavFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.S3FileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.WebDavFileSystemProvider>());
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.S3FileSystemProvider>());
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.AzureBlobFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.AzureBlobFileSystemProvider>());
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.AzureFilesFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.AzureFilesFileSystemProvider>());
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.SmbFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.SmbFileSystemProvider>());
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.Cloud.GoogleDriveFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.Cloud.GoogleDriveFileSystemProvider>());
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.Cloud.OneDriveFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.Cloud.OneDriveFileSystemProvider>());
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.Cloud.DropboxFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.Cloud.DropboxFileSystemProvider>());
        services.AddSingleton(sp => new NextCommander.Vfs.Remote.Cloud.BoxFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks));
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.Cloud.BoxFileSystemProvider>());
        // PC link: the sharing side (LinkService) and the connecting side (link://)
        services.AddSingleton(sp => new NextCommander.App.Services.LinkService(settings, sp.GetRequiredService<SecretStore>(), sp.GetRequiredService<IDialogService>(),
            new LocalFileSystemProvider()));
        services.AddSingleton(sp =>
        {
            var link = sp.GetRequiredService<NextCommander.App.Services.LinkService>();
            return new NextCommander.Vfs.Remote.Link.LinkFileSystemProvider(sp.GetRequiredService<RemoteSession>().Hooks, link.Discovery, link.IdentityAsync, () => link.ComputerName);
        });
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Remote.Link.LinkFileSystemProvider>());
        services.AddSingleton<NextCommander.Vfs.Archive.ArchiveFileSystemProvider>();
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<NextCommander.Vfs.Archive.ArchiveFileSystemProvider>());
        services.AddSingleton(sp =>
        {
            var registry = new ProviderRegistry(sp.GetServices<IFileSystemProvider>());
            var archives = sp.GetRequiredService<NextCommander.Vfs.Archive.ArchiveFileSystemProvider>();
            archives.Registry = registry;
            var dialogs = sp.GetRequiredService<IDialogService>();
            // Archive reads run on worker threads: ask on the UI thread.
            archives.PasswordPrompt = (name, retry) => Dispatcher.UIThread.InvokeAsync(() => dialogs.PromptPasswordAsync("Password",
                retry ? $"{name}: wrong password. Enter it again:" : $"{name} is encrypted. Password:"));
            return registry;
        });
        services.AddSingleton<CommandRegistry>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton(_ => NextCommander.App.Services.Icons.FileIconServiceFactory.Create());
        services.AddSingleton<FileClipboard>();
        services.AddSingleton(sp => new ThumbnailService(sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<AppSettings>().ThumbnailDiskCache ? ThumbnailCacheDirectory(store) : null));
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }

    private static string? s_logDirectory;
    private static bool s_programLogOn;

    /// <summary>The program's own log on or off (Configuration > Log file > Program log); nothing is written while off.</summary>
    public static void ApplyProgramLog(bool on)
    {
        if (s_logDirectory is null || on == s_programLogOn)
        {
            return;
        }
        s_programLogOn = on;
        if (!on)
        {
            Log.Information("Program log switched off");
            Log.CloseAndFlush();
            Log.Logger = Serilog.Core.Logger.None;
            return;
        }
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(s_logDirectory, "nextcommander-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, shared: true, formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        Log.Information("NextCommander starting on {OS}", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
    }
}
