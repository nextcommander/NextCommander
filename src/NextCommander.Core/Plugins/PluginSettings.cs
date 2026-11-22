using System.Globalization;

namespace NextCommander.Core.Plugins;

/// <summary>The kinds of settings a plugin can have; the program draws each with its own control.</summary>
public enum PluginSettingKind
{
    /// <summary>A line of text.</summary>
    Text,

    /// <summary>A number between <see cref="PluginSetting.Min"/> and <see cref="PluginSetting.Max"/>.</summary>
    Number,

    /// <summary>A check box ("true" / "false").</summary>
    Toggle,

    /// <summary>One of <see cref="PluginSetting.Choices"/>.</summary>
    Choice,

    /// <summary>A program's path, with a button to pick it.</summary>
    Program,

    /// <summary>A file's path, with a button to pick it.</summary>
    File,

    /// <summary>A folder's path.</summary>
    Folder,

    /// <summary>
    /// A password, token or key: typed hidden, kept in the program's password store (protected by the master password
    /// when there is one), never in the settings file; the plugin asks for it with <see cref="IPluginSettingValues.GetSecretAsync"/>.
    /// </summary>
    Secret,
}

/// <summary>
/// One setting of a plugin: its key (stored under the plugin's name in the settings), how it is edited, and the texts
/// the user sees (written in English; their translations go into the plugin's languages folder).
/// </summary>
public sealed record PluginSetting(string Key, PluginSettingKind Kind, string Label)
{
    /// <summary>The help text under the label.</summary>
    public string? Description { get; init; }

    /// <summary>The value when the user has set none (as text: "10", "true", a choice's value).</summary>
    public string? Default { get; init; }

    /// <summary>The grey text of an empty field (e.g. where the program is looked for when the path is empty).</summary>
    public string? Placeholder { get; init; }

    public double Min { get; init; }

    public double Max { get; init; } = 1_000_000;

    /// <summary>The values of a <see cref="PluginSettingKind.Choice"/> and the texts shown for them.</summary>
    public IReadOnlyList<(string Value, string Text)> Choices { get; init; } = [];
}

/// <summary>The values of a plugin's settings (what the user set, else the setting's default).</summary>
public interface IPluginSettingValues
{
    /// <summary>The value as text; null when it is empty and has no default.</summary>
    string? Get(string key);

    bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);

    double GetNumber(string key) => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>
    /// A <see cref="PluginSettingKind.Secret"/> from the password store, or null when none is saved. Ask when it is
    /// needed (connecting ...), not in <see cref="IConfigurablePlugin.Apply"/>: a locked store asks for the master password.
    /// </summary>
    Task<string?> GetSecretAsync(string key) => Task.FromResult<string?>(null);
}

/// <summary>
/// A plugin with settings: the program shows them on the Plugins page of the configuration (a group per plugin, found by
/// the search too) and opens it from the plugins window ("Configure...") and from a message that the plugin cannot be
/// used. Implemented by any public class of the plugin with a parameterless constructor (its own settings class, or one
/// of its file systems, columns ...). The values are kept in the settings under the plugin's name; the plugin never
/// shows its own window for them.
/// </summary>
public interface IConfigurablePlugin
{
    /// <summary>The title of the plugin's group of settings ("Docker").</summary>
    string SettingsTitle { get; }

    IReadOnlyList<PluginSetting> Settings { get; }

    /// <summary>Why the values cannot be used (OK / Apply are refused and this is shown), or null when they can.</summary>
    string? Validate(IPluginSettingValues values) => null;

    /// <summary>Takes the values: when the plugin is loaded and after the user changed them (no restart).</summary>
    void Apply(IPluginSettingValues values);
}

/// <summary>A plugin's values from the settings (key → text), with the defaults of its settings, and its secrets from the password store.</summary>
public sealed class PluginSettingValues(IReadOnlyList<PluginSetting> settings, IReadOnlyDictionary<string, string>? values,
    Func<string, Task<string?>>? secrets = null) : IPluginSettingValues
{
    /// <summary>The id of a plugin's secret in the password store.</summary>
    public static string SecretId(string plugin, string key) => $"plugin:{plugin}:{key}";

    public Task<string?> GetSecretAsync(string key) =>
        secrets is not null && settings.Any(s => s.Key == key && s.Kind == PluginSettingKind.Secret) ? secrets(key) : Task.FromResult<string?>(null);

    public string? Get(string key)
    {
        if (values is not null && values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }
        return settings.FirstOrDefault(s => s.Key == key)?.Default;
    }
}
