using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
        dialog.Save();
        Assert.NotNull(dialog.Result);
        var saved = ColorThemes.Find(dialog.Result!.Id)!;
        Assert.Equal("Deniz", saved.Name);
        Assert.Equal("#123456", saved.Colors["FileList.Text"]);
        Assert.StartsWith(dir, saved.FilePath, StringComparison.OrdinalIgnoreCase);
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
