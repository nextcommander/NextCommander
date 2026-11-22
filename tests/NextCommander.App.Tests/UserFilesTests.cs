using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Views;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// Color theme and language files, the button bar editor, tab dragging, right button marking and the vertical bar.
/// Changes application resources: Appearance collection.
/// </summary>
[Collection("Appearance")]
public sealed class UserFilesTests : IDisposable
{
    private readonly UiFixture _fx = new();
    private AppSettings? _settings;

    public void Dispose()
    {
        ColorThemes.UserDirectory = null;
        UiLocalization.UserDirectory = null;
        if (_settings is not null)
        {
            _settings.ColorThemeFile = string.Empty;
            App.ApplyAppearance(_settings);
        }
        _fx.Dispose();
    }

    private static Color Brush(string key) => ((ISolidColorBrush)App.Current!.Resources[key]!).Color;

    [AvaloniaFact]
    public async Task Dark_theme_file_list_text_is_white()
    {
        var (_, vm) = await _fx.OpenAsync();
        _settings = vm.Settings;
        vm.Settings.ColorThemeFile = ColorTheme.DarkThemeId;
        App.ApplyAppearance(vm.Settings);
        Assert.Equal(ThemeVariant.Dark, App.Current!.RequestedThemeVariant);
        Assert.Equal(Colors.White, Brush("FmListTextBrush"));
        Assert.Equal(Color.Parse("#3C3C3C"), Brush("FmFunctionKeyBorderBrush")); // the line between the function keys
        Assert.True(App.Current.TryGetResource("SystemAccentColor", ThemeVariant.Dark, out var accent));
        Assert.Equal(Color.Parse("#007ACC"), (Color)accent!);
        // also the defaults below a dark theme (keys a theme leaves out)
        Assert.True(App.Current.TryGetResource("FmListTextBrush", ThemeVariant.Dark, out _));
    }

    [AvaloniaFact]
    public async Task Color_theme_files_are_listed_applied_and_exported()
    {
        var (_, vm) = await _fx.OpenAsync();
        _settings = vm.Settings;
        var dir = _fx.L("themes");
        Directory.CreateDirectory(dir);
        ColorThemes.UserDirectory = dir;
        await File.WriteAllTextAsync(Path.Combine(dir, "deneme.json"), """
            {
              // comments are allowed
              "Name": "Deneme",
              "Base": "Light",
              "Colors": { "Accent": "#123456", "FileList.Text": "#102030", "FileList.FolderIcon": "#AA5500", "FileList.HoverBackground": "#EEEEEE", "Bilinmeyen.Alan": "#FFFFFF" },
            }
            """);

        // shipped themes (program folder) and the user's own
        var themes = ColorThemes.Available();
        Assert.Contains(themes, t => t.Id == "deneme" && t.DisplayName == "Deneme");
        Assert.Contains(themes, t => t.Id == ColorTheme.DarkThemeId && t.IsDark);
        Assert.Contains(themes, t => t.Id == "macos" && t.DisplayName == "macOS" && !t.IsDark);
        Assert.Contains(themes, t => t.Id == "macos-dark" && t.IsDark);
        Assert.Contains(themes, t => t.Id == "ubuntu" && t.DisplayName == "Ubuntu" && !t.IsDark);
        Assert.Contains(themes, t => t.Id == "ubuntu-dark" && t.DisplayName == "Ubuntu Dark" && t.IsDark);
        foreach (var shipped in Directory.GetFiles(ColorThemes.BuiltInDirectory, "*.json"))
        {
            var theme = ColorTheme.Load(shipped);
            Assert.NotNull(theme);
            Assert.All(theme.Colors.Keys, k => Assert.Contains(ColorThemes.Keys, known => known.Key == k)); // known keys only
            Assert.All(ColorThemes.Keys, known => Assert.True(theme.Colors.ContainsKey(known.Key), $"{theme.Id}: {known.Key}")); // complete
        }

        vm.Settings.ColorThemeFile = "deneme";
        App.ApplyAppearance(vm.Settings);
        Assert.Equal(ThemeVariant.Light, App.Current!.RequestedThemeVariant);
        Assert.Equal(Color.Parse("#102030"), Brush("FmListTextBrush"));
        Assert.Equal(Color.Parse("#AA5500"), Brush("FmFolderBrush"));
        Assert.Equal(Color.Parse("#EEEEEE"), (Color)App.Current.Resources["DataGridRowHoveredBackgroundColor"]!);
        Assert.True(App.Current.TryGetResource("SystemAccentColor", ThemeVariant.Light, out var accent));
        Assert.Equal(Color.Parse("#123456"), (Color)accent!);

        // the colors in effect as a new theme file, every key with a comment telling what it colors
        var exported = ColorThemes.Export(App.Current, "Benim tema");
        Assert.Equal(Path.Combine(dir, "Benim-tema.json"), exported.FilePath);
        var text = await File.ReadAllTextAsync(exported.FilePath!);
        Assert.Contains("\"FileList.Text\": \"#102030\",   // Text of the file list", text, StringComparison.Ordinal);
        var again = ColorTheme.Load(exported.FilePath!)!;
        Assert.Equal("Light", again.Base);
        Assert.Equal("#102030", again.Colors["FileList.Text"]);
        Assert.Equal("#123456", again.Colors["Accent"]);

        // back to no theme: the theme's colors are gone
        vm.Settings.ColorThemeFile = string.Empty;
        App.ApplyAppearance(vm.Settings);
        Assert.False(App.Current.Resources.ContainsKey("FmFolderBrush"));
        Assert.Null(ColorThemes.Current);
    }

    [AvaloniaFact]
    public void The_shipped_plugins_bring_their_own_translations()
    {
        UiLocalization.UserDirectory = null;
        var plugins = Path.Combine(AppContext.BaseDirectory, "plugins");

        // each plugin's languages folder: every shipped language has every key of its en.json and keeps the {0} holes
        foreach (var plugin in new[] { "git", "rclone", "sqlite", "docker" })
        {
            var languages = Path.Combine(plugins, plugin, "languages");
            var en = Localizer.ParseFile(File.ReadAllText(Path.Combine(languages, "en.json")));
            Assert.NotEmpty(en);
            foreach (var code in new[] { "tr", "de", "es", "fr", "zh" })
            {
                var file = Localizer.ParseFile(File.ReadAllText(Path.Combine(languages, code + ".json")));
                Assert.Empty(file.Keys.Except(en.Keys));
                Assert.Empty(en.Keys.Except(file.Keys));
                foreach (var (key, english) in en)
                {
                    foreach (System.Text.RegularExpressions.Match hole in System.Text.RegularExpressions.Regex.Matches(english, @"\{\d+(:[^}]*)?\}"))
                    {
                        Assert.True(file[key].Contains(hole.Value, StringComparison.Ordinal), $"{plugin}/{code}.json {key}: {hole.Value} is missing");
                    }
                }
            }
        }

        // not in the program's files any more: a language read without the plugin leaves its texts in English
        Assert.Equal("Git history", UiLocalization.Read("tr")!.Translate("Git history"));

        // joined when the language is read with the loaded plugins; the program's own translation of a text wins
        UiLocalization.PluginDirectories.AddRange([Path.Combine(plugins, "git"), Path.Combine(plugins, "rclone")]);
        try
        {
            var tr = UiLocalization.Read("tr")!;
            Assert.Equal("Git geçmişi", tr.Translate("Git history"));
            Assert.Equal("İçinde değişiklik var", tr.Translate("Changes inside"));
            Assert.Equal("Değişti (hazırlandı)", tr.Translate("Changed (staged)"));
            Assert.Equal("rclone uzak bağlantıları", tr.Translate("rclone remotes"));
            Assert.Equal("Kopyala", tr.Translate("Copy")); // the program's texts as before
        }
        finally
        {
            UiLocalization.PluginDirectories.Clear();
        }
    }

    [AvaloniaFact]
    public void Language_files_from_the_program_and_settings_folders()
    {
        // shipped: en.json lists every text of the code (template for new languages), tr.json translates every key
        var tr = Localizer.ParseFile(File.ReadAllText(Path.Combine(UiLocalization.BuiltInDirectory, "tr.json")));
        var en = Localizer.ParseFile(File.ReadAllText(Path.Combine(UiLocalization.BuiltInDirectory, "en.json")));
        Assert.Empty(tr.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(tr.Keys));
        Assert.Equal("English", en[Localizer.NameKey]);
        Assert.Equal("Türkçe", tr[Localizer.NameKey]);

        // the other shipped languages translate every key too, with their own name
        var shipped = new Dictionary<string, string> { ["de"] = "Deutsch", ["es"] = "Español", ["fr"] = "Français", ["zh"] = "中文（简体）" };
        foreach (var (code, name) in shipped)
        {
            var file = Localizer.ParseFile(File.ReadAllText(Path.Combine(UiLocalization.BuiltInDirectory, code + ".json")));
            Assert.Empty(file.Keys.Except(en.Keys));
            Assert.Empty(en.Keys.Except(file.Keys));
            Assert.Equal(name, file[Localizer.NameKey]);
            // the {0} patterns of a text are all in its translation (a lost one would drop a file name or a number)
            foreach (var (key, english) in en)
            {
                foreach (System.Text.RegularExpressions.Match hole in System.Text.RegularExpressions.Regex.Matches(english, @"\{\d+(:[^}]*)?\}"))
                {
                    Assert.True(file[key].Contains(hole.Value, StringComparison.Ordinal), $"{code}.json {key}: {hole.Value} is missing");
                }
            }
        }
        Assert.Equal(["de", "en", "es", "fr", "tr", "zh"], UiLocalization.Available().Select(l => l.Code).Order(StringComparer.Ordinal));

        var dir = _fx.L("languages");
        Directory.CreateDirectory(dir);
        UiLocalization.UserDirectory = dir;
        File.WriteAllText(Path.Combine(dir, "de.json"), """{ "_name": "Deutsch", "Copy": "Kopieren", "{0} files": "{0} Dateien" }""");
        var languages = UiLocalization.Available();
        Assert.Equal("en", languages[0].Code);
        Assert.Equal("English", languages[0].Name);
        Assert.Contains(("de", "Deutsch"), languages);
        Assert.Contains(("tr", "Türkçe"), languages);
        var german = UiLocalization.Read("de")!;
        Assert.Equal("Kopieren", german.Translate("Copy")); // the English text as key
        Assert.Equal("5 Dateien", german.Translate("5 files"));

        // keys "Form.Item.Option": en.json gives the English text of a key (the text in the code)
        Assert.All(en.Keys.Where(k => k != Localizer.NameKey), k => Assert.Matches(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", k));
        Assert.Equal("Gizli dosyaları göster", tr["Settings.Display.ShowHiddenFiles"]);
        Assert.Equal("Show hidden files", en["Settings.Display.ShowHiddenFiles"]);
        Assert.Equal("Gizli dosyaları göster", UiLocalization.Read("tr")!.Translate("Show hidden files"));
        File.WriteAllText(Path.Combine(dir, "de.json"), """{ "_name": "Deutsch", "Settings.Display.ShowHiddenFiles": "Versteckte Dateien anzeigen" }""");
        Assert.Equal("Versteckte Dateien anzeigen", UiLocalization.Read("de")!.Translate("Show hidden files"));
        Assert.Equal("de", UiLocalization.ResolveLanguage("system", new System.Globalization.CultureInfo("de-DE")));
        Assert.Equal("tr", UiLocalization.ResolveLanguage("system", new System.Globalization.CultureInfo("tr-TR")));
        Assert.Equal("fr", UiLocalization.ResolveLanguage("system", new System.Globalization.CultureInfo("fr-FR")));
        Assert.Equal("zh", UiLocalization.ResolveLanguage("system", new System.Globalization.CultureInfo("zh-CN")));
        Assert.Equal("en", UiLocalization.ResolveLanguage("system", new System.Globalization.CultureInfo("ja-JP"))); // no file: English

        // a file in the settings folder wins over the shipped one
        File.WriteAllText(Path.Combine(dir, "tr.json"), """{ "_name": "Türkçe (benim)", "Settings.Display.ShowHiddenFiles": "Gizliler de" }""");
        Assert.Equal("Gizliler de", UiLocalization.Read("tr")!.Translate("Show hidden files"));
    }

    [AvaloniaFact]
    public async Task Button_bar_editor()
    {
        var (_, vm) = await _fx.OpenAsync();
        var dialog = _fx.Track(new ButtonBarEditorDialog("Change button bar", vm.Settings.ButtonBar, 16, false,
            (b, size) => Controls.ButtonBarControl.CreateIcon(b, size, vm), _ => Task.FromResult<string?>(null)));
        dialog.Show();
        await SettleAsync();
        var count = dialog.Buttons.Count;
        dialog.Select(0);
        dialog.Add();
        dialog.SetFields("notepad.exe", "Not", "Notepad");
        dialog.SetWindowState(ProgramWindowState.Maximized);
        dialog.AddSeparator();
        dialog.SelectSize(32);
        Assert.Equal(count + 2, dialog.Buttons.Count);
        Assert.Equal("notepad.exe", dialog.Buttons[1].Command);
        Assert.Equal("Notepad", dialog.Buttons[1].Tooltip);
        Assert.True(dialog.Buttons[2].IsSeparator);
        dialog.Select(1);
        dialog.Move(-1);
        Assert.Equal("notepad.exe", dialog.Buttons[0].Command);
        Assert.Equal(count, vm.Settings.ButtonBar.Count); // the live bar is untouched
        dialog.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(32, dialog.Result!.IconSize);
        Assert.Equal(ProgramWindowState.Maximized, dialog.Result.Buttons.First(b => b.Command == "notepad.exe").WindowState);
        Assert.Equal(count + 2, dialog.Result.Buttons.Count);

        // Configuration > Button bar takes the result
        _fx.Dialogs.NextButtonBar = dialog.Result;
        await vm.ExecuteAsync("nc_ButtonConfig");
        Assert.Equal(32, vm.Settings.ButtonBarIconSize);
        Assert.Equal("notepad.exe", vm.Settings.ButtonBar[0].Command);
        _fx.Dialogs.NextButtonBar = null;
        await vm.ExecuteAsync("nc_VertButtonConfig");
        Assert.Same(vm.Settings.VerticalButtonBar, _fx.Dialogs.LastButtonBar);
    }

    [AvaloniaFact]
    public async Task Vertical_bar_is_not_resized_by_the_splitter_and_starts_at_the_file_list()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.ShowVerticalButtonBar = true;
        vm.ApplySettings();
        await SettleAsync();
        var grid = w.FindControl<Grid>("PanelsGrid")!;
        Assert.Equal(3, grid.ColumnDefinitions.Count);
        Assert.All(new[] { 0, 2 }, i => Assert.True(grid.ColumnDefinitions[i].Width.IsStar));
        var host = w.FindControl<Border>("VerticalBarHost")!;
        var left = w.FindControl<PanelSideView>("LeftSideView")!;
        var right = w.FindControl<PanelSideView>("RightSideView")!;
        Assert.True(host.Bounds.Width > 0);
        // 50 % / 50 %: both sides equally wide, the bar between them (not taken from the right side)
        Assert.Equal(50, vm.LeftPanelPercent);
        Assert.Equal(left.Bounds.Width, right.Bounds.Width, 1.0);
        var hostLeft = host.TranslatePoint(default, w)!.Value.X;
        Assert.True(hostLeft >= left.Bounds.Right && hostLeft + host.Bounds.Width <= right.TranslatePoint(default, w)!.Value.X + 0.5);
        var first = host.GetVisualDescendants().OfType<Button>().First();
        Assert.True(first.TranslatePoint(default, host)!.Value.Y < 10);
        // the first button is level with the start of the right file list (below drive bar, tabs and path)
        var listTop = w.RightPanel.ListArea.TranslatePoint(default, w)!.Value.Y;
        Assert.True(listTop > 20);
        Assert.Equal(listTop, first.TranslatePoint(default, w)!.Value.Y, 3.0);
    }

    [AvaloniaFact]
    public async Task Current_tab_has_a_close_button()
    {
        var (w, vm) = await _fx.OpenAsync();
        var side = vm.LeftSide;
        var view = w.GetVisualDescendants().OfType<PanelSideView>().First();
        List<Button> Closes() => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tabclose")).ToList();
        await SettleAsync();
        Assert.DoesNotContain(Closes(), b => b.IsEffectivelyVisible); // a single tab cannot be closed

        await side.OpenTabAsync();
        await side.OpenTabAsync();
        await SettleAsync();
        var visible = Assert.Single(Closes(), b => b.IsEffectivelyVisible); // only on the current tab
        Assert.Same(side.ActiveTab, visible.Tag);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "tab-close.png"));
        }

        var closing = side.ActiveTab;
        visible.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        Assert.Equal(2, side.Tabs.Count);
        Assert.DoesNotContain(closing, side.Tabs);
        Assert.Same(side.ActiveTab, Assert.Single(Closes(), b => b.IsEffectivelyVisible).Tag); // the new current tab has it
    }

    [AvaloniaFact]
    public async Task Tabs_can_be_dragged()
    {
        var (w, vm) = await _fx.OpenAsync();
        var side = vm.LeftSide;
        await side.OpenTabAsync();
        await side.OpenTabAsync();
        var first = side.Tabs[0];
        await SettleAsync();
        var view = w.GetVisualDescendants().OfType<PanelSideView>().First();
        Button TabButton(PanelViewModelRef t) => view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tab") && ReferenceEquals(b.Tag, t.Tab));
        Point Center(Button b) => b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), w)!.Value;
        var from = Center(TabButton(new(first)));
        var to = Center(TabButton(new(side.Tabs[2])));
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point(from.X + 20, from.Y), RawInputModifiers.LeftMouseButton);
        w.MouseMove(to, RawInputModifiers.LeftMouseButton);
        w.MouseUp(to, MouseButton.Left);
        await SettleAsync();
        Assert.Equal(2, side.Tabs.IndexOf(first));
        Assert.Same(first, side.ActiveTab);
    }

    private sealed record PanelViewModelRef(ViewModels.PanelViewModel Tab);

    [AvaloniaFact]
    public async Task Right_click_marks_and_a_long_right_press_opens_the_menu()
    {
        var (w, vm) = await _fx.OpenAsync();
        await SettleAsync();
        Assert.True(vm.Settings.RightButtonSelects);
        var grid = w.LeftPanel.GetVisualDescendants().OfType<DataGrid>().First();
        Point RowPoint(string name)
        {
            var row = grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => (r.DataContext as ViewModels.FileItemViewModel)?.FullName == name);
            return row.TranslatePoint(new Point(60, row.Bounds.Height / 2), w)!.Value;
        }
        var menu = grid.ContextMenu!;

        // a short right click marks, no menu
        w.MouseDown(RowPoint("a.txt"), MouseButton.Right);
        w.MouseUp(RowPoint("a.txt"), MouseButton.Right);
        await SettleAsync();
        Assert.True(vm.Left.Items.Single(i => i.FullName == "a.txt").IsMarked);
        Assert.False(menu.IsOpen);

        // held: the menu opens (and stays open after the release), the file is not marked
        w.MouseDown(RowPoint("b.log"), MouseButton.Right);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!menu.IsOpen && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Assert.True(menu.IsOpen);
        w.MouseUp(RowPoint("b.log"), MouseButton.Right);
        await SettleAsync();
        Assert.True(menu.IsOpen);
        Assert.False(vm.Left.Items.Single(i => i.FullName == "b.log").IsMarked);
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Mac_secondary_click_opens_the_menu_and_cmd_click_marks()
    {
        var (w, vm) = await _fx.OpenAsync();
        w.LeftPanel.MacClicksOverride = true;
        await SettleAsync();
        Assert.True(vm.Settings.RightButtonSelects); // the Windows setting does not apply on a Mac
        var grid = w.LeftPanel.GetVisualDescendants().OfType<DataGrid>().First();
        Point RowPoint(string name)
        {
            var row = grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => (r.DataContext as ViewModels.FileItemViewModel)?.FullName == name);
            return row.TranslatePoint(new Point(60, row.Bounds.Height / 2), w)!.Value;
        }
        var menu = grid.ContextMenu!;

        // right button (or a two-finger click): the menu at once, on the clicked file, nothing marked
        w.MouseDown(RowPoint("b.log"), MouseButton.Right);
        await SettleAsync();
        Assert.True(menu.IsOpen);
        w.MouseUp(RowPoint("b.log"), MouseButton.Right);
        await SettleAsync();
        Assert.True(menu.IsOpen);
        Assert.Equal("b.log", vm.Left.CursorItem?.FullName);
        Assert.DoesNotContain(vm.Left.Items, i => i.IsMarked);
        menu.Close();
        await SettleAsync();

        // Ctrl+click is the secondary click too
        w.MouseDown(RowPoint("a.txt"), MouseButton.Left, RawInputModifiers.Control);
        w.MouseUp(RowPoint("a.txt"), MouseButton.Left, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(menu.IsOpen);
        Assert.Equal("a.txt", vm.Left.CursorItem?.FullName);
        menu.Close();
        await SettleAsync();

        // Cmd+click marks (Finder's adding to the selection)
        w.MouseDown(RowPoint("a.txt"), MouseButton.Left, RawInputModifiers.Meta);
        w.MouseUp(RowPoint("a.txt"), MouseButton.Left, RawInputModifiers.Meta);
        await SettleAsync();
        Assert.True(vm.Left.Items.Single(i => i.FullName == "a.txt").IsMarked);
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void Right_button_marks_by_default_also_in_old_settings_files()
    {
        Assert.True(new AppSettings().RightButtonSelects);
        var dir = _fx.L("oldsettings");
        Directory.CreateDirectory(dir);
        // a file from before the settings version: the old default (false) is replaced
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "RightButtonSelects": false }""");
        var store = new SettingsStore(dir);
        var loaded = store.Load();
        Assert.True(loaded.RightButtonSelects);
        Assert.Equal(AppSettings.CurrentSettingsVersion, loaded.SettingsVersion);
        // a current file keeps the user's choice
        loaded.RightButtonSelects = false;
        store.Save(loaded);
        Assert.False(store.Load().RightButtonSelects);
        Assert.Contains("SettingsVersion", File.ReadAllText(store.FilePath), StringComparison.Ordinal);

        // dark mode of an older file became the dark theme file
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "SettingsVersion": 2, "Theme": "Dark" }""");
        Assert.Equal(ColorTheme.DarkThemeId, store.Load().ColorThemeFile);
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "SettingsVersion": 2, "Theme": "System" }""");
        Assert.Equal(string.Empty, store.Load().ColorThemeFile);
    }
}
