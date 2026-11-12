using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextCommander.Core.Config;

/// <summary>How the glyphs of an icon pack are drawn.</summary>
public enum IconPackStyle
{
    /// <summary>Filled with the color of each icon.</summary>
    Colored,

    /// <summary>Every icon in one color (<see cref="IconPack.Color"/>, or the icon color of the color theme).</summary>
    Monochrome,

    /// <summary>A thin outline in the icon's color over a light fill of the same color (Fluent look).</summary>
    Outline,
}

/// <summary>
/// An icon pack file (<c>iconpacks/&lt;name&gt;.json</c>): how the built-in icons of the menus, button bars, drive buttons
/// and file list (when the operating system's icons are not used) are drawn. The program ships packs next to it
/// (<c>IconPacks</c>); a file in the settings folder (<c>iconpacks</c>) adds one, or replaces a shipped one of the same name.
/// <code>
/// {
///   "Name": "My pack",
///   "Style": "Outline",                 // Colored, Monochrome or Outline
///   "Color": "#607080",                 // Monochrome: the one color (empty = the theme's icon color)
///   "Badges": true,                     // the small marks (plus, cross, arrow) at the corner of menu icons
///   "Colors": { "Blue": "#0078D4", "File.Folder": "#FFB900" },
///   "Glyphs": { "FmFolderIcon": "M1,3 L6,3 L8,5 L15,5 L15,13 L1,13 Z" }   // SVG path data, 16x16
/// }
/// </code>
/// </summary>
public sealed class IconPack
{
    public const string Extension = ".json";

    /// <summary>The shipped colored pack (the default).</summary>
    public const string DefaultId = "colored";

    /// <summary>The color names of the menu and button bar icons (<see cref="Colors"/>).</summary>
    public static readonly IReadOnlyList<string> PaletteNames = ["Blue", "Green", "Red", "Yellow", "Purple", "Teal", "Orange", "Gray"];

    private static readonly JsonSerializerOptions s_json = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Shown name (the file name when empty).</summary>
    public string Name { get; set; } = string.Empty;

    public IconPackStyle Style { get; set; } = IconPackStyle.Colored;

    /// <summary>Monochrome: "#RRGGBB" (empty: the icon color of the color theme, so it suits light and dark themes).</summary>
    public string? Color { get; set; }

    /// <summary>Whether menu icons keep their small corner marks.</summary>
    public bool Badges { get; set; } = true;

    /// <summary>Colors by name: the palette (<see cref="PaletteNames"/>) and the file types ("File.Folder", "File.Archive" ...).</summary>
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Glyph shapes replacing the built-in ones by resource key ("FmFolderIcon", "MiCopy" ...): SVG path data on a 16x16 grid.</summary>
    public Dictionary<string, string> Glyphs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File name without extension: the value stored in the settings.</summary>
    [JsonIgnore]
    public string Id { get; set; } = string.Empty;

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>Reads a pack file; null when it cannot be read.</summary>
    public static IconPack? Load(string path)
    {
        try
        {
            var pack = JsonSerializer.Deserialize<IconPack>(File.ReadAllText(path), s_json);
            if (pack is null)
            {
                return null;
            }
            pack.Id = Path.GetFileNameWithoutExtension(path);
            pack.FilePath = path;
            pack.Colors = new Dictionary<string, string>(pack.Colors ?? [], StringComparer.OrdinalIgnoreCase);
            pack.Glyphs = new Dictionary<string, string>(pack.Glyphs ?? [], StringComparer.OrdinalIgnoreCase);
            return pack;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes the pack (the icon pack editor of the Configuration window).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, s_write), new System.Text.UTF8Encoding(false));
        Id = Path.GetFileNameWithoutExtension(path);
        FilePath = path;
    }

    /// <summary>A copy to edit (the colors and shapes too).</summary>
    public IconPack Clone() => new()
    {
        Name = Name,
        Style = Style,
        Color = Color,
        Badges = Badges,
        Colors = new Dictionary<string, string>(Colors, StringComparer.OrdinalIgnoreCase),
        Glyphs = new Dictionary<string, string>(Glyphs, StringComparer.OrdinalIgnoreCase),
        Id = Id,
        FilePath = FilePath,
    };

    /// <summary>A file name for a pack name ("My pack" → "my-pack.json").</summary>
    public static string FileNameFor(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var file = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return (file.Length > 0 ? file : "iconpack") + Extension;
    }

    /// <summary>All packs of the folders, by name; a later folder replaces a pack of the same file name.</summary>
    public static IReadOnlyList<IconPack> LoadAll(IEnumerable<string?> directories)
    {
        var packs = new Dictionary<string, IconPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in directories)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(dir, "*" + Extension))
            {
                if (Load(file) is { } pack)
                {
                    packs[pack.Id] = pack;
                }
            }
        }
        // the default first, then by name
        return packs.Values
            .OrderBy(p => string.Equals(p.Id, DefaultId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
