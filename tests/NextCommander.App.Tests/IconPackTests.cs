using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;
using Shape = Avalonia.Controls.Shapes.Path;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Icons > Icon pack: the glyphs and colors of the built-in icons (application-wide: Appearance collection).</summary>
[Collection("Appearance")]
public sealed class IconPackTests : IDisposable
{
    private readonly UiFixture _fx = new();
    private readonly string _userPacks = Path.Combine(Path.GetTempPath(), "fm-iconpacks-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        IconPacks.UserDirectory = null;
        App.ApplyIconSettings(new AppSettings());
        App.ApplyColorTheme(null);
        _fx.Dispose();
        try
        {
            Directory.Delete(_userPacks, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static Color FillOf(Control icon) => ((ISolidColorBrush)icon.GetLogicalDescendants().OfType<Shape>().First().Fill!).Color;

    [AvaloniaFact]
    public async Task Packs_change_menu_drive_and_file_icons()
    {
        // shipped packs, the default first
        var packs = IconPacks.Available();
        Assert.Equal(IconPack.DefaultId, packs[0].Id);
        Assert.Contains(packs, p => p.Id == "monochrome" && p.Style == IconPackStyle.Monochrome);
        Assert.Contains(packs, p => p.Id == "fluent" && p.Style == IconPackStyle.Outline);
        Assert.Equal(IconPack.DefaultId, new AppSettings().IconPack);

        File.WriteAllText(_fx.L("a.zip"), "z");
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.IconMode = IconMode.Standard; // the built-in type icons, not the operating system's
        vm.ApplySettings();
        var colored = FillOf(Services.MenuIcons.Create("nc_Copy")!);
        Assert.Null(vm.Left.Items.First(i => i.FullName == "a.zip").IconStroke);

        // Fluent: its palette, an outline, its own folder shape
        vm.Settings.IconPack = "fluent";
        vm.ApplySettings();
        Assert.Equal("fluent", IconPacks.Current.Id);
        var copy = Services.MenuIcons.Create("nc_Copy")!.GetLogicalDescendants().OfType<Shape>().First();
        Assert.Equal(Color.Parse("#0F6CBD"), ((ISolidColorBrush)copy.Stroke!).Color);
        Assert.True(((ISolidColorBrush)copy.Fill!).Opacity < 0.5);
        var zip = vm.Left.Items.First(i => i.FullName == "a.zip");
        Assert.Equal(Color.Parse("#8764B8"), ((ISolidColorBrush)zip.IconStroke!).Color);
        Assert.NotSame(App.Current!.FindResource("FmFolderIcon"), FileKinds.GeometryFor(FileKind.Folder));
        Assert.NotSame(App.Current!.FindResource("MiDrive"), DriveIconConverter.Glyph.Convert(NextCommander.Platform.DriveKind.Fixed, typeof(Geometry), null, System.Globalization.CultureInfo.InvariantCulture));

        // Monochrome: one color (the theme's icon color)
        vm.Settings.IconPack = "monochrome";
        vm.ApplySettings();
        var mono = ((ISolidColorBrush)IconPacks.MonochromeBrush()).Color;
        Assert.Equal(mono, ((ISolidColorBrush)FileKinds.BrushFor(FileKind.Archive)).Color);
        var icon = Services.MenuIcons.Create("nc_Delete")!;
        var host = _fx.Track(new Window { Content = icon });
        host.Show();
        await SettleAsync();
        Assert.Equal(mono, FillOf(icon));
        Assert.NotEqual(colored, mono);

        // a pack of the user (settings folder): own colors, no badges, a broken glyph keeps the built-in one
        Directory.CreateDirectory(_userPacks);
        File.WriteAllText(Path.Combine(_userPacks, "mine.json"), """
            // comments are allowed
            { "Name": "Benim", "Style": "Colored", "Badges": false,
              "Colors": { "Blue": "#112233" }, "Glyphs": { "MiCopy": "this is not path data" } }
            """);
        IconPacks.UserDirectory = _userPacks;
        vm.Settings.IconPack = "mine";
        vm.ApplySettings();
        Assert.Equal("Benim", IconPacks.Current.DisplayName);
        Assert.Equal(Color.Parse("#112233"), FillOf(Services.MenuIcons.Create("nc_Copy")!));
        Assert.Single(Services.MenuIcons.Create("nc_MkDir")!.GetLogicalDescendants().OfType<Shape>()); // no "+" badge
        Assert.Same(App.Current!.FindResource("MiCopy"), IconPacks.Glyph("MiCopy"));

        // a pack that no longer exists: the default
        vm.Settings.IconPack = "gone";
        vm.ApplySettings();
        Assert.Equal(IconPack.DefaultId, IconPacks.Current.Id);

        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var (id, theme) in new[] { ("colored", (string?)null), ("monochrome", null), ("fluent", null), ("fluent", "dark"), ("monochrome", "dark") })
            {
                vm.Settings.ColorThemeFile = theme ?? string.Empty;
                vm.Settings.IconPack = id;
                vm.ApplySettings();
                await SettleAsync(6);
                w.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"iconpack-{id}{(theme is null ? "" : "-" + theme)}.png"));
            }
            App.ApplyColorTheme(null);
            var settings = _fx.Track(new Views.Dialogs.SettingsWindow(vm.Settings, () => { }, []));
            settings.Show();
            settings.SelectPage("Display/Icons");
            await SettleAsync(4);
            settings.CaptureRenderedFrame()!.Save(Path.Combine(dir, "settings-icons.png"));
        }
    }
}
