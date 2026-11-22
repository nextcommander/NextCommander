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

    /// <summary>The settings of every loaded plugin that has some.</summary>
    private static IEnumerable<SettingItem> PluginItems()
    {
        foreach (var (plugin, config) in Configurations())
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
                yield return PluginItem(plugin, config.SettingsTitle, setting);
            }
        }
    }

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
