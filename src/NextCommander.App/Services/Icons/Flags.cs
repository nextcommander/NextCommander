using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace NextCommander.App.Services.Icons;

/// <summary>
/// Small vector flags for the language list (Configuration > Language): a language code ("tr", "en", "de-AT") gives the
/// flag of the country most associated with it; unknown codes get none.
/// </summary>
public static class Flags
{
    private const double W = 30;
    private const double H = 20;

    private static IBrush B(string color) => new SolidColorBrush(Color.Parse(color));

    /// <summary>Horizontal or vertical stripes of equal (or given) share.</summary>
    private static Canvas Stripes(bool vertical, params (string Color, double Share)[] stripes)
    {
        var canvas = new Canvas { Width = W, Height = H, ClipToBounds = true };
        var total = stripes.Sum(s => s.Share);
        var at = 0.0;
        foreach (var (color, share) in stripes)
        {
            var size = (vertical ? W : H) * share / total;
            var rect = new Rectangle { Fill = B(color), Width = vertical ? size + 0.3 : W, Height = vertical ? H : size + 0.3 };
            Canvas.SetLeft(rect, vertical ? at : 0);
            Canvas.SetTop(rect, vertical ? 0 : at);
            canvas.Children.Add(rect);
            at += size;
        }
        return canvas;
    }

    private static Canvas Plain(string color) => Stripes(false, (color, 1));

    private static void Add(Canvas canvas, Control shape, double left = 0, double top = 0)
    {
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        canvas.Children.Add(shape);
    }

    /// <summary>A five-pointed star centred at (x, y) with outer radius r, one point up (rotated by <paramref name="angle"/> degrees).</summary>
    private static Geometry Star(double x, double y, double r, double angle = -90)
    {
        var points = new List<Point>();
        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? r : r * 0.382;
            var a = (angle + i * 36) * Math.PI / 180;
            points.Add(new Point(x + radius * Math.Cos(a), y + radius * Math.Sin(a)));
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true);
            foreach (var p in points.Skip(1))
            {
                context.LineTo(p);
            }
            context.EndFigure(isClosed: true);
        }
        return geometry;
    }

    private static Canvas Turkey()
    {
        var canvas = Plain("#E30A17");
        Add(canvas, new Ellipse { Width = 10, Height = 10, Fill = Brushes.White }, 5.5, 5);
        Add(canvas, new Ellipse { Width = 8, Height = 8, Fill = B("#E30A17") }, 7.75, 6);
        // the star (its point to the left, towards the crescent)
        canvas.Children.Add(new Path { Data = Star(17.3, 10, 2.6, 180), Fill = Brushes.White });
        return canvas;
    }

    private static Canvas UnitedKingdom()
    {
        var canvas = Plain("#012169");
        Line Bar(double x1, double y1, double x2, double y2, string color, double thickness) =>
            new() { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = B(color), StrokeThickness = thickness };
        canvas.Children.Add(Bar(0, 0, W, H, "#FFFFFF", 4));
        canvas.Children.Add(Bar(W, 0, 0, H, "#FFFFFF", 4));
        canvas.Children.Add(Bar(0, 0, W, H, "#C8102E", 1.4));
        canvas.Children.Add(Bar(W, 0, 0, H, "#C8102E", 1.4));
        Add(canvas, new Rectangle { Width = W, Height = 6, Fill = Brushes.White }, 0, 7);
        Add(canvas, new Rectangle { Width = 6, Height = H, Fill = Brushes.White }, 12, 0);
        Add(canvas, new Rectangle { Width = W, Height = 3.6, Fill = B("#C8102E") }, 0, 8.2);
        Add(canvas, new Rectangle { Width = 3.6, Height = H, Fill = B("#C8102E") }, 13.2, 0);
        return canvas;
    }

    private static Canvas Japan()
    {
        var canvas = Plain("#FFFFFF");
        Add(canvas, new Ellipse { Width = 12, Height = 12, Fill = B("#BC002D") }, 9, 4);
        return canvas;
    }

    /// <summary>China: the flag is drawn on a 30 × 20 grid, the same as this canvas; the small stars point at the big one.</summary>
    private static Canvas China()
    {
        var canvas = Plain("#EE1C25");
        var yellow = B("#FFFF00");
        canvas.Children.Add(new Path { Data = Star(5, 5, 3), Fill = yellow });
        foreach (var (x, y) in new[] { (10.0, 2.0), (12.0, 4.0), (12.0, 7.0), (10.0, 9.0) })
        {
            var towards = Math.Atan2(5 - y, 5 - x) * 180 / Math.PI;
            canvas.Children.Add(new Path { Data = Star(x, y, 1, towards), Fill = yellow });
        }
        return canvas;
    }

    private static Canvas Portugal()
    {
        var canvas = Stripes(true, ("#046A38", 2), ("#DA291C", 3));
        Add(canvas, new Ellipse { Width = 8, Height = 8, Fill = B("#FFE900") }, 8, 6);
        return canvas;
    }

    /// <summary>The flag drawing of a language code, or null.</summary>
    private static Canvas? Drawing(string code) => code.Split('-', '_')[0].ToLowerInvariant() switch
    {
        "tr" => Turkey(),
        "en" => UnitedKingdom(),
        "de" => Stripes(false, ("#000000", 1), ("#DD0000", 1), ("#FFCE00", 1)),
        "fr" => Stripes(true, ("#0055A4", 1), ("#FFFFFF", 1), ("#EF4135", 1)),
        "it" => Stripes(true, ("#009246", 1), ("#FFFFFF", 1), ("#CE2B37", 1)),
        "es" => Stripes(false, ("#AA151B", 1), ("#F1BF00", 2), ("#AA151B", 1)),
        "nl" => Stripes(false, ("#AE1C28", 1), ("#FFFFFF", 1), ("#21468B", 1)),
        "ru" => Stripes(false, ("#FFFFFF", 1), ("#0039A6", 1), ("#D52B1E", 1)),
        "pl" => Stripes(false, ("#FFFFFF", 1), ("#DC143C", 1)),
        "uk" => Stripes(false, ("#0057B7", 1), ("#FFD700", 1)),
        "ja" => Japan(),
        "pt" => Portugal(),
        "zh" => China(),
        _ => null,
    };

    /// <summary>Whether there is a flag for a language code.</summary>
    public static bool Has(string code) => Drawing(code) is not null;

    /// <summary>The flag of a language, <paramref name="height"/> pixels high (3:2, a thin frame for white flags), or null.</summary>
    public static Control? Create(string code, double height = 14)
    {
        if (Drawing(code) is not { } canvas)
        {
            return null;
        }
        return new Border
        {
            Width = height * 1.5,
            Height = height,
            BorderThickness = new Thickness(0.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
            Child = new Viewbox { Stretch = Stretch.Fill, Child = canvas },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }
}
