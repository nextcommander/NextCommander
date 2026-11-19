using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The theme, icon pack and language editors (no JSON typed by hand) and the icons of the Lister's context menu.</summary>
[Collection("Appearance")]
public sealed class AppearanceEditorsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        ColorThemes.UserDirectory = null;
        IconPacks.UserDirectory = null;
        UiLocalization.UserDirectory = null;
        _fx.Dispose();
    }

    [AvaloniaFact]
    public async Task The_lister_context_menu_has_icons()
    {
        var (_, vm) = await _fx.OpenAsync();
        var entry = (await new NextCommander.Vfs.Local.LocalFileSystemProvider().GetEntryAsync(VfsPath.Local(_fx.L("a.txt")), CancellationToken.None))!;
        var window = _fx.Track(new ListerWindow(new ListerRequest([entry], 0, vm.Tools)));
        window.Show();
        await WaitUntilAsync(() => window.View.Entry is not null && window.View.Loading is { IsCompleted: true });
        await SettleAsync(4);
        var menu = window.View.ContextMenu!;
        menu.Open(window.View);
        await SettleAsync(4);
        var items = menu.Items.OfType<MenuItem>().Where(m => m.Header is string).ToDictionary(m => (string)m.Header!);
        foreach (var header in new[] { "Text", "Hex", "Image", "Word wrap", "Find...", "Reload" })
        {
            Assert.True(items[header].Icon is Control, $"no icon: {header}");
        }
        menu.Close();
    }

    [AvaloniaFact]
    public void The_theme_editor_saves_a_theme_into_the_user_folder()
    {
        var dir = _fx.L("themes");
        ColorThemes.UserDirectory = dir;
        var dialog = _fx.Track(new ThemeEditorDialog(ColorThemes.Capture(Application.Current!, string.Empty)));
        dialog.Show();
        dialog.Save();
        Assert.Null(dialog.Result); // no name
        dialog.ThemeName = "Deniz";
        dialog.Picker("FileList.Text")!.Color = Color.Parse("#123456");
        Assert.Equal("#123456", dialog.EditedTheme.Colors["FileList.Text"]);

        // the accent in one click; a color left out follows the look again
        dialog.SetAccent("#30D158");
        Assert.Equal("#30D158", dialog.EditedTheme.Colors["Accent"]);
        dialog.EditedTheme.Colors.Remove("Menu.Text");
        Assert.True(dialog.ContrastChecks.Total > 0);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir2)
        {
            Directory.CreateDirectory(dir2);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog)!.SavePng(Path.Combine(dir2, "theme-editor.png"));
        }
        dialog.Save();
        Assert.NotNull(dialog.Result);
        var saved = ColorThemes.Find(dialog.Result!.Id)!;
        Assert.Equal("Deniz", saved.Name);
        Assert.Equal("#123456", saved.Colors["FileList.Text"]);
        Assert.StartsWith(dir, saved.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task The_theme_preview_follows_the_colors()
    {
        var dialog = _fx.Track(new ThemeEditorDialog(ColorThemes.Capture(Application.Current!, "Preview")));
        dialog.Show();
        await SettleAsync(4);
        Color Background(string text) =>
            ((ISolidColorBrush)Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<TextBlock>()
                .First(t => t.Text == text && t.Parent is Border).Parent!.GetValue(Border.BackgroundProperty)!).Color;

        // the accent circles and a picker: the preview shows the new colors
        dialog.SetAccent("#FF375F");
        await SettleAsync(4);
        Assert.Equal(Color.Parse("#FF375F"), Background("Copy"));
        dialog.Picker("Controls.DisabledBackground")!.Color = Color.Parse("#112233");
        await SettleAsync(4);
        Assert.Equal(Color.Parse("#112233"), Background("Queue"));

        // a color chosen in the picker's own drop-down (as the user does)
        var picker = dialog.Picker("Controls.HoverBackground")!;
        var drop = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(picker).OfType<DropDownButton>().First();
        drop.Flyout!.ShowAt(drop);
        await SettleAsync(4);
        var view = ((Avalonia.Controls.Primitives.PopupFlyoutBase)drop.Flyout).GetType().GetProperty("Content")?.GetValue(drop.Flyout) as Control;
        // the drop-down's spectrum (the user drags in it): bound to the picker's own color
        var spectrum = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view!).OfType<Avalonia.Controls.Primitives.ColorSpectrum>().FirstOrDefault()
            ?? throw new InvalidOperationException("no spectrum in " + string.Join(",", Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view!).Select(c => c.GetType().Name).Distinct()));
        spectrum.Color = Color.Parse("#445566");
        await SettleAsync(4);
        Assert.Equal("#445566", dialog.EditedTheme.Colors["Controls.HoverBackground"]);
        Assert.Equal(Color.Parse("#445566"), Background("Cancel"));
        drop.Flyout.Hide();

        // a color left to the look stays so (the pickers shown again do not set it)
        var before = dialog.EditedTheme.Colors.Count;
        dialog.EditedTheme.Colors.Remove("Menu.Text");
        dialog.SetAccent("#30D158");
        await SettleAsync(6);
        Assert.False(dialog.EditedTheme.Colors.ContainsKey("Menu.Text"));
        Assert.Equal(before - 1, dialog.EditedTheme.Colors.Count);

        // a new theme from the system colors keeps only what differs from the look: switching the look changes the window
        var fresh = _fx.Track(new ThemeEditorDialog(ColorThemes.Capture(Application.Current!, "Fresh")));
        fresh.Show();
        await SettleAsync(4);
        Assert.False(fresh.EditedTheme.Colors.ContainsKey("Window.Background"));
        Color FreshBackground() => ((ISolidColorBrush)Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(fresh).OfType<TextBlock>()
            .First(t => t.Text == "Copy 3 files").FindAncestorOfType<StackPanel>()!.Parent!.GetValue(Border.BackgroundProperty)!).Color;
        var lightBack = FreshBackground();
        fresh.SetLook(dark: !fresh.EditedTheme.IsDark);
        await SettleAsync(4);
        Assert.NotEqual(lightBack, FreshBackground());

        // the look: a light theme with a dark look shows the dark defaults where it sets nothing
        dialog.EditedTheme.Colors.Remove("Controls.DisabledBackground");
        dialog.SetLook(dark: true);
        await SettleAsync(4);
        var dark = Background("Queue");
        dialog.SetLook(dark: false);
        await SettleAsync(4);
        Assert.NotEqual(dark, Background("Queue"));
    }

    [AvaloniaFact]
    public async Task The_plugins_window_lists_turns_off_and_installs_plugins()
    {
        // two installed plugins (one loaded, with a file system), one turned off whose folder is gone
        var userRoot = _fx.L("plugins-user");
        var programRoot = _fx.L("plugins-program");
        void Plugin(string root, string folder, string name, string version)
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            File.WriteAllText(Path.Combine(root, folder, "plugin.json"), $$"""{ "Name": "{{name}}", "Assembly": "{{name}}.dll", "Version": "{{version}}", "Description": "{{name}} plugin" }""");
        }
        Plugin(programRoot, "rclone", "rclone", "1.0");
        Plugin(userRoot, "exif", "Image metadata", "0.9");
        var loaded = new NextCommander.Core.Plugins.LoadedPlugin { Folder = Path.Combine(programRoot, "rclone"), Manifest = new() { Name = "rclone", Version = "1.0" } };
        loaded.FileSystems.Add(new NextCommander.Vfs.Local.LocalFileSystemProvider());
        var settings = new AppSettings { DisabledPlugins = ["Image metadata", "Old one"] };
        var saved = 0;
        string? opened = null;
        var dialog = _fx.Track(new PluginsDialog(new PluginsRequest([loaded], [userRoot, programRoot], settings, () => saved++,
            _ => Task.FromResult<string?>(null), (_, folder) =>
            {
                opened = folder;
                return Task.CompletedTask;
            }, () => true)));
        dialog.Show();
        await SettleAsync(4);
        Assert.Equal(["Image metadata", "Old one", "rclone"], dialog.Rows.Select(r => r.Name));
        Assert.Equal(1, dialog.Rows.Single(r => r.Name == "rclone").FileSystems);
        Assert.False(dialog.NeedsRestart);
        Shot(dialog, "plugins");

        // the search; turning a loaded plugin off waits for a restart
        dialog.Search("metadata");
        await SettleAsync();
        Assert.Equal(["Image metadata"], dialog.Shown.Select(r => r.Name));
        dialog.Search(string.Empty);
        dialog.SetDisabled(dialog.Rows.Single(r => r.Name == "rclone"), disabled: true);
        Assert.Contains("rclone", settings.DisabledPlugins);
        Assert.True(saved > 0);
        Assert.True(dialog.NeedsRestart);

        // installing from a zip file: into the settings folder's plugins, under its name
        var source = _fx.L("new-plugin");
        Plugin(source, "inner", "Hasher", "2.1");
        var zip = _fx.L("hasher.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(source, "inner"), zip);
        Assert.Equal("Hasher", PluginsDialog.InstallFrom(zip, userRoot));
        Assert.True(File.Exists(Path.Combine(userRoot, "Hasher", "plugin.json")));
        var empty = _fx.L("empty.zip");
        CreateEmptyZip(empty);
        Assert.Null(PluginsDialog.InstallFrom(empty, userRoot)); // no plugin.json in it
        Assert.Null(opened);
    }

    private static bool CreateEmptyZip(string path)
    {
        using var stream = File.Create(path);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
        return true;
    }

    private static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(dir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public void The_icon_pack_editor_previews_and_saves_a_pack()
    {
        var dir = _fx.L("iconpacks");
        IconPacks.UserDirectory = dir;
        var shipped = IconPacks.Find(IconPack.DefaultId)!;
        var dialog = _fx.Track(new IconPackEditorDialog(shipped));
        dialog.Show();
        Assert.NotEmpty(dialog.PreviewIcons);
        dialog.PackName = "Benim ikonlarım";
        dialog.EditedPack.Style = IconPackStyle.Monochrome;
        dialog.Save();
        var saved = IconPacks.Find(dialog.Result!.Id)!;
        Assert.Equal("Benim ikonlarım", saved.Name);
        Assert.Equal(IconPackStyle.Monochrome, saved.Style);
        Assert.StartsWith(dir, saved.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(saved.FilePath, shipped.FilePath); // the shipped pack is untouched
    }

    [AvaloniaFact]
    public void The_language_editor_saves_a_new_language()
    {
        var dir = _fx.L("languages");
        UiLocalization.UserDirectory = dir;
        var dialog = _fx.Track(new LanguageEditorDialog(null));
        dialog.Show();
        Assert.True(dialog.Rows.Count > 1000);
        Assert.Equal(dialog.Rows.Count, dialog.Shown.Count);

        dialog.Code = "EN";
        dialog.Save();
        Assert.Null(dialog.Result);
        Assert.NotEmpty(dialog.ErrorText);

        dialog.Code = "it";
        dialog.LanguageName = "Italiano";
        var copy = dialog.Rows.First(r => r.English == "Copy");
        copy.Translation = "Copia";
        var holed = dialog.Rows.First(r => r.English.Contains("{0}", StringComparison.Ordinal));
        holed.Translation = "senza segnaposto";
        dialog.Save();
        Assert.Null(dialog.Result); // the {0} is lost
        holed.Translation = string.Empty;

        dialog.OnlyMissing = true;
        Assert.DoesNotContain(copy, dialog.Shown);
        dialog.Save();
        Assert.Equal("it", dialog.Result);
        var file = Localizer.ParseFile(File.ReadAllText(Path.Combine(dir, "it.json")));
        Assert.Equal("Italiano", file[Localizer.NameKey]);
        Assert.Equal("Copia", file[copy.Key]);
        Assert.Contains(("it", "Italiano"), UiLocalization.Available());
        Assert.Equal("Copia", UiLocalization.Read("it")!.Translate("Copy"));

        // an existing language is loaded into the boxes
        var turkish = _fx.Track(new LanguageEditorDialog("tr"));
        turkish.Show();
        Assert.Equal("tr", turkish.Code);
        Assert.False(string.IsNullOrEmpty(turkish.Rows.First(r => r.English == "Copy").Translation));
    }
}
