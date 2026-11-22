using System.Globalization;
using System.Text;
using NextCommander.Core.Plugins;
using SkiaSharp;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// Fonts (.ttf, .otf, .ttc, .woff and, where the system's font engine reads it, .woff2): the family, style, weight and
/// number of glyphs, sample lines at several sizes and a table of the characters it has. Drawn from the file itself
/// (nothing is installed), as pictures in the page.
/// </summary>
public sealed class FontViewer : IViewerPlugin
{
    private static readonly string[] s_extensions = [".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2"];

    internal const string Sample = "The quick brown fox jumps over the lazy dog. 0123456789";

    /// <summary>A pangram with the Turkish letters (ç ğ ı İ ö ş ü).</summary>
    internal const string SampleTurkish = "Pijamalı hasta yağız şoföre çabucak güvendi. İĞÜŞÖÇ";

    public string Name => "Font";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) =>
        s_extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        var bytes = await ViewerHtml.ReadAllAsync(content, 100L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Describe(bytes, fileName, dark), cancellationToken).ConfigureAwait(false);
    }

    internal static ViewerContent Describe(byte[] bytes, string fileName, bool dark)
    {
        using var data = SKData.CreateCopy(bytes);
        using var typeface = SKTypeface.FromData(data);
        if (typeface is null)
        {
            var failed = new ViewerHtml(fileName, null, dark).Note("This font cannot be shown (its format is not read by the system's font engine).");
            return new ViewerContent { Html = failed.ToString(), Status = ViewerHtml.T("Font") };
        }
        var style = typeface.FontStyle;
        var styleName = $"{(style.Slant == SKFontStyleSlant.Upright ? ViewerHtml.T("Upright") : ViewerHtml.T("Italic"))} · {WeightName(style.Weight)}";
        var page = new ViewerHtml(typeface.FamilyName, styleName, dark);
        page.Facts(
        [
            ("Family", typeface.FamilyName),
            ("Weight", $"{WeightName(style.Weight)} ({style.Weight})"),
            ("Width", style.Width.ToString(CultureInfo.InvariantCulture)),
            ("Glyphs", typeface.GlyphCount.ToString("N0", CultureInfo.CurrentCulture)),
            ("Fixed width", typeface.IsFixedPitch ? ViewerHtml.T("Yes") : null),
            ("Size", ViewerHtml.Size(bytes.Length)),
        ]);
        var ink = dark ? new SKColor(0xE6, 0xE6, 0xE6) : new SKColor(0x1F, 0x1F, 0x1F);
        page.Heading("Samples");
        foreach (var size in new[] { 12, 18, 24, 36, 48, 72 })
        {
            page.Raw($"<div class=\"muted\">{size} pt</div>").Raw(Image(Line(typeface, Sample, size, ink)));
        }
        page.Raw(Image(Line(typeface, SampleTurkish, 28, ink)));
        page.Heading("Characters").Raw(Image(Characters(typeface, ink, dark)));
        return new ViewerContent { Html = page.ToString(), Status = $"{typeface.FamilyName} · {styleName}" };
    }

    private static string WeightName(int weight) => weight switch
    {
        <= 150 => "Thin",
        <= 250 => "Extra light",
        <= 350 => "Light",
        <= 450 => "Regular",
        <= 550 => "Medium",
        <= 650 => "Semibold",
        <= 750 => "Bold",
        <= 850 => "Extra bold",
        _ => "Black",
    };

    private static string Image(byte[] png) => $"<p><img src=\"data:image/png;base64,{Convert.ToBase64String(png)}\"></p>";

    /// <summary>One line of text at a size, as a PNG with a transparent background.</summary>
    internal static byte[] Line(SKTypeface typeface, string text, float points, SKColor ink)
    {
        using var font = new SKFont(typeface, points * 96 / 72);
        using var paint = new SKPaint { Color = ink, IsAntialias = true };
        var width = (int)Math.Ceiling(Math.Min(font.MeasureText(text), 4000)) + 8;
        var metrics = font.Metrics;
        var height = (int)Math.Ceiling(metrics.Descent - metrics.Ascent) + 8;
        using var bitmap = new SKBitmap(Math.Max(width, 1), Math.Max(height, 1));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawText(text, 4, 4 - metrics.Ascent, SKTextAlign.Left, font, paint);
        }
        return Png(bitmap);
    }

    /// <summary>The characters of Basic Latin, Latin-1 and Latin Extended-A (Turkish letters among them) the font has, in a grid.</summary>
    internal static byte[] Characters(SKTypeface typeface, SKColor ink, bool dark)
    {
        var characters = Enumerable.Range(0x21, 0x7E - 0x20).Concat(Enumerable.Range(0xA1, 0x17F - 0xA0))
            .Where(c => typeface.ContainsGlyph(c)).ToList();
        const int columns = 16;
        const int cell = 44;
        var rows = Math.Max(1, (characters.Count + columns - 1) / columns);
        using var font = new SKFont(typeface, 24);
        using var paint = new SKPaint { Color = ink, IsAntialias = true };
        using var line = new SKPaint { Color = dark ? new SKColor(0x3C, 0x3C, 0x3C) : new SKColor(0xDC, 0xDC, 0xDC), IsStroke = true, StrokeWidth = 1 };
        using var bitmap = new SKBitmap(columns * cell + 1, rows * cell + 1);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            for (var i = 0; i < characters.Count; i++)
            {
                var x = i % columns * cell;
                var y = i / columns * cell;
                canvas.DrawRect(x, y, cell, cell, line);
                var text = char.ConvertFromUtf32(characters[i]);
                canvas.DrawText(text, x + cell / 2f, y + cell / 2f + 9, SKTextAlign.Center, font, paint);
            }
        }
        return Png(bitmap);
    }

    private static byte[] Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
