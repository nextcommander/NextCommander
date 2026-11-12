using System.Reflection;
using System.Xml;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;

namespace NextCommander.App.Views.Lister;

/// <summary>
/// The built-in highlighting definitions are made for light backgrounds. In dark mode the editor gets a copy of its
/// definition whose named colors (Comment, String, Keywords…) use the VS Code "Dark+" palette; a line transformer
/// lightens any remaining dark inline colors.
/// </summary>
public sealed class ThemeAwareColors : IVisualLineTransformer
{
    private static readonly Dictionary<string, IHighlightingDefinition?> s_dark = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<IHighlightingDefinition> s_darkSet = [];
    private static Dictionary<string, string>? s_resources;

    private readonly TextEditor _editor;
    private readonly Dictionary<Color, IBrush?> _cache = [];
    private IHighlightingDefinition? _light;
    private bool _swapping;

    private ThemeAwareColors(TextEditor editor)
    {
        _editor = editor;
    }

    public static void Attach(TextEditor editor)
    {
        var colors = new ThemeAwareColors(editor);
        editor.TextArea.TextView.LineTransformers.Add(colors);
        var view = editor.TextArea.TextView;
        var lightLink = view.LinkTextForegroundBrush;
        var lightLineBackground = view.CurrentLineBackground;
        var lightLineBorder = view.CurrentLineBorder;
        void Update()
        {
            var dark = editor.ActualThemeVariant == ThemeVariant.Dark;
            view.LinkTextForegroundBrush = dark ? new ImmutableSolidColorBrush(Color.FromRgb(0x37, 0x94, 0xFF)) : lightLink;
            // VS Code: no fill, a subtle #282828 frame around the current line
            view.CurrentLineBackground = dark ? Brushes.Transparent : lightLineBackground;
            view.CurrentLineBorder = dark ? new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), 2) : lightLineBorder;
            colors.ApplyDefinition();
            editor.TextArea.TextView.Redraw();
        }
        editor.ActualThemeVariantChanged += (_, _) => Update();
        editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextEditor.SyntaxHighlightingProperty && !colors._swapping)
            {
                colors._light = editor.SyntaxHighlighting is { } d && s_darkSet.Contains(d) ? colors._light : editor.SyntaxHighlighting;
                colors.ApplyDefinition();
            }
        };
        colors._light = editor.SyntaxHighlighting;
        Update();
    }

    /// <summary>Shows the dark copy of the current definition in dark mode and the original in light mode.</summary>
    private void ApplyDefinition()
    {
        var wanted = _light is null ? null : _editor.ActualThemeVariant == ThemeVariant.Dark ? DarkVersion(_light) ?? _light : _light;
        if (!ReferenceEquals(wanted, _editor.SyntaxHighlighting))
        {
            _swapping = true;
            try
            {
                _editor.SyntaxHighlighting = wanted;
            }
            finally
            {
                _swapping = false;
            }
        }
    }

    /// <summary>The definition reloaded from its built-in .xshd with VS Code colors (null if it is not a built-in one).</summary>
    public static IHighlightingDefinition? DarkVersion(IHighlightingDefinition light)
    {
        if (s_darkSet.Contains(light))
        {
            return light;
        }
        lock (s_dark)
        {
            if (s_dark.TryGetValue(light.Name, out var cached))
            {
                return cached;
            }
            IHighlightingDefinition? dark = null;
            try
            {
                s_resources ??= LoadResourceNames();
                if (s_resources.TryGetValue(light.Name, out var resource))
                {
                    var assembly = typeof(HighlightingManager).Assembly;
                    using var stream = assembly.GetManifestResourceStream(resource)!;
                    using var reader = XmlReader.Create(stream);
                    var xshd = HighlightingLoader.LoadXshd(reader);
                    foreach (var color in xshd.Elements.OfType<XshdColor>())
                    {
                        if (color.Name is { } name && VsCodeColor(name) is { } vs)
                        {
                            color.Foreground = new SimpleHighlightingBrush(vs);
                        }
                    }
                    dark = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
                    s_darkSet.Add(dark);
                }
            }
            catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException or InvalidOperationException or IOException)
            {
                dark = null;
            }
            s_dark[light.Name] = dark;
            return dark;
        }
    }

    /// <summary>Built-in definition name to its embedded .xshd resource.</summary>
    private static Dictionary<string, string> LoadResourceNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(HighlightingManager).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".xshd", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }
                using var reader = XmlReader.Create(stream);
                if (HighlightingLoader.LoadXshd(reader).Name is { } name)
                {
                    result.TryAdd(name, resource);
                }
            }
            catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException)
            {
                // skip broken resources
            }
        }
        return result;
    }

    private static readonly (string[] Parts, Color Color)[] s_rules =
    [
        (["Comment"], Color.FromRgb(0x6A, 0x99, 0x55)),
        (["String", "Char", "AttributeValue", "CData", "Verbatim", "Quote"], Color.FromRgb(0xCE, 0x91, 0x78)),
        (["Number", "Digit"], Color.FromRgb(0xB5, 0xCE, 0xA8)),
        (["Preprocessor", "Directive", "Goto", "Exception", "Return", "Jump", "Loop", "Control", "Conditional", "Include"], Color.FromRgb(0xC5, 0x86, 0xC0)),
        (["MethodCall", "Function", "Method"], Color.FromRgb(0xDC, 0xDC, 0xAA)),
        (["Attribute", "Variable", "Parameter", "Property"], Color.FromRgb(0x9C, 0xDC, 0xFE)),
        (["Entity", "Escape", "Regex"], Color.FromRgb(0xD7, 0xBA, 0x7D)),
        (["Punctuation", "Operator"], Color.FromRgb(0xD4, 0xD4, 0xD4)),
        (["Class", "Interface", "Struct", "Enum", "TypeName"], Color.FromRgb(0x4E, 0xC9, 0xB0)),
        (["Keyword", "Modifier", "Visibility", "Namespace", "Type", "TrueFalse", "Null", "This", "Tag", "Element", "Heading", "Declaration", "DocType"],
            Color.FromRgb(0x56, 0x9C, 0xD6)),
    ];

    /// <summary>VS Code Dark+ color for a highlighting color name (null: keep and only lighten if needed).</summary>
    public static Color? VsCodeColor(string name)
    {
        foreach (var (parts, color) in s_rules)
        {
            if (parts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                return color;
            }
        }
        return null;
    }

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        if (_editor.ActualThemeVariant != ThemeVariant.Dark)
        {
            return;
        }
        foreach (var element in elements)
        {
            if (element.TextRunProperties.ForegroundBrush is ISolidColorBrush { } brush && Lift(brush.Color) is { } lifted)
            {
                element.TextRunProperties.SetForegroundBrush(lifted);
            }
        }
    }

    /// <summary>Returns a lighter brush for colors too dark to read on a dark background, otherwise null.</summary>
    private IBrush? Lift(Color color)
    {
        if (_cache.TryGetValue(color, out var cached))
        {
            return cached;
        }
        var hsl = color.ToHsl();
        IBrush? result = null;
        if (hsl.L < 0.6 || RelativeLuminance(color) < 0.18)
        {
            var lifted = new HslColor(hsl.A, hsl.H, Math.Min(hsl.S, 0.85), Math.Max(0.72, hsl.L)).ToRgb();
            result = new ImmutableSolidColorBrush(lifted);
        }
        _cache[color] = result;
        return result;
    }

    private static double RelativeLuminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
