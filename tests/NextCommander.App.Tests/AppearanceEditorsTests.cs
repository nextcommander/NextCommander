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
