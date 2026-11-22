using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NextCommander.Core.Config;
using NextCommander.Core.Plugins;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The plugins' own settings (<see cref="IConfigurablePlugin"/>): a group per plugin on the Plugins page, drawn by the
/// program from what the plugin declares; the values live in <see cref="AppSettings.PluginSettings"/> under the plugin's
/// name. A value equal to the setting's default is not stored.
/// </summary>
public sealed partial class SettingsView
{
    /// <summary>The page the plugins' settings are on.</summary>
    public const string PluginsPage = "Operation/Plugins";

    /// <summary>The loaded plugins with settings: (plugin name, its settings); set by the main window.</summary>
    public static Func<IReadOnlyList<(string Plugin, IConfigurablePlugin Config)>>? PluginConfigurations { get; set; }

    /// <summary>Where the next configuration opens (a plugin's "Configure..."): the page, scrolled to the group; used once.</summary>
    public static (string Page, string Group)? StartAt { get; set; }

    private readonly string? _startGroup;

    private static IReadOnlyList<(string Plugin, IConfigurablePlugin Config)> Configurations()
    {
        try
        {
            return PluginConfigurations?.Invoke() ?? [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// The Plugins page, a group per plugin (the user's request, 22 November 2026): its switch (loaded or not) first, its
    /// own settings below it (greyed out while it is off). Installed plugins come from <see cref="PluginRoots"/>; turned
    /// off ones that are not installed any more stay until they are turned on.
    /// </summary>
    private static IEnumerable<SettingItem> PluginItems(AppSettings s)
    {
        var configurations = Configurations();
        IReadOnlyList<(string Name, PluginManifest? Manifest, string Folder)> found;
        try
        {
            found = PluginLoader.Discover(PluginRoots);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found = [];
        }
        var names = found.Select(p => p.Name).ToList();
        // plugins with settings that were not found in the folders (tests, a plugin loaded from elsewhere)
        names.AddRange(configurations.Select(c => c.Plugin).Where(p => !names.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase));
        // turned off before and not installed any more: they keep a switch until turned on
        names.AddRange(s.DisabledPlugins.Where(d => !names.Contains(d, StringComparer.OrdinalIgnoreCase)));
        if (names.Count == 0 && PluginRoots.Count > 0)
        {
            yield return NoPluginsItem();
        }
        foreach (var name in names)
        {
            (string Name, PluginManifest? Manifest, string Folder)? info = found.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ? found.First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
            var mine = configurations.Where(c => string.Equals(c.Plugin, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var group = mine.Count > 0 ? mine[0].Config.SettingsTitle : name;
            yield return PluginSwitchItem(name, group, info);
            foreach (var (plugin, config) in mine)
            {
                IReadOnlyList<PluginSetting> settings;
                try
                {
                    settings = config.Settings;
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
                {
                    continue; // a plugin that cannot tell its settings is left out
                }
                foreach (var setting in settings)
                {
                    // under the plugin's switch: greyed out while the plugin is off
                    yield return PluginItem(plugin, group, setting) with { Indent = true };
                }
            }
        }
    }

    /// <summary>A plugin's switch: loaded or not (in <see cref="AppSettings.DisabledPlugins"/>); its version and description below.</summary>
    private static SettingItem PluginSwitchItem(string name, string group, (string Name, PluginManifest? Manifest, string Folder)? info)
    {
        bool Get(AppSettings s) => !s.DisabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
        void Set(AppSettings s, bool on)
        {
            if (Get(s) == on)
            {
                return; // the list keeps its order
            }
            var others = s.DisabledPlugins.Where(d => !string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
            s.DisabledPlugins = on ? [.. others] : [.. others, name];
        }
        var details = info?.Folder is null ? "(not installed)"
            : string.Join("  ·  ", new[] { info.Value.Manifest?.Version, info.Value.Manifest?.Description }.Where(t => !string.IsNullOrWhiteSpace(t)));
        return new(PluginsPage, group, "Load this plugin (after a restart)", details.Length > 0 ? details : null, (s, changed) =>
        {
            var box = new CheckBox { Content = "Load this plugin (after a restart)", Tag = name, IsChecked = Get(s) };
            if (info?.Folder is { } folder)
            {
                ToolTip.SetTip(box, folder);
            }
            box.IsCheckedChanged += (_, _) =>
            {
                Set(s, box.IsChecked == true);
                changed();
            };
            return box;
        }, (from, to) => Set(to, Get(from)), IsCheckBox: true);
    }

    /// <summary>The page without plugins: where they go.</summary>
    private static SettingItem NoPluginsItem() =>
        new(PluginsPage, "Plugins", "Installed plugins", null, (_, _) => new TextBlock
        {
            Text = "No plugins are installed. A plugin is a folder with a plugin.json in the plugins folder of the settings folder or of the program.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.7,
        }, (_, _) => { }, FullWidth: true);

    /// <summary>The stored value (null: none, the default applies).</summary>
    internal static string? StoredValue(AppSettings s, string plugin, string key) =>
        s.PluginSettings.TryGetValue(plugin, out var values) && values.TryGetValue(key, out var value) ? value : null;

    /// <summary>Stores a value; an empty one or the default is removed (so the settings file holds only what was changed).</summary>
    internal static void StoreValue(AppSettings s, string plugin, PluginSetting setting, string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (value is null || value == setting.Default)
        {
            if (s.PluginSettings.TryGetValue(plugin, out var values))
            {
                values.Remove(setting.Key);
                if (values.Count == 0)
                {
                    s.PluginSettings.Remove(plugin);
                }
            }
            return;
        }
        if (!s.PluginSettings.TryGetValue(plugin, out var stored))
        {
            stored = [];
            s.PluginSettings[plugin] = stored;
        }
        stored[setting.Key] = value;
    }

    private static SettingItem PluginItem(string plugin, string title, PluginSetting setting)
    {
        string? Get(AppSettings s) => StoredValue(s, plugin, setting.Key);
        void Set(AppSettings s, string? v) => StoreValue(s, plugin, setting, v);
        string Current(AppSettings s) => Get(s) ?? setting.Default ?? string.Empty;
        var label = setting.Label;
        var description = setting.Description;
        return setting.Kind switch
        {
            PluginSettingKind.Toggle => Bool(PluginsPage, title, label, description,
                s => string.Equals(Current(s), "true", StringComparison.OrdinalIgnoreCase), (s, v) => Set(s, v ? "true" : "false")),
            PluginSettingKind.Number => Number(PluginsPage, title, label, description, setting.Min, setting.Max,
                s => double.TryParse(Current(s), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : setting.Min,
                (s, v) => Set(s, v.ToString(CultureInfo.InvariantCulture))),
            PluginSettingKind.Choice when setting.Choices.Count > 0 => Choice(PluginsPage, title, label, description,
                [.. setting.Choices.Select(c => (c.Text, c.Value))], s => setting.Choices.Any(c => c.Value == Current(s)) ? Current(s) : setting.Choices[0].Value, Set),
            PluginSettingKind.Program or PluginSettingKind.File or PluginSettingKind.Folder => PathItem(title, setting, Get, Set),
            PluginSettingKind.Secret => SecretItem(title, PluginSettingValues.SecretId(plugin, setting.Key), setting),
            _ => Text(PluginsPage, title, label, description, Get, Set, setting.Placeholder ?? setting.Default),
        };
    }

    /// <summary>A path with a button that picks it (a program or file through the file dialog, a folder through the folder dialog).</summary>
    private static SettingItem PathItem(string title, PluginSetting setting, Func<AppSettings, string?> get, Action<AppSettings, string?> set) =>
        new(PluginsPage, title, setting.Label, setting.Description, (s, changed) =>
        {
            var box = new TextBox { Text = get(s), PlaceholderText = setting.Placeholder ?? setting.Default, MinWidth = 360, MaxWidth = 520 };
            box.TextChanged += (_, _) =>
            {
                set(s, box.Text);
                changed();
            };
            var browse = new Button { Content = "Browse...", VerticalAlignment = VerticalAlignment.Center };
            browse.Click += async (_, _) =>
            {
                string? picked;
                if (setting.Kind == PluginSettingKind.Folder)
                {
                    var storage = TopLevel.GetTopLevel(browse)?.StorageProvider;
                    var folders = storage is null ? [] : await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = setting.Label, AllowMultiple = false });
                    picked = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
                }
                else
                {
                    picked = PickProgram is null ? null : await PickProgram(setting.Label);
                }
                if (!string.IsNullOrEmpty(picked))
                {
                    box.Text = picked;
                }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.AddRange([box, browse]);
            return row;
        }, (from, to) => set(to, get(from)));

    /// <summary>Whether the password store holds a secret (the field then says it is saved); set by the main window.</summary>
    public static Func<string, bool>? SecretSaved { get; set; }

    /// <summary>
    /// A secret: typed hidden; what is typed replaces the saved one, Forget removes it. Only the change is carried
    /// (<see cref="AppSettings.PluginSecretChanges"/>), the main window puts it into the password store.
    /// </summary>
    private static SettingItem SecretItem(string title, string id, PluginSetting setting) =>
        new(PluginsPage, title, setting.Label, setting.Description, (s, changed) =>
        {
            var saved = SecretSaved?.Invoke(id) == true;
            var box = new TextBox
            {
                PasswordChar = '•',
                MinWidth = 300,
                MaxWidth = 420,
                PlaceholderText = saved ? "saved (type to change)" : setting.Placeholder ?? "Not set",
                Text = s.PluginSecretChanges.TryGetValue(id, out var typed) ? typed : null,
            };
            var forget = new Button { Content = "Forget", VerticalAlignment = VerticalAlignment.Center, IsEnabled = saved };
            box.TextChanged += (_, _) =>
            {
                if (string.IsNullOrEmpty(box.Text))
                {
                    s.PluginSecretChanges.Remove(id);
                }
                else
                {
                    s.PluginSecretChanges[id] = box.Text;
                }
                changed();
            };
            forget.Click += (_, _) =>
            {
                box.Text = null;
                s.PluginSecretChanges[id] = string.Empty; // removed from the store with Apply / OK
                box.PlaceholderText = setting.Placeholder ?? "Not set";
                forget.IsEnabled = false;
                changed();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.AddRange([box, forget]);
            return row;
        }, (from, to) =>
        {
            if (from.PluginSecretChanges.TryGetValue(id, out var value))
            {
                to.PluginSecretChanges[id] = value;
            }
            else
            {
                to.PluginSecretChanges.Remove(id);
            }
        });

    /// <summary>What a plugin says against the values being edited (the first one), or null.</summary>
    private string? PluginProblem()
    {
        foreach (var (plugin, config) in Configurations())
        {
            try
            {
                if (config.Validate(_work.PluginValues(plugin, config.Settings)) is { Length: > 0 } problem)
                {
                    return $"Settings of {config.SettingsTitle}: {problem}";
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or ArgumentException)
            {
                return $"Settings of {config.SettingsTitle}: {ex.Message}";
            }
        }
        return null;
    }

    /// <summary>Brings a group of the page into view (a plugin's "Configure...").</summary>
    private void ScrollToGroup(string group)
    {
        if (_rows.Children.FirstOrDefault(c => Equals(c.Tag, group)) is { } section)
        {
            section.BringIntoView();
            // the first field of the group gets the focus
            section.GetVisualDescendants().OfType<Avalonia.Input.InputElement>().FirstOrDefault(e => e is TextBox or CheckBox or ComboBox or NumericUpDown)?.Focus();
        }
    }

    /// <summary>Gives every loaded plugin its values (at the start and after the configuration); a plugin's failure only goes to the log.</summary>
    /// <param name="readSecret">Reads a secret from the password store by its id (null: plugins get no secrets).</param>
    public static void ApplyPluginSettings(AppSettings settings, IEnumerable<LoadedPlugin> plugins, Action<string, Exception>? failed = null,
        Func<string, Task<string?>>? readSecret = null)
    {
        foreach (var plugin in plugins)
        {
            var name = plugin.Name;
            Func<string, Task<string?>>? secrets = readSecret is null ? null : key => readSecret(PluginSettingValues.SecretId(name, key));
            foreach (var config in plugin.Configurables)
            {
                try
                {
                    config.Apply(settings.PluginValues(name, config.Settings, secrets));
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or ArgumentException or UnauthorizedAccessException)
                {
                    failed?.Invoke(plugin.Name, ex);
                }
            }
        }
    }
}
