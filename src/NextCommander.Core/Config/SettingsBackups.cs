using System.Text.Json;
using System.Text.Json.Nodes;

namespace NextCommander.Core.Config;

/// <summary>A part of the settings that can be restored on its own (Restore settings from a backup).</summary>
public enum SettingsPart
{
    /// <summary>Every option not in another part (layout, display, copy ...); the window and the panels' folders stay.</summary>
    Settings,
    Connections,
    Hotlist,
    Shortcuts,
    ButtonBars,
    ColorTheme,
}

/// <summary>What restoring would change in a part: "add" (back), "del" (removed), "chg" (another value).</summary>
public sealed record SettingsChange(string Op, string Name, string? From, string? To);

/// <summary>What is noted about a backup besides its file (backups/index.json): why it was made, a name, pinned.</summary>
public sealed class SettingsBackupInfo
{
    /// <summary>"start" (the settings that loaded), "manual" (Back up now), "file" (added from a file), "before:&lt;action&gt;".</summary>
    public string Trigger { get; set; } = "start";

    public string? Label { get; set; }

    /// <summary>Never removed automatically (nor deleted before it is unpinned).</summary>
    public bool Pinned { get; set; }
}

/// <summary>
/// The parts of the settings, what restoring a backup would change in each, and a backup's parts put into the current
/// settings (the user's template of 24 November 2026: a restore of chosen parts, each change shown).
/// </summary>
public static class SettingsDiff
{
    /// <summary>The settings properties of each part (the rest belongs to <see cref="SettingsPart.Settings"/>).</summary>
    private static readonly Dictionary<SettingsPart, string[]> s_parts = new()
    {
        [SettingsPart.Connections] = [nameof(AppSettings.Connections), nameof(AppSettings.ConnectionFolders)],
        [SettingsPart.Hotlist] = [nameof(AppSettings.Hotlist)],
        [SettingsPart.Shortcuts] = [nameof(AppSettings.Hotkeys)],
        [SettingsPart.ButtonBars] = [nameof(AppSettings.ButtonBar), nameof(AppSettings.VerticalButtonBar)],
        [SettingsPart.ColorTheme] = [nameof(AppSettings.ColorThemeFile)],
    };

    /// <summary>
    /// What changes by itself while the program is used (the window, the panels' folders, histories, what was used last):
    /// not compared and not restored.
    /// </summary>
    private static readonly HashSet<string> s_volatile = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.Window), nameof(AppSettings.Left), nameof(AppSettings.Right),
        nameof(AppSettings.LeftActive), nameof(AppSettings.SettingsLastPage), nameof(AppSettings.CommandLineHistory),
        nameof(AppSettings.FrequentDirectories), nameof(AppSettings.CommandPaletteRecent), nameof(AppSettings.SettingsVersion),
    };

    public static IReadOnlyList<SettingsPart> Parts { get; } = Enum.GetValues<SettingsPart>();

    private static bool IsVolatile(string property) =>
        s_volatile.Contains(property) || property.EndsWith("History", StringComparison.Ordinal);

    private static SettingsPart PartOf(string property) =>
        s_parts.FirstOrDefault(p => p.Value.Contains(property)).Key is var part && s_parts.ContainsKey(part) ? part : SettingsPart.Settings;

    private static JsonObject ToJson(AppSettings settings) =>
        JsonSerializer.SerializeToNode(settings, SettingsStore.JsonOptions)!.AsObject();

    /// <summary>The settings as text for comparing: without what changes by itself (the same settings give the same text).</summary>
    public static string Normalized(AppSettings settings)
    {
        var json = ToJson(settings);
        foreach (var name in json.Select(p => p.Key).Where(IsVolatile).ToList())
        {
            json.Remove(name);
        }
        return json.ToJsonString();
    }

    /// <summary>The current settings with the chosen parts of a backup (a new object; the window and the panels stay).</summary>
    public static AppSettings Merge(AppSettings current, AppSettings backup, IReadOnlyCollection<SettingsPart> parts)
    {
        var result = ToJson(current);
        var from = ToJson(backup);
        foreach (var (name, value) in from.ToList())
        {
            if (!IsVolatile(name) && parts.Contains(PartOf(name)))
            {
                result[name] = value?.DeepClone();
            }
        }
        foreach (var name in result.Select(p => p.Key).ToList())
        {
            if (!from.ContainsKey(name) && !IsVolatile(name) && parts.Contains(PartOf(name)))
            {
                result.Remove(name); // not in the backup (an older version): the default
            }
        }
        var merged = result.Deserialize<AppSettings>(SettingsStore.JsonOptions) ?? new AppSettings();
        merged.SettingsVersion = current.SettingsVersion;
        return merged;
    }

    /// <summary>What restoring a part of <paramref name="backup"/> would change in <paramref name="current"/>.</summary>
    public static IReadOnlyList<SettingsChange> Changes(SettingsPart part, AppSettings backup, AppSettings current)
    {
        var changes = new List<SettingsChange>();
        switch (part)
        {
            case SettingsPart.Connections:
                Compare(changes, backup.Connections.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First()),
                    current.Connections.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First()),
                    c => c.Authority, (a, b) => JsonSerializer.Serialize(a, SettingsStore.JsonOptions) == JsonSerializer.Serialize(b, SettingsStore.JsonOptions));
                var folders = backup.ConnectionFolders.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
                var now = current.ConnectionFolders.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
                changes.AddRange(folders.Except(now).Select(f => new SettingsChange("add", f + "/", null, null)));
                changes.AddRange(now.Except(folders).Select(f => new SettingsChange("del", f + "/", null, null)));
                break;
            case SettingsPart.Hotlist:
                Compare(changes, backup.Hotlist.GroupBy(h => h.Name).ToDictionary(g => g.Key, g => g.First()),
                    current.Hotlist.GroupBy(h => h.Name).ToDictionary(g => g.Key, g => g.First()),
                    h => h.Path, (a, b) => a.Path == b.Path && a.Target == b.Target);
                break;
            case SettingsPart.Shortcuts:
                Compare(changes, backup.Hotkeys, current.Hotkeys, c => c, string.Equals);
                break;
            case SettingsPart.ButtonBars:
                CompareBar(changes, backup.ButtonBar, current.ButtonBar, string.Empty);
                CompareBar(changes, backup.VerticalButtonBar, current.VerticalButtonBar, "Vertical bar › ");
                break;
            case SettingsPart.ColorTheme:
                if (!string.Equals(backup.ColorThemeFile, current.ColorThemeFile, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(new SettingsChange("chg", "Theme", ThemeName(current.ColorThemeFile), ThemeName(backup.ColorThemeFile)));
                }
                break;
            default:
                var a = ToJson(backup);
                var b = ToJson(current);
                foreach (var name in a.Select(p => p.Key).Union(b.Select(p => p.Key)).Where(n => !IsVolatile(n) && PartOf(n) == SettingsPart.Settings))
                {
                    if (a[name]?.ToJsonString() != b[name]?.ToJsonString())
                    {
                        changes.Add(new SettingsChange("chg", Readable(name), Shown(b[name]), Shown(a[name])));
                    }
                }
                break;
        }
        return changes;
    }

    private static void Compare<T>(List<SettingsChange> changes, IReadOnlyDictionary<string, T> backup, IReadOnlyDictionary<string, T> current,
        Func<T, string> shown, Func<T, T, bool> same)
    {
        foreach (var (name, value) in backup)
        {
            if (!current.TryGetValue(name, out var now))
            {
                changes.Add(new SettingsChange("add", name, null, shown(value)));
            }
            else if (!same(value, now))
            {
                changes.Add(new SettingsChange("chg", name, shown(now), shown(value)));
            }
        }
        foreach (var (name, value) in current)
        {
            if (!backup.ContainsKey(name))
            {
                changes.Add(new SettingsChange("del", name, shown(value), null));
            }
        }
    }

    private static void CompareBar(List<SettingsChange> changes, List<ButtonDefinition> backup, List<ButtonDefinition> current, string prefix)
    {
        static string Name(ButtonDefinition b) => b.IsSeparator ? "—" : !string.IsNullOrWhiteSpace(b.Tooltip) ? b.Tooltip! : b.Command;
        var was = backup.Where(b => !b.IsSeparator).Select(Name).ToList();
        var now = current.Where(b => !b.IsSeparator).Select(Name).ToList();
        var bar = new List<SettingsChange>();
        bar.AddRange(now.Except(was).Select(n => new SettingsChange("del", prefix + n, null, null)));
        bar.AddRange(was.Except(now).Select(n => new SettingsChange("add", prefix + n, null, null)));
        if (bar.Count == 0 && JsonSerializer.Serialize(backup, SettingsStore.JsonOptions) != JsonSerializer.Serialize(current, SettingsStore.JsonOptions))
        {
            bar.Add(new SettingsChange("chg", prefix + "Button order", null, null)); // the same buttons, another order or look
        }
        changes.AddRange(bar);
    }

    private static string ThemeName(string? id) => string.IsNullOrEmpty(id) ? "System" : id;

    /// <summary>"PanelFontSize" → "Panel font size".</summary>
    public static string Readable(string property)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < property.Length; i++)
        {
            var c = property[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(property[i - 1]) || (i + 1 < property.Length && char.IsLower(property[i + 1]))))
            {
                builder.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    /// <summary>A value as it is shown: plain values as they are (a long one cut), lists and groups as "(changed)".</summary>
    private static string? Shown(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => value.ToJsonString().Trim('"') is var text && text.Length > 60 ? text[..57] + "..." : value.ToJsonString().Trim('"'),
        JsonArray array => $"{array.Count} items",
        _ => "…",
    };
}
