using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.App.ViewModels;
using NextCommander.App.Views;
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Lister;
using NextCommander.App.Views.Search;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Commands;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;
using NextCommander.Core.Operations;
using NextCommander.Core.Rename;
using NextCommander.Core.Search;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Vfs.Local;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// The screenshots of the documentation (docs/screenshots): 1920x1080, English, made from sample files only. Nothing of
/// the machine shows: the sample folder is mapped to a drive letter of its own (subst), the drive list and the free space
/// are fixed, the file times are fixed, the culture is en-US.
/// Run alone: FM_DOC_SCREENSHOTS=&lt;folder&gt; dotnet test --filter DocScreenshots (Windows; does nothing without it).
/// </summary>
[Collection("Appearance")]
public sealed class DocScreenshots
{
    private const int Width = 1920;
    private const int Height = 1080;

    private static readonly DateTime s_time = new(2026, 11, 3, 9, 12, 0);

    private readonly List<Window> _windows = [];
    private string _out = string.Empty;
    private string _drive = string.Empty; // "X:"
    private string _temp = string.Empty;
    private FakeDialogs _dialogs = new();
    private NextCommander.Vfs.Archive.ArchiveFileSystemProvider _archives = new();

    private string D(params string[] parts) => Path.Combine([_drive + "\\", .. parts]);

    [AvaloniaFact]
    public async Task Make_documentation_screenshots()
    {
        if (Environment.GetEnvironmentVariable("FM_DOC_SCREENSHOTS") is not { Length: > 0 } output || !OperatingSystem.IsWindows())
        {
            return;
        }
        _out = output;
        Directory.CreateDirectory(_out);
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var localizer = Localizer.Current;
        var defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        // the background threads (searches, comparisons) format with the default culture
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        Localizer.Current = null;
        _temp = Path.Combine(Path.GetTempPath(), "nc-doc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
        _drive = Subst(_temp);
        PanelViewModel.DriveSpaceOverride = path => path.StartsWith(_drive, StringComparison.OrdinalIgnoreCase)
            ? ("Data", 612L * 1024 * 1024 * 1024 + 345_678_912, 931L * 1024 * 1024 * 1024 + 512_000_000)
            : ("System", 238L * 1024 * 1024 * 1024 + 123_456_789, 476L * 1024 * 1024 * 1024 + 734_003_200);
        try
        {
            MakeSampleFiles();
            await MainWindowsAsync();
            await ToolWindowsAsync();
            await SettingsAsync();
            await DialogsAsync();
        }
        finally
        {
            foreach (var window in _windows)
            {
                window.Close();
            }
            App.ApplyColorTheme(null);
            PanelViewModel.DriveSpaceOverride = null;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
            CultureInfo.DefaultThreadCurrentCulture = defaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = defaultUiCulture;
            Localizer.Current = localizer;
            Unsubst(_drive);
            try
            {
                Directory.Delete(_temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ the sample files

    private void MakeSampleFiles()
    {
        var site = D("Projects", "Website");
        Write(site, "README.md", Readme);
        Write(site, "CHANGELOG.md", "# Changelog\n\n## 1.4.0\n- New gallery page\n- Faster image loading\n\n## 1.3.2\n- Fixed the contact form\n");
        Write(site, "LICENSE", "MIT License\n\nCopyright (c) 2026 Example\n\nPermission is hereby granted, free of charge, to any person obtaining a copy...\n");
        Write(site, "package.json", "{\n  \"name\": \"website\",\n  \"version\": \"1.4.0\",\n  \"scripts\": { \"build\": \"vite build\", \"dev\": \"vite\" }\n}\n");
        Write(site, "index.html", "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <title>Example</title>\n  <link rel=\"stylesheet\" href=\"styles.css\">\n</head>\n<body>\n  <h1>Hello, world</h1>\n  <script src=\"app.js\"></script>\n</body>\n</html>\n");
        Write(site, "styles.css", "body {\n  font-family: system-ui, sans-serif;\n  margin: 0 auto;\n  max-width: 60rem;\n}\n\nh1 { color: #0b5cad; }\n");
        Write(site, "app.js", "document.addEventListener('DOMContentLoaded', () => {\n  console.log('ready');\n});\n");
        Write(site, "robots.txt", "User-agent: *\nAllow: /\n");
        Write(site, "sitemap.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"></urlset>\n");
        Write(site, "notes.txt", "Ideas\n- dark mode\n- search page\n- image captions\n");
        Write(site, "deploy.ps1", "param([string]$Target = 'staging')\nnpm run build\nWrite-Host \"Deploying to $Target\"\n");
        Write(site, ".gitignore", "node_modules/\ndist/\n");
        Write(Path.Combine(site, "src"), "Program.cs", ProgramCs);
        Write(Path.Combine(site, "src"), "Gallery.cs", "namespace Website;\n\n/// <summary>The photo gallery.</summary>\npublic sealed class Gallery\n{\n    public List<string> Photos { get; } = [];\n}\n");
        Write(Path.Combine(site, "src"), "Contact.cs", "namespace Website;\n\npublic sealed record Contact(string Name, string Email, string Message);\n");
        Write(Path.Combine(site, "docs"), "guide.md", "# Guide\n\nHow to build and deploy the website.\n");
        Write(Path.Combine(site, "tests"), "GalleryTests.cs", "public sealed class GalleryTests\n{\n}\n");
        Directory.CreateDirectory(Path.Combine(site, "assets"));
        Image(Path.Combine(site, "assets", "logo.png"), 256, 256, 3);
        Image(Path.Combine(site, "assets", "hero.png"), 1280, 720, 5);

        // the older copy (synchronize, compare)
        var backup = D("Backup", "Website 2026-10");
        foreach (var file in new[] { "README.md", "LICENSE", "package.json", "index.html", "robots.txt", "app.js" })
        {
            Directory.CreateDirectory(backup);
            File.Copy(Path.Combine(site, file), Path.Combine(backup, file));
        }
        Write(backup, "styles.css", "body {\n  font-family: sans-serif;\n  margin: 0 auto;\n}\n");
        Write(backup, "old-banner.html", "<div class=\"banner\">Sale</div>\n");
        Write(Path.Combine(backup, "src"), "Program.cs", ProgramCs.Replace("Welcome", "Hello", StringComparison.Ordinal).Replace("    app.MapGet(\"/gallery\", () => gallery.Photos);\n", string.Empty, StringComparison.Ordinal));
        Write(Path.Combine(backup, "src"), "Gallery.cs", File.ReadAllText(Path.Combine(site, "src", "Gallery.cs")));

        // photos (thumbnails, multi-rename, duplicates)
        var photos = D("Photos", "Holiday 2026");
        string[] names = ["IMG_0412", "IMG_0413", "IMG_0418", "IMG_0421", "IMG_0425", "IMG_0430", "IMG_0433", "IMG_0441", "IMG_0447", "IMG_0452", "IMG_0458", "IMG_0460"];
        for (var i = 0; i < names.Length; i++)
        {
            Image(Path.Combine(photos, names[i] + ".png"), 800, 600, i);
        }
        Directory.CreateDirectory(D("Backup", "Photos"));
        foreach (var name in names.Take(4))
        {
            File.Copy(Path.Combine(photos, name + ".png"), D("Backup", "Photos", name + ".png"));
        }
        File.Copy(Path.Combine(photos, names[5] + ".png"), D("Backup", "Photos", "beach copy.png"));

        Write(D("Archives"), "release-notes.txt", "Website 1.4.0\n");

        // folder sizes
        var random = new Random(7);
        foreach (var (folder, prefix, ext, count, size) in new[]
        {
            ("Photos", "photo", ".jpg", 40, 60_000), ("Photos/2025", "photo", ".jpg", 25, 80_000), ("Videos", "clip", ".mp4", 5, 900_000),
            ("Documents", "report", ".pdf", 30, 20_000), ("Documents", "letter", ".docx", 20, 15_000), ("Music", "track", ".mp3", 24, 120_000),
            ("Source", "file", ".cs", 80, 3_000), ("Source/bin", "library", ".dll", 10, 50_000),
        })
        {
            var dir = D("Media", folder.Replace('/', '\\'));
            Directory.CreateDirectory(dir);
            for (var i = 0; i < count; i++)
            {
                File.WriteAllBytes(Path.Combine(dir, $"{prefix}{i + 1:00}{ext}"), new byte[size / 2 + random.Next(size)]);
            }
        }

        // fixed times everywhere: files first, then the folders (deepest first)
        var n = 0;
        foreach (var file in Directory.GetFiles(_temp, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            File.SetLastWriteTime(file, s_time.AddMinutes(-(n++ * 97 % 20_000)));
        }
        // the backup is two weeks older than the website
        foreach (var file in Directory.GetFiles(D("Backup"), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTime(file, File.GetLastWriteTime(file).AddDays(-14));
        }

        // an archive of the website (made after the times are fixed: its entries keep them)
        var archive = D("Archives", "website-1.4.0.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.GetFiles(site, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                zip.CreateEntryFromFile(file, Path.GetRelativePath(site, file).Replace('\\', '/'));
            }
        }
        File.SetLastWriteTime(archive, s_time.AddDays(-2));
        foreach (var dir in Directory.GetDirectories(_temp, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            Directory.SetLastWriteTime(dir, s_time.AddHours(-(n++ % 50)));
        }
    }

    private const string Readme = """
        # Website

        The source of the **example website**: pages, styles and the photo gallery.

        ## Build

        1. Install the packages: `npm install`
        2. Start the development server: `npm run dev`
        3. Build for production: `npm run build`

        ## Folders

        | Folder | Content |
        |--------|---------|
        | `src` | Server code |
        | `assets` | Images and fonts |
        | `docs` | Guides |

        > Deploy with `deploy.ps1 -Target production`.

        See the [guide](docs/guide.md) for details.
        """;

    private const string ProgramCs = """
        using Microsoft.AspNetCore.Builder;

        namespace Website;

        // Entry point of the website: routes and static files
        internal static class Program
        {
            private static void Main(string[] args)
            {
                var builder = WebApplication.CreateBuilder(args);
                var app = builder.Build();
                var gallery = new Gallery();

                app.UseStaticFiles();
                app.MapGet("/", () => "Welcome to the example website");
                app.MapGet("/gallery", () => gallery.Photos);
                app.Run();
            }
        }
        """;

    private static void Write(string dir, string name, string text)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
    }

    /// <summary>A small landscape picture (sky, sun, hills) in one of a few color moods, saved as PNG.</summary>
    private static void Image(string path, int width, int height, int seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        (Color Top, Color Bottom, Color Hill, Color Sun)[] moods =
        [
            (Color.Parse("#3a7bd5"), Color.Parse("#a8d8ff"), Color.Parse("#2e8b57"), Color.Parse("#ffd54f")),
            (Color.Parse("#ff7e5f"), Color.Parse("#feb47b"), Color.Parse("#6b4226"), Color.Parse("#fff3b0")),
            (Color.Parse("#0f2027"), Color.Parse("#2c5364"), Color.Parse("#1b3a2f"), Color.Parse("#f5f5f5")),
            (Color.Parse("#56ccf2"), Color.Parse("#e0f7fa"), Color.Parse("#c2a35a"), Color.Parse("#ffeb3b")),
            (Color.Parse("#614385"), Color.Parse("#516395"), Color.Parse("#2b5876"), Color.Parse("#ffcc80")),
            (Color.Parse("#2193b0"), Color.Parse("#6dd5ed"), Color.Parse("#1e5631"), Color.Parse("#fff59d")),
        ];
        var mood = moods[seed % moods.Length];
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(mood.Top, 0), new GradientStop(mood.Bottom, 1) },
            }, new Rect(0, 0, width, height));
            var sunX = width * (0.2 + (seed * 0.13 % 0.6));
            ctx.DrawEllipse(new SolidColorBrush(mood.Sun), null, new Point(sunX, height * 0.3), height * 0.09, height * 0.09);
            var hills = new StreamGeometry();
            using (var g = hills.Open())
            {
                g.BeginFigure(new Point(0, height), true);
                g.LineTo(new Point(0, height * 0.7));
                g.CubicBezierTo(new Point(width * 0.25, height * (0.5 + seed % 3 * 0.05)), new Point(width * 0.5, height * 0.85), new Point(width * 0.75, height * 0.62));
                g.CubicBezierTo(new Point(width * 0.85, height * 0.55), new Point(width * 0.95, height * 0.65), new Point(width, height * 0.6));
                g.LineTo(new Point(width, height));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(mood.Hill), null, hills);
        }
        bitmap.SavePng(path);
    }

    // ------------------------------------------------------------------ the main window

    private async Task<(MainWindow Window, MainWindowViewModel Vm)> OpenAsync(string left, string right, Action<AppSettings>? configure = null)
    {
        var settings = new AppSettings();
        settings.Left.Path = left;
        settings.Right.Path = right;
        configure?.Invoke(settings);
        _dialogs = new FakeDialogs();
        _archives = new NextCommander.Vfs.Archive.ArchiveFileSystemProvider();
        var registry = new ProviderRegistry([new LocalFileSystemProvider(), _archives]);
        _archives.Registry = registry;
        var platform = PlatformProxy.Create(PlatformServiceFactory.Create(),
        [
            new DriveItem("C:\\", "C:\\", "System", 476L * 1024 * 1024 * 1024, 238L * 1024 * 1024 * 1024, DriveKind.Fixed, true),
            new DriveItem(_drive + "\\", _drive + "\\", "Data", 931L * 1024 * 1024 * 1024, 612L * 1024 * 1024 * 1024, DriveKind.Fixed, true),
        ]);
        var vm = new MainWindowViewModel(registry, platform, settings, new SettingsStore(D("Settings")), _dialogs, new JobQueue(), new CommandRegistry());
        var window = new MainWindow { DataContext = vm, Width = Width, Height = Height };
        _windows.Add(window);
        window.Show();
        await vm.InitializeAsync();
        await SettleAsync(10);
        return (window, vm);
    }

    private static void Cursor(PanelViewModel panel, string name)
    {
        var index = panel.Items.ToList().FindIndex(i => i.FullName == name);
        Assert.True(index >= 0, name);
        panel.MoveCursorTo(index);
    }

    private static void Mark(PanelViewModel panel, params string[] names)
    {
        foreach (var item in panel.Items.Where(i => names.Contains(i.FullName)))
        {
            item.IsMarked = true;
        }
    }

    private void Save(Window window, string name) => Save(window.CaptureRenderedFrame()!, name);

    private void Save(Bitmap bitmap, string name)
    {
        Assert.Equal(new PixelSize(Width, Height), bitmap.PixelSize);
        bitmap.SavePng(Path.Combine(_out, name));
    }

    private static async Task CloseMenusAsync(MainWindow w)
    {
        foreach (var menu in w.GetLogicalDescendants().OfType<Menu>())
        {
            menu.Close();
        }
        await SettleAsync(4);
    }

    private async Task<MenuItem> OpenMenuAsync(MainWindow w, string header)
    {
        var menu = w.GetLogicalDescendants().OfType<MenuItem>().First(m => m.Header as string == header);
        menu.IsSubMenuOpen = true;
        await SettleAsync(6);
        return menu;
    }

    private async Task MainWindowsAsync()
    {
        var site = D("Projects", "Website");
        var (w, vm) = await OpenAsync(site, D("Backup", "Website 2026-10"));
        await vm.LeftSide.OpenTabAsync(VfsPath.Local(D("Photos", "Holiday 2026")), activate: false);
        await vm.LeftSide.OpenTabAsync(VfsPath.Local(D("Archives")), activate: false);
        vm.Activate(vm.Left);
        Cursor(vm.Left, "README.md");
        Mark(vm.Left, "index.html", "styles.css", "app.js");
        await SettleAsync();

        foreach (var (theme, name) in new[] { ((string?)null, "main-light.png"), ("dark", "main-dark.png") })
        {
            App.ApplyColorTheme(theme);
            await SettleAsync(6);
            Save(w, name);
        }
        App.ApplyColorTheme(null);
        await SettleAsync(4);

        // menus
        var files = await OpenMenuAsync(w, "_Files");
        Save(w, "menu-files.png");
        await CloseMenusAsync(w);
        var show = await OpenMenuAsync(w, "_Show");
        Save(w, "menu-show.png");
        await CloseMenusAsync(w);
        await SettleAsync(4);

        // other themes with the Files menu open
        foreach (var (theme, name) in new[] { ("ubuntu", "main-ubuntu.png"), ("macos-dark", "main-macos-dark.png") })
        {
            App.ApplyColorTheme(theme);
            await SettleAsync(6);
            files = await OpenMenuAsync(w, "_Files");
            Save(w, name);
            await CloseMenusAsync(w);
            await SettleAsync(4);
        }
        App.ApplyColorTheme(null);
        await SettleAsync(4);

        // icon packs
        foreach (var id in new[] { "colored", "fluent", "monochrome" })
        {
            vm.Settings.IconPack = id;
            vm.ApplySettings();
            await SettleAsync(6);
            Save(w, $"icons-{id}.png");
        }
        vm.Settings.IconPack = IconPack.DefaultId;
        vm.ApplySettings();
        await SettleAsync(4);

        // quick view of the code
        await vm.Left.NavigateAsync(VfsPath.Local(Path.Combine(site, "src")));
        Cursor(vm.Left, "Program.cs");
        await vm.ExecuteAsync("nc_QuickView");
        await SettleAsync(4);
        await w.LoadQuickViewAsync();
        await w.QuickView!.Loading!;
        await SettleAsync(6);
        Save(w, "quick-view.png");
        await vm.ExecuteAsync("nc_QuickView");
        await SettleAsync(4);

        // custom columns
        await vm.Left.NavigateAsync(VfsPath.Local(site));
        vm.Left.ChooseColumnSet("Source code");
        await WaitUntilAsync(() => vm.Left.Items.Where(i => i.Entry is { IsDirectory: false }).All(i => i.Custom is [_, _] && i.Custom[0] != "…"));
        await SettleAsync(6);
        Save(w, "custom-columns.png");
        vm.Left.ChooseColumnSet(null);

        // tree and thumbnails
        vm.ShowTreePane = true;
        await vm.Right.NavigateAsync(VfsPath.Local(D("Photos", "Holiday 2026")));
        vm.Right.ViewMode = PanelViewMode.Thumbnails;
        vm.Activate(vm.Right);
        Cursor(vm.Right, "IMG_0421.png");
        await SettleAsync(40);
        Save(w, "tree-and-thumbnails.png");
        vm.Right.ViewMode = PanelViewMode.Full;
        vm.ShowTreePane = false;
        vm.Activate(vm.Left);

        // inside a zip archive
        await vm.Right.NavigateAsync(VfsPath.Local(D("Archives")));
        await vm.Left.NavigateAsync(_archives.GetRoot(VfsPath.Local(D("Archives", "website-1.4.0.zip"))));
        await SettleAsync(10);
        Save(w, "archive.png");

        // folder sizes in place of the other panel
        await vm.Left.NavigateAsync(VfsPath.Local(D("Media")));
        vm.Activate(vm.Left);
        await SettleAsync(4);
        await vm.ExecuteAsync("nc_DiskUsage");
        await SettleAsync(3);
        var usage = w.GetVisualDescendants().OfType<DiskUsageView>().Single();
        await WaitUntilAsync(() => usage.Root is not null);
        await usage.Running!;
        await SettleAsync(6);
        if (usage.Map.Rectangles.FirstOrDefault(r => r.Node.Extension == ".jpg" && r.Node.Parent?.Name == "2025") is { Node: { } photo })
        {
            usage.Reveal(photo);
        }
        await SettleAsync(10);
        Save(w, "disk-usage.png");
    }

    // ------------------------------------------------------------------ tool windows over the main window

    private async Task ToolWindowsAsync()
    {
        var site = D("Projects", "Website");
        var (w, vm) = await OpenAsync(site, D("Backup", "Website 2026-10"));
        Cursor(vm.Left, "README.md");
        await SettleAsync();
        var background = w.CaptureRenderedFrame()!;

        async Task Shot(Window tool, string name, double width, double height, Func<Task>? prepare = null)
        {
            tool.Width = width;
            tool.Height = height;
            _windows.Add(tool);
            tool.Show();
            await SettleAsync(6);
            if (prepare is not null)
            {
                await prepare();
            }
            await SettleAsync(6);
            Save(Compose(background, tool.CaptureRenderedFrame()!, tool.Title ?? string.Empty), name);
            tool.Close();
        }

        // Lister and editor
        var code = Path.Combine(site, "src", "Program.cs");
        var lister = new ListerWindow(new ListerRequest([new LocalEntryReader().Read(code)], 0, vm.Tools));
        await Shot(lister, "lister.png", 1200, 760, async () =>
        {
            await WaitUntilAsync(() => lister.View.Entry is not null && lister.View.Loading is { IsCompleted: true });
        });
        App.ApplyColorTheme("dark");
        await SettleAsync(6);
        var dark = w.CaptureRenderedFrame()!;
        var listerDark = new ListerWindow(new ListerRequest([new LocalEntryReader().Read(code)], 0, vm.Tools)) { Width = 1200, Height = 760 };
        _windows.Add(listerDark);
        listerDark.Show();
        await WaitUntilAsync(() => listerDark.View.Entry is not null && listerDark.View.Loading is { IsCompleted: true });
        await SettleAsync(8);
        Save(Compose(dark, listerDark.CaptureRenderedFrame()!, listerDark.Title ?? string.Empty, dark: true), "lister-dark.png");
        listerDark.Close();
        App.ApplyColorTheme(null);
        await SettleAsync(6);

        var markdown = new ListerWindow(new ListerRequest([new LocalEntryReader().Read(Path.Combine(site, "README.md"))], 0, vm.Tools));
        await Shot(markdown, "markdown-preview.png", 1200, 760, async () =>
        {
            await WaitUntilAsync(() => markdown.View.Entry is not null && markdown.View.Loading is { IsCompleted: true });
        });

        var editor = new EditorWindow(new LocalEntryReader().Read(code), vm.Tools) { AskSaveOnClose = () => Task.FromResult(SaveChoice.Discard) };
        await Shot(editor, "editor.png", 1200, 760, async () => await WaitUntilAsync(() => editor.Loading is { IsCompleted: true }));

        // find files
        var find = new FindFilesWindow(new FindFilesRequest(vm.Tools, D("Projects"), null, false, vm.FeedSearchResultsAsync, vm.GoToFileAsync));
        await Shot(find, "find-files.png", 1200, 760, async () =>
        {
            find.SetFields("*.cs *.md", D("Projects"), "Gallery");
            await find.StartAsync();
        });

        // multi-rename
        var photos = (await vm.Tools.Registry.Resolve(VfsPath.Local(D("Photos", "Holiday 2026"))).ListAsync(VfsPath.Local(D("Photos", "Holiday 2026")), CancellationToken.None))
            .Where(e => !e.IsDirectory).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var rename = new MultiRenameWindow(photos, vm.Tools, () => Task.CompletedTask);
        await Shot(rename, "multi-rename.png", 1200, 760, () =>
        {
            rename.Apply(new MultiRenameOptions { NameMask = "Holiday_[C]", CounterDigits = 3 });
            return Task.CompletedTask;
        });

        // synchronize and compare
        // a page of the main window with its own tab
        var sync = new SyncDirsView(VfsPath.Local(site), VfsPath.Local(D("Backup", "Website 2026-10")), vm.Tools);
        w.ShowSync(sync);
        await SettleAsync(6);
        await sync.CompareAsync();
        await SettleAsync(8);
        Save(w, "synchronize.png");
        sync.Close();
        await SettleAsync(3);
        var reader = new LocalEntryReader();
        var compare = new CompareWindow(reader.Read(D("Backup", "Website 2026-10", "src", "Program.cs")), reader.Read(code), vm.Tools);
        await Shot(compare, "compare.png", 1300, 800, async () => await WaitUntilAsync(() => compare.Comparing is { IsCompleted: true }));

        // duplicate files
        await vm.Left.NavigateAsync(VfsPath.Local(D("Photos")));
        await vm.Right.NavigateAsync(VfsPath.Local(D("Backup", "Photos")));
        await vm.ExecuteAsync("nc_FindDuplicates");
        var duplicates = new DuplicatesWindow(_dialogs.LastDuplicates!) { Confirm = false };
        await Shot(duplicates, "duplicates.png", 1200, 760, async () =>
        {
            await duplicates.StartAsync();
            duplicates.MarkAllBut(DuplicateKeep.FirstFolder);
        });
    }

    // ------------------------------------------------------------------ settings and dialogs

    private async Task SettingsAsync()
    {
        var (w, vm) = await OpenAsync(D("Projects", "Website"), D("Backup", "Website 2026-10"));
        var background = w.CaptureRenderedFrame()!;
        foreach (var (page, name) in new[]
        {
            ("Layout", "configuration.png"), ("Display/Color", "configuration-color.png"), ("Display/Icons", "configuration-icons.png"),
            ("Packer/7-Zip", "configuration-7zip.png"), ("Display/Language", "languages.png"),
        })
        {
            var settings = new SettingsWindow(vm.Settings, () => { }, []) { Width = 1100, Height = 780 };
            _windows.Add(settings);
            settings.Show();
            settings.SelectPage(page);
            await SettleAsync(8);
            // the processor count of the machine stays out ("0 = 7-Zip decides (16 processors).")
            foreach (var text in settings.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text?.Contains(" processors)", StringComparison.Ordinal) == true))
            {
                text.Text = System.Text.RegularExpressions.Regex.Replace(text.Text!, @" \(\d+ processors\)", string.Empty);
            }
            await SettleAsync(2);
            if (page == "Display/Language")
            {
                settings.GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IEnumerable<string> items && items.Contains("Türkçe")).IsDropDownOpen = true;
                await SettleAsync(8);
            }
            Save(Compose(background, settings.CaptureRenderedFrame()!, settings.Title ?? "Configuration"), name);
            settings.Close();
        }
    }

    private async Task DialogsAsync()
    {
        var (w, vm) = await OpenAsync(D("Projects", "Website"), D("Backup", "Website 2026-10"));
        var background = w.CaptureRenderedFrame()!;

        // connection manager
        var sample = new AppSettings();
        sample.Connections.AddRange(new[]
        {
            (RemoteProtocol.Sftp, "Web server", "server.example.com", "Work/Production"), (RemoteProtocol.Sftp, "Web server 2", "server2.example.com", "Work/Production"),
            (RemoteProtocol.S3, "Backups (S3)", "s3.eu-central-1.amazonaws.com", "Work/Production"), (RemoteProtocol.Sftp, "Test server", "test.example.com", "Work/Test"),
            (RemoteProtocol.Ftp, "Downloads", "ftp.example.org", "Partners"), (RemoteProtocol.WebDavs, "Nextcloud", "cloud.example.com", ""),
            (RemoteProtocol.Smb, "NAS", "nas.local", ""), (RemoteProtocol.GoogleDrive, "Google Drive", "drive.google.com", ""),
        }.Select(c => new RemoteConnection { Name = c.Item2, Protocol = c.Item1, Host = c.Item3, User = "user", Folder = c.Item4, ClientId = "id" }));
        sample.Connections[0].Favorite = true;
        sample.ConnectionFolders.Add(new ConnectionFolder { Path = "Work/Production", Color = "#ED4245" });
        sample.ConnectionFolders.Add(new ConnectionFolder { Path = "Work/Test", Color = "#F0B232" });
        var manager = new NextCommander.App.Views.Connections.ConnectionManagerView(new NextCommander.App.Views.Connections.ConnectionManagerRequest(sample, () => { },
            (_, _) => Task.FromResult(string.Empty), _ => false, _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null)))
;
        var managerHost = new Window { Content = manager, Width = 1080, Height = 680, Title = "Connection manager" };
        _windows.Add(managerHost);
        managerHost.Show();
        manager.Toggle("Work/Production");
        manager.Reveal(sample.Connections[0]);
        await SettleAsync(8);
        Save(Compose(background, managerHost.CaptureRenderedFrame()!, managerHost.Title ?? string.Empty), "connections.png");
        managerHost.Close();

        // About (neutral folders)
        var info = new AboutInfo(UpdateChecker.DisplayVersion(typeof(MainWindowViewModel).Assembly), "© 2026 NextCommander", Environment.Version.ToString(),
            typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3) ?? "?", "Windows 11 (x64)", @"C:\Program Files\NextCommander",
            @"C:\Users\user\AppData\Roaming\NextCommander", AboutInfo.BuiltInComponents, () => Task.CompletedTask, _ => Task.CompletedTask);
        var about = new AboutDialog(info);
        _windows.Add(about);
        about.Show();
        await SettleAsync(8);
        Save(Compose(background, about.CaptureRenderedFrame()!, about.Title ?? string.Empty), "about.png");
        about.Close();

        // the flags of the languages
        var grid = new WrapPanel { Margin = new Thickness(0, 40, 0, 0), ItemWidth = 280, ItemHeight = 230, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, MaxWidth = 280 * 6 };
        foreach (var (code, name) in new[]
        {
            ("en", "English"), ("tr", "Türkçe"), ("de", "Deutsch"), ("es", "Español"), ("fr", "Français"), ("zh", "中文"),
            ("it", "Italiano"), ("nl", "Nederlands"), ("pt", "Português"), ("pl", "Polski"), ("uk", "Українська"), ("ja", "日本語"),
        })
        {
            var cell = new StackPanel { Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            cell.Children.Add(new Border
            {
                Child = Flags.Create(code, 120)!,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                BorderBrush = new SolidColorBrush(Color.Parse("#d0d0d0")),
                BorderThickness = new Thickness(1),
            });
            cell.Children.Add(new TextBlock { Text = name, FontSize = 20, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Classes = { "noloc" } });
            grid.Children.Add(cell);
        }
        var page = new StackPanel { Spacing = 20, Margin = new Thickness(80, 60) };
        page.Children.Add(new TextBlock { Text = "Languages", FontSize = 40, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } });
        page.Children.Add(new TextBlock { Text = "English, Turkish, German, Spanish, French and Chinese are included; more languages can be added as files.", FontSize = 20, Opacity = 0.75, Classes = { "noloc" } });
        page.Children.Add(grid);
        var flags = new Window { Width = Width, Height = Height, Content = page, Background = Brushes.White };
        _windows.Add(flags);
        flags.Show();
        await SettleAsync(8);
        Save(flags, "flags.png");
        flags.Close();
    }

    // ------------------------------------------------------------------ composing a tool window over the main window

    /// <summary>The main window, dimmed a little, with the tool window in the middle: a title bar, a frame and a shadow.</summary>
    private static Bitmap Compose(Bitmap background, Bitmap window, string title, bool dark = false)
    {
        const double bar = 34;
        var size = window.Size;
        var x = Math.Round((Width - size.Width) / 2);
        var y = Math.Round((Height - size.Height - bar) / 2);
        var result = new RenderTargetBitmap(new PixelSize(Width, Height));
        using (var ctx = result.CreateDrawingContext())
        {
            ctx.DrawImage(background, new Rect(0, 0, Width, Height));
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), new Rect(0, 0, Width, Height));
            for (var i = 1; i <= 14; i++)
            {
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(26 - i * 1.6), 0, 0, 0)), null,
                    new Rect(x - i, y - i + 6, size.Width + 2 * i, size.Height + bar + 2 * i), 8 + i, 8 + i);
            }
            var frame = new Rect(x, y, size.Width, size.Height + bar);
            ctx.DrawRectangle(dark ? new SolidColorBrush(Color.Parse("#202020")) : Brushes.White, new Pen(new SolidColorBrush(Color.Parse(dark ? "#3c3c3c" : "#8a8a8a")), 1), frame, 8, 8);
            ctx.FillRectangle(new SolidColorBrush(Color.Parse(dark ? "#2b2b2b" : "#f3f3f3")), new Rect(x + 1, y + 1, size.Width - 2, bar - 1));
            var text = new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 13, dark ? Brushes.White : Brushes.Black);
            ctx.DrawText(text, new Point(x + 14, y + (bar - text.Height) / 2));
            // the three window buttons on the right
            var glyph = new Pen(new SolidColorBrush(Color.Parse(dark ? "#cccccc" : "#444444")), 1);
            var right = x + size.Width - 24;
            ctx.DrawLine(glyph, new Point(right - 5, y + bar / 2 - 5), new Point(right + 5, y + bar / 2 + 5));
            ctx.DrawLine(glyph, new Point(right + 5, y + bar / 2 - 5), new Point(right - 5, y + bar / 2 + 5));
            ctx.DrawRectangle(null, glyph, new Rect(right - 51, y + bar / 2 - 5, 10, 10));
            ctx.DrawLine(glyph, new Point(right - 97, y + bar / 2), new Point(right - 87, y + bar / 2));
            ctx.DrawImage(window, new Rect(x, y + bar, size.Width, size.Height));
        }
        return result;
    }

    // ------------------------------------------------------------------ drive letter of its own

    private static string Subst(string folder)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        foreach (var letter in "NMKJQRSTUVWXYPOL")
        {
            if (used.Contains(letter))
            {
                continue;
            }
            var drive = letter + ":";
            if (Run("subst", $"{drive} \"{folder}\"") && Directory.Exists(drive + "\\"))
            {
                return drive;
            }
        }
        throw new InvalidOperationException("No free drive letter for subst");
    }

    private static void Unsubst(string drive)
    {
        if (drive.Length > 0)
        {
            Run("subst", $"{drive} /D");
        }
    }

    private static bool Run(string program, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0;
    }
}

/// <summary>Reads a local entry synchronously (test setup).</summary>
internal sealed class LocalEntryReader
{
    private readonly LocalFileSystemProvider _provider = new();

    public VfsEntry Read(string path) => _provider.GetEntryAsync(VfsPath.Local(path), CancellationToken.None).GetAwaiter().GetResult()!;
}

/// <summary>The real platform service with a fixed drive list (documentation screenshots).</summary>
public class PlatformProxy : DispatchProxy
{
    private IPlatformService _inner = null!;
    private IReadOnlyList<DriveItem> _drives = [];

    public static IPlatformService Create(IPlatformService inner, IReadOnlyList<DriveItem> drives)
    {
        var proxy = Create<IPlatformService, PlatformProxy>();
        var self = (PlatformProxy)(object)proxy;
        self._inner = inner;
        self._drives = drives;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IPlatformService.GetDrives) && args is null or { Length: 0 })
        {
            return _drives;
        }
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
