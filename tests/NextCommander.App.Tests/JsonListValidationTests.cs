using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// Configuration: the lists of the settings (color rules, editors and associations by type, thumbnail fields, ignore
/// list, Start menu, plugins, view modes) are edited with controls; an entry with an error stops OK / Apply.
/// </summary>
[Collection("Appearance")]
public sealed class JsonListValidationTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public void Entries_are_checked_for_their_values()
    {
        // the checks behind the red marks (also used for settings.json written by hand)
        var (rules, error) = SettingsView.ParseJsonList<ColorRule>("""[{ "Mask": "*.txt", "LightColor": "#112233", "DarkColor": "#AABBCC" }]""");
        Assert.Null(error);
        Assert.Equal("*.txt", Assert.Single(rules!).Mask);
        Assert.Contains("unknown field \"LigthColor\"", SettingsView.ParseJsonList<ColorRule>("""[{ "Mask": "*.txt", "LigthColor": "#112233" }]""").Error);
        Assert.Contains("LightColor", SettingsView.ValidateEntry(new ColorRule { Mask = "*.txt", LightColor = "kırmızı" }));
        Assert.Contains("Mask is empty", SettingsView.ValidateEntry(new ColorRule { Mask = " " }));
        Assert.Contains("Command is empty", SettingsView.ValidateEntry(new ButtonDefinition { Label = "x" }));
        Assert.Null(SettingsView.ValidateEntry(ButtonDefinition.Separator()));
        Assert.Contains("Program is empty", SettingsView.ValidateEntry(new EditorAssociation { Mask = "*.md", Program = "" }));
        Assert.Contains("Command is empty", SettingsView.ValidateEntry(new FileAssociation { Mask = "*.md" }));
        Assert.All(ColorRule.Defaults(), r => Assert.Null(SettingsView.ValidateEntry(r)));
        Assert.All(new AppSettings().ButtonBar, b => Assert.Null(SettingsView.ValidateEntry(b)));
    }

    private static async Task<SettingsWindow> OpenAsync(UiFixture fx, AppSettings settings, string page)
    {
        var window = fx.Track(new SettingsWindow(settings, () => { }, []) { Width = 1100, Height = 760 });
        window.Show();
        window.SelectPage(page);
        await SettleAsync(4);
        return window;
    }

    private static TextBox Box(Control? editor) =>
        editor as TextBox ?? editor!.GetVisualDescendants().OfType<TextBox>().First();

    private static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public async Task Color_rules_are_edited_with_fields_and_a_wrong_color_stops_OK()
    {
        var (_, vm) = await _fx.OpenAsync();
        var before = vm.Settings.ColorRules.Count;
        var window = await OpenAsync(_fx, vm.Settings, "Display/Color");
        var editor = window.GetVisualDescendants().OfType<ListEditor<ColorRule>>().Single();
        Assert.Equal(before, editor.Items.Count);
        Assert.Equal(0, editor.SelectedIndex);
        Shot(window, "settings-color-rules");

        editor.Add(new ColorRule { Name = "Logs", Mask = "*.log", LightColor = "#C62828", DarkColor = "#EF9A9A" });
        await SettleAsync();
        Assert.Equal(before, editor.SelectedIndex);
        Box(editor.FieldEditor("Color (dark)")).Text = "#nope";
        await SettleAsync();
        Assert.Contains("DarkColor is not a valid color", editor.ErrorText);
        Assert.Contains(SettingsView.InvalidJsonClass, editor.Classes);
        Assert.False(window.CanApply());
        Assert.Equal(before, vm.Settings.ColorRules.Count); // nothing reaches the settings before Apply

        Box(editor.FieldEditor("Color (dark)")).Text = "#445566";
        Box(editor.FieldEditor("Changed within (minutes)")).Text = "90";
        await SettleAsync();
        Assert.True(window.CanApply());
        editor.Move(-1); // before the last of the defaults
        window.Apply();
        Assert.Equal(before + 1, vm.Settings.ColorRules.Count);
        var logs = vm.Settings.ColorRules[before - 1];
        Assert.Equal(("Logs", "*.log", "#445566", 90), (logs.Name, logs.Mask, logs.DarkColor, logs.MaxAgeMinutes!.Value));

        // later changes in the window are not in the live settings until Apply again
        Box(editor.FieldEditor("Name")).Text = "Log files";
        await SettleAsync();
        Assert.Equal("Logs", vm.Settings.ColorRules[before - 1].Name);
    }

    [AvaloniaFact]
    public async Task Editors_associations_thumbnail_fields_and_the_ignore_list_have_their_editors()
    {
        var (_, vm) = await _fx.OpenAsync();
        var settings = vm.Settings;
        settings.IgnoreList = ["Thumbs.db"];

        var window = await OpenAsync(_fx, settings, "Operation/Edit and view");
        var editors = window.GetVisualDescendants().OfType<ListEditor<EditorAssociation>>().Single();
        editors.Add(new EditorAssociation { Mask = "*.md", Program = "x" });
        await SettleAsync();
        editors.FieldEditor("Editor")!.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "System")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        Shot(window, "settings-editor-by-type");

        window.SelectPage("Operation/Associations");
        await SettleAsync(4);
        var associations = window.GetVisualDescendants().OfType<ListEditor<FileAssociation>>().Single();
        associations.Add(new FileAssociation { Mask = "*.log", Command = "lister", Label = "Log" });
        await SettleAsync();
        Shot(window, "settings-associations");

        window.SelectPage("Operation/Thumbnails");
        await SettleAsync(4);
        var thumbs = window.GetVisualDescendants().OfType<ListEditor<ThumbnailFieldRule>>().Single();
        thumbs.Add(new ThumbnailFieldRule { Mask = "*.mp4", Fields = "media.duration" });
        await SettleAsync();

        window.SelectPage("Ignore list");
        await SettleAsync(4);
        var ignore = window.GetVisualDescendants().OfType<ListEditor<SettingsView.TextEntry>>().Single();
        Assert.Equal("Thumbs.db", Assert.Single(ignore.Items).Value);
        ignore.Add(new SettingsView.TextEntry());
        await SettleAsync();
        Assert.False(window.CanApply()); // an empty line
        Box(ignore.FieldEditor("Name, mask or path")).Text = "*.tmp";
        await SettleAsync();
        Assert.True(window.CanApply());
        Shot(window, "settings-ignore-list");

        window.Apply();
        Assert.Equal("system", settings.EditorAssociations.Single(e => e.Mask == "*.md").Program);
        Assert.Equal("lister", settings.Associations.Single(a => a.Mask == "*.log").Command);
        Assert.Equal("media.duration", settings.ThumbnailFieldsByType.Single(r => r.Mask == "*.mp4").Fields);
        Assert.Equal(["Thumbs.db", "*.tmp"], settings.IgnoreList);
    }

    [AvaloniaFact]
    public async Task Start_menu_items_and_separators()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.StartMenu = [new ButtonDefinition { Label = "Notepad", Command = "notepad.exe" }];
        var window = await OpenAsync(_fx, vm.Settings, "Misc.");
        var menu = window.GetVisualDescendants().OfType<ListEditor<ButtonDefinition>>().Single();
        menu.Add(ButtonDefinition.Separator());
        await SettleAsync();
        Assert.Null(menu.FieldEditor("Command")); // a separator has no fields
        menu.Add(new ButtonDefinition { Label = "Copy", Command = string.Empty });
        await SettleAsync();
        Assert.False(window.CanApply());
        Box(menu.FieldEditor("Command")).Text = "nc_Copy";
        await SettleAsync();
        Assert.True(window.CanApply());
        Shot(window, "settings-start-menu");
        window.Apply();
        Assert.Equal(["notepad.exe", "-", "nc_Copy"], vm.Settings.StartMenu.Select(b => b.Command));
    }

    [AvaloniaFact]
    public async Task Plugins_are_listed_with_check_boxes()
    {
        var (_, vm) = await _fx.OpenAsync();
        var root = Path.Combine(_fx.Root, "plugins");
        foreach (var name in new[] { "Alpha", "Beta" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, "plugin.json"), $$"""{ "Name": "{{name}}", "Assembly": "{{name}}.dll", "Version": "1.0", "Description": "{{name}} plugin" }""");
        }
        var previous = SettingsView.PluginRoots;
        SettingsView.PluginRoots = [root];
        try
        {
            vm.Settings.DisabledPlugins = ["Beta", "Gone"];
            var window = await OpenAsync(_fx, vm.Settings, "Operation/Plugins");
            // a group per plugin, its switch (a check box drawn as a switch) first
            var boxes = window.GetVisualDescendants().OfType<CheckBox>().Where(b => b.Tag is string).ToDictionary(b => (string)b.Tag!);
            Assert.All(boxes.Values, b => Assert.Contains("switch", b.Classes));
            Assert.Equal(["Alpha", "Beta", "Gone"], boxes.Keys);
            Assert.True(boxes["Alpha"].IsChecked);
            Assert.False(boxes["Beta"].IsChecked);
            Shot(window, "settings-plugins");
            boxes["Alpha"].IsChecked = false;
            boxes["Beta"].IsChecked = true;
            window.Apply();
            Assert.Equal(["Gone", "Alpha"], vm.Settings.DisabledPlugins);
        }
        finally
        {
            SettingsView.PluginRoots = previous;
        }
    }

    [AvaloniaFact]
    public async Task View_modes_are_listed_with_an_editor_button()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.ColumnSets = [new NextCommander.Core.Columns.ColumnSet { Name = "Photos", AutoPathMask = "*Pictures*" }];
        var window = await OpenAsync(_fx, vm.Settings, "Display/View modes");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").StartsWith("Photos", StringComparison.Ordinal));
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Edit...");
        Shot(window, "settings-view-modes");
    }
}
