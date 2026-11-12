using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using NextCommander.Core.Config;
using Shape = Avalonia.Controls.Shapes.Path;

namespace NextCommander.App.Services.Icons;

/// <summary>
/// The icon pack in use (Configuration > Icons > Icon pack): the glyphs and colors of the built-in icons of the menus,
/// button bars, drive buttons and file list. See <see cref="IconPack"/> for the file format.
/// </summary>
public static class IconPacks
{
    /// <summary>The theme brush monochrome icons use when the pack names no color (follows light / dark themes).</summary>
    public const string MonochromeBrushKey = "FmDriveIconBrush";

    private static readonly ConcurrentDictionary<(string Pack, string Key), Geometry?> s_glyphs = new();

    /// <summary>&lt;program&gt;/IconPacks (shipped packs).</summary>
    public static string BuiltInDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "IconPacks");

    /// <summary>&lt;settings&gt;/iconpacks (the user's packs; set at start, null in tests).</summary>
    public static string? UserDirectory { get; set; }

    /// <summary>The pack in use; a plain colored pack when no file is found.</summary>
    public static IconPack Current { get; private set; } = new() { Id = IconPack.DefaultId };

    /// <summary>Raised after <see cref="Apply"/> changed the pack.</summary>
    public static event EventHandler? Changed;

    public static IReadOnlyList<IconPack> Available() => IconPack.LoadAll([BuiltInDirectory, UserDirectory]);

    public static IconPack? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Available().FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Uses the pack <paramref name="id"/> (the default pack when it does not exist). The file is read again each time, so a
    /// pack edited while the program runs takes effect with the settings window's Apply.
    /// </summary>
    public static void Apply(string? id) => Use(Find(id) ?? Find(IconPack.DefaultId) ?? new IconPack { Id = IconPack.DefaultId });

    /// <summary>Makes <paramref name="pack"/> the current one (also for tests, without a file).</summary>
    public static void Use(IconPack pack)
    {
        Current = pack;
        s_glyphs.Clear();
        FileKinds.Reset();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The glyph <paramref name="key"/> of a pack: its own shape, or the built-in resource.</summary>
    public static Geometry? Glyph(string key, IconPack? pack = null)
    {
        pack ??= Current;
        return s_glyphs.GetOrAdd((pack.Id + "|" + pack.FilePath, key), _ =>
        {
            if (pack.Glyphs.TryGetValue(key, out var data) && !string.IsNullOrWhiteSpace(data))
            {
                try
                {
                    return Geometry.Parse(data);
                }
                catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or InvalidOperationException)
                {
                    // not path data: the built-in glyph
                }
            }
            return Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;
        });
    }

    /// <summary>A color of the pack by name ("Blue", "File.Folder"), or <paramref name="fallback"/>.</summary>
    public static IBrush Color(string name, IBrush fallback, IconPack? pack = null)
    {
        pack ??= Current;
        return pack.Colors.TryGetValue(name, out var text) && Avalonia.Media.Color.TryParse(text, out var color)
            ? new ImmutableSolidColorBrush(color)
            : fallback;
    }

    /// <summary>The one color of a monochrome pack (its own, or the theme's icon color at this moment).</summary>
    public static IBrush MonochromeBrush(IconPack? pack = null)
    {
        pack ??= Current;
        if (Avalonia.Media.Color.TryParse(pack.Color, out var color))
        {
            return new ImmutableSolidColorBrush(color);
        }
        return Application.Current?.TryGetResource(MonochromeBrushKey, Application.Current.ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Gray;
    }

    /// <summary>The light fill of the outline style: the icon color at a quarter strength.</summary>
    public static IBrush Tint(IBrush brush) =>
        brush is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, 0.22) : brush;

    /// <summary>
    /// A 16x16 shape of <paramref name="glyph"/> in <paramref name="color"/>, drawn as the pack says (filled, one color, or
    /// outlined). Monochrome icons without a color of their own follow the theme.
    /// </summary>
    public static Shape Draw(Geometry glyph, IBrush color, IconPack? pack = null)
    {
        pack ??= Current;
        var shape = new Shape { Data = glyph };
        switch (pack.Style)
        {
            case IconPackStyle.Monochrome when Avalonia.Media.Color.TryParse(pack.Color, out var own):
                shape.Fill = new ImmutableSolidColorBrush(own);
                break;
            case IconPackStyle.Monochrome:
                shape[!Shape.FillProperty] = shape.GetResourceObservable(MonochromeBrushKey).ToBinding();
                break;
            case IconPackStyle.Outline:
                shape.Fill = Tint(color);
                shape.Stroke = color;
                shape.StrokeThickness = 1;
                break;
            default:
                shape.Fill = color;
                break;
        }
        return shape;
    }
}
