using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextCommander.Core.Config;

/// <summary>
/// A color theme file (<c>themes/&lt;name&gt;.json</c>). Without a theme the application uses the system colors. Users can
/// add any number of themes: the application lists the files next to the program (<c>Themes</c>) and in the settings
/// folder (<c>themes</c>; a file there with the same name replaces the shipped one).
/// <code>
/// {
///   "Name": "Dark",
///   "Base": "Dark",                       // Light or Dark: the variant the theme is drawn on
///   "Colors": {
///     "Window.Background": "#1E1E1E",     // key = area of the window, value = "#RRGGBB" or "#AARRGGBB"
///     "FileList.Text": "#FFFFFF"
///   }
/// }
/// </code>
/// Every color is optional; what a theme leaves out keeps the default of its base variant. The known keys and what
/// they color are listed by the application (<c>ColorThemes.Keys</c>) and written with comments when a theme is saved.
/// </summary>
public sealed class ColorTheme
{
    public const string Extension = ".json";

    /// <summary>The shipped dark theme (also what dark mode of older settings files became).</summary>
    public const string DarkThemeId = "dark";

    /// <summary>
    /// "Follow the system": the system colors while the operating system is in light mode, the dark theme while it is in
    /// dark mode (switching along when the system does). The default.
    /// </summary>
    public const string SystemThemeId = "system";

    private static readonly JsonSerializerOptions s_json = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Shown name (the file name when empty).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"Light" or "Dark".</summary>
    public string Base { get; set; } = "Light";

    /// <summary>Colors by area key ("FileList.Text", "Window.Background" ...).</summary>
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File name without extension: the value stored in the settings.</summary>
    [JsonIgnore]
    public string Id { get; set; } = string.Empty;

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public bool IsDark => string.Equals(Base, "Dark", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>Reads a theme file; null when it cannot be read.</summary>
    public static ColorTheme? Load(string path)
    {
        try
        {
            var theme = JsonSerializer.Deserialize<ColorTheme>(File.ReadAllText(path), s_json);
            if (theme is null)
            {
                return null;
            }
            theme.Id = Path.GetFileNameWithoutExtension(path);
            theme.FilePath = path;
            theme.Colors = new Dictionary<string, string>(theme.Colors ?? [], StringComparer.OrdinalIgnoreCase);
            return theme;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the theme; <paramref name="descriptions"/> (key → what it colors) become comments and give the order
    /// (keys without a description follow).
    /// </summary>
    public void Save(string path, IReadOnlyList<(string Key, string Description)>? descriptions = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var order = descriptions ?? [];
        var keys = order.Select(d => d.Key).Where(Colors.ContainsKey)
            .Concat(Colors.Keys.Where(k => !order.Any(d => string.Equals(d.Key, k, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"Name\": ").Append(Quote(Name)).Append(",\n");
        text.Append("  \"Base\": ").Append(Quote(IsDark ? "Dark" : "Light")).Append(",   // Light or Dark\n");
        text.Append("  \"Colors\": {\n");
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            text.Append("    ").Append(Quote(key)).Append(": ").Append(Quote(Colors[key])).Append(i < keys.Count - 1 ? "," : string.Empty);
            var description = order.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)).Description;
            if (!string.IsNullOrEmpty(description))
            {
                text.Append("   // ").Append(description);
            }
            text.Append('\n');
        }
        text.Append("  }\n}\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        Id = Path.GetFileNameWithoutExtension(path);
        FilePath = path;
    }

    private static readonly JsonSerializerOptions s_quote = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Quote(string value) => JsonSerializer.Serialize(value, s_quote);

    /// <summary>A copy to edit.</summary>
    public ColorTheme Clone() => new()
    {
        Name = Name,
        Base = Base,
        Colors = new Dictionary<string, string>(Colors, StringComparer.OrdinalIgnoreCase),
        Id = Id,
        FilePath = FilePath,
    };

    /// <summary>All themes of the folders, by name; a later folder replaces a theme of the same file name.</summary>
    public static IReadOnlyList<ColorTheme> LoadAll(IEnumerable<string?> directories)
    {
        var themes = new Dictionary<string, ColorTheme>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in directories)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(dir, "*" + Extension))
            {
                if (Load(file) is { } theme)
                {
                    themes[theme.Id] = theme;
                }
            }
        }
        return themes.Values.OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A file name made from a theme name (letters, digits, '-' and '_').</summary>
    public static string FileNameFor(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var id = new string(chars).Trim('-');
        return (id.Length == 0 ? "theme" : id) + Extension;
    }
}
