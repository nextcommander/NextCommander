using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using NextCommander.Core.Plugins;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>A plugin's own settings: drawn by the program on the Plugins page, checked by the plugin, stored under its name, given to it.</summary>
[Collection("Appearance")]
public sealed class PluginSettingsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        SettingsView.PluginConfigurations = null;
        SettingsView.StartAt = null;
        _fx.Dispose();
    }

    private sealed class SamplePlugin : IConfigurablePlugin
    {
        public string SettingsTitle => "Sample plugin";

        public IReadOnlyList<PluginSetting> Settings { get; } =
        [
            new("program", PluginSettingKind.Program, "Sample program") { Description = "Empty: the one on the PATH." },
            new("rows", PluginSettingKind.Number, "Sample rows") { Default = "10", Min = 1, Max = 50 },
            new("fast", PluginSettingKind.Toggle, "Sample fast mode") { Default = "false" },
            new("mode", PluginSettingKind.Choice, "Sample mode") { Default = "a", Choices = [("a", "Alpha"), ("b", "Beta")] },
        ];

        public IPluginSettingValues? Applied { get; private set; }

        public string? Validate(IPluginSettingValues values) =>
            values.Get("program") is { } program && !File.Exists(program) ? "The program was not found: " + program : null;

        public void Apply(IPluginSettingValues values) => Applied = values;
    }

    [AvaloniaFact]
    public async Task The_settings_of_a_plugin_are_edited_on_the_plugins_page_and_given_to_it()
    {
        var sample = new SamplePlugin();
        var plugin = new LoadedPlugin { Folder = Path.Combine(Path.GetTempPath(), "sample") };
        plugin.Configurables.Add(sample);
        var settings = new AppSettings();
        SettingsView.PluginConfigurations = () => [(plugin.Name, sample)];
        // "Configure..." of the plugins window: the Plugins page at the plugin's group
        SettingsView.StartAt = (SettingsView.PluginsPage, sample.SettingsTitle);
        var window = _fx.Track(new SettingsWindow(settings, () => SettingsView.ApplyPluginSettings(settings, [plugin]), []));
        window.Show();
        await SettleAsync(4);
        Assert.Equal("Plugins", window.PageTitle);
        Assert.Null(SettingsView.StartAt); // used once
        Assert.Contains("Installed plugins", window.VisibleLabels);
        foreach (var label in new[] { "Sample program", "Sample rows", "Sample fast mode", "Sample mode" })
        {
            Assert.Contains(label, window.VisibleLabels);
        }

        // the program path has a Browse button; a path that is not there is refused by the plugin
        var browse = window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Browse..."));
        var path = ((StackPanel)browse.Parent!).Children.OfType<TextBox>().Single();
        path.Text = Path.Combine(Path.GetTempPath(), "no-such-program.exe");
        window.GetLogicalDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Sample fast mode")).IsChecked = true;
        await SettleAsync();
        Assert.True(window.IsDirty);
        Assert.False(window.CanApply());
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Settings of Sample plugin: The program was not found", StringComparison.Ordinal) == true);

        // a real file: applied, stored under the plugin's name (the defaults are not stored), given to the plugin
        var program = typeof(PluginSettingsTests).Assembly.Location;
        path.Text = program;
        await SettleAsync();
        Assert.True(window.CanApply());
        window.Apply();
        Assert.Equal(new Dictionary<string, string> { ["program"] = program, ["fast"] = "true" }, settings.PluginSettings[plugin.Name]);
        Assert.Equal(program, sample.Applied!.Get("program"));
        Assert.True(sample.Applied.GetBool("fast"));
        Assert.Equal(10, sample.Applied.GetNumber("rows"));
        Assert.Equal("a", sample.Applied.Get("mode"));

        // back to the defaults: nothing of the plugin is left in the settings file
        window.GetLogicalDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Sample fast mode")).IsChecked = false;
        path.Text = string.Empty;
        await SettleAsync();
        window.Apply();
        Assert.False(settings.PluginSettings.ContainsKey(plugin.Name));
        Assert.Null(sample.Applied.Get("program"));

        // the plugins window: "Configure..." on a plugin with settings (not on one without), which opens them
        var roots = _fx.L("plugins");
        foreach (var name in new[] { "sample", "plain" })
        {
            Directory.CreateDirectory(Path.Combine(roots, name));
            await File.WriteAllTextAsync(Path.Combine(roots, name, "plugin.json"), $$"""{ "Name": "{{name}}", "Assembly": "{{name}}.dll" }""");
        }
        var shown = new LoadedPlugin { Folder = Path.Combine(roots, "sample"), Manifest = new() { Name = "sample" } };
        shown.Configurables.Add(sample);
        var plain = new LoadedPlugin { Folder = Path.Combine(roots, "plain"), Manifest = new() { Name = "plain" } };
        plain.FileSystems.Add(new NextCommander.Vfs.Local.LocalFileSystemProvider());
        LoadedPlugin? configured = null;
        var dialog = _fx.Track(new PluginsDialog(new PluginsRequest([shown, plain], [roots], settings, () => { }, _ => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask, () => true, p =>
            {
                configured = p;
                return Task.CompletedTask;
            })));
        dialog.Show();
        await SettleAsync(3);
        var configure = dialog.GetLogicalDescendants().OfType<Button>().Where(b => Equals(ToolTip.GetTip(b), "Configure...")).ToList();
        Assert.Single(configure);
        configure[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(2);
        Assert.Same(shown, configured);

        // the search finds a plugin's settings by its title
        window.Search("Sample plugin");
        await SettleAsync(2);
        Assert.Contains("Sample rows", window.VisibleLabels);
    }
}
