using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using NextCommander.Core.Formatting;
using NextCommander.Core.Tools;

namespace NextCommander.App.Controls;

/// <summary>
/// The treemap of a disk usage scan: every file is a rectangle sized by its bytes and colored by its type; a folder is
/// the area of its files, with a strip for its name and size when there is room. The files of the highlighted type stay
/// bright, the others fade. The selected file or folder is outlined, the one under the pointer framed; a tooltip tells
/// its name, folder, size and share.
/// </summary>
public sealed class TreemapControl : Control
{
    private const double Strip = 16;

    private readonly List<(UsageNode Node, TreemapRect Rect)> _layout = [];
    private readonly List<(UsageNode Node, TreemapRect Rect, bool Titled)> _folders = [];
    private readonly Dictionary<UsageNode, TreemapRect> _rects = [];
    private readonly Dictionary<Color, IBrush> _fills = [];
    private Size _laidOutFor;
    private UsageNode? _laidOutRoot;
    private UsageNode? _hover;
    private static readonly Typeface s_bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface s_normal = new(FontFamily.Default);

    public static readonly StyledProperty<UsageNode?> RootProperty = AvaloniaProperty.Register<TreemapControl, UsageNode?>(nameof(Root));

    public static readonly StyledProperty<UsageNode?> SelectedProperty = AvaloniaProperty.Register<TreemapControl, UsageNode?>(nameof(Selected));

    static TreemapControl()
    {
        AffectsRender<TreemapControl>(RootProperty, SelectedProperty);
    }

    public TreemapControl()
    {
        ClipToBounds = true;
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The folder shown (the scanned one, or one zoomed into).</summary>
    public UsageNode? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public UsageNode? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    /// <summary>The color of a file (by its extension).</summary>
    public Func<UsageNode, Color> ColorOf { get; set; } = _ => Colors.SteelBlue;

    /// <summary>The files of the highlighted type (null: all are bright).</summary>
    public Func<UsageNode, bool>? Matches { get; set; }

    /// <summary>A file or folder was clicked.</summary>
    public event EventHandler<UsageNode>? NodeClicked;

    /// <summary>A file or folder was double-clicked.</summary>
    public event EventHandler<UsageNode>? NodeDoubleClicked;

    /// <summary>The pointer is over another file or folder (null: none).</summary>
    public event EventHandler<UsageNode?>? Hovered;

    /// <summary>The laid out rectangles of the files (and of folders too small to divide; tests).</summary>
    public IReadOnlyList<(UsageNode Node, TreemapRect Rect)> Rectangles
    {
        get
        {
            EnsureLayout();
            return _layout;
        }
    }

    /// <summary>The tree changed (a file was deleted) or the highlight: lays it out and draws it again.</summary>
    public void Invalidate()
    {
        _laidOutRoot = null;
        InvalidateVisual();
    }

    private void EnsureLayout()
    {
        if (ReferenceEquals(_laidOutRoot, Root) && _laidOutFor == Bounds.Size)
        {
            return;
        }
        _laidOutRoot = Root;
        _laidOutFor = Bounds.Size;
        _layout.Clear();
        _folders.Clear();
        _rects.Clear();
        if (Root is { Size: > 0 } root && Bounds.Width > 0 && Bounds.Height > 0)
        {
            Lay(root, new TreemapRect(0, 0, Bounds.Width, Bounds.Height), 0);
        }
    }

    /// <summary>A node in its rectangle: a file as it is, a folder with its name strip (when there is room) around its children.</summary>
    private void Lay(UsageNode node, TreemapRect rect, int depth)
    {
        _rects[node] = rect;
        if (!node.IsDirectory || node.Children.Count == 0 || rect.Width < 2 || rect.Height < 2)
        {
            _layout.Add((node, rect));
            return;
        }
        var titled = depth > 0 && rect.Width > 70 && rect.Height > 44;
        _folders.Add((node, rect, titled));
        var inner = titled ? new TreemapRect(rect.X + 2, rect.Y + Strip, rect.Width - 4, rect.Height - Strip - 2) : rect;
        var children = node.Children.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();
        var total = children.Sum(c => (double)c.Size);
        if (total <= 0)
        {
            return;
        }
        var scale = inner.Area / total;
        var placed = TreemapLayout.Squarify([.. children.Select(c => c.Size * scale)], inner);
        for (var i = 0; i < children.Count; i++)
        {
            if (placed[i].Area >= 0.25)
            {
                Lay(children[i], placed[i], depth + 1);
            }
        }
    }

    private bool Dark => ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>The fill of a file: its color, lighter in the upper left, darker in the lower right.</summary>
    private IBrush Fill(Color color)
    {
        if (!_fills.TryGetValue(color, out var brush))
        {
            brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Views.Tools.UsageTypes.Shade(color, 0.18), 0), new GradientStop(Views.Tools.UsageTypes.Shade(color, -0.12), 1) },
            };
            _fills[color] = brush;
        }
        return brush;
    }

    private static FormattedText Text(string text, Typeface face, double size, IBrush brush, double width) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxTextWidth = Math.Max(1, width),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var dark = Dark;
        context.FillRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x16, 0x16, 0x16) : Color.FromRgb(0xE4, 0xE4, 0xE4)), new Rect(Bounds.Size));
        EnsureLayout();

        // the folders: a shade and their name strip
        var folderFill = new SolidColorBrush(dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(15, 0, 0, 0));
        var title = new SolidColorBrush(dark ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x5C, 0x5C, 0x5C));
        foreach (var (node, rect, titled) in _folders)
        {
            if (!titled)
            {
                continue;
            }
            var r = R(rect);
            context.FillRectangle(folderFill, r.Deflate(0.5));
            context.DrawText(Text(node.Name + "  " + SizeFormatter.Human(node.Size), s_bold, 11, title, r.Width - 10), new Point(r.X + 5, r.Y + 1));
        }

        // the files: faded when another type is highlighted, named where they are large enough
        var label = new SolidColorBrush(Color.FromArgb(242, 255, 255, 255));
        var sub = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255));
        foreach (var (node, rect) in _layout)
        {
            if (node.IsDirectory)
            {
                continue; // a folder too small to divide: background
            }
            var r = R(rect);
            var hit = Matches?.Invoke(node) ?? true;
            var color = ColorOf(node);
            using (context.PushOpacity(hit ? 1 : 0.16))
            {
                if (r.Width >= 3 && r.Height >= 3)
                {
                    context.FillRectangle(Fill(color), r.Deflate(0.5));
                }
                else
                {
                    context.FillRectangle(new SolidColorBrush(color), r);
                }
            }
            if (hit && r.Width > 64 && r.Height > 30)
            {
                using (context.PushClip(r))
                {
                    context.DrawText(Text(node.Name, s_bold, 11.5, label, r.Width - 12), new Point(r.X + 6, r.Y + 4));
                    context.DrawText(Text(SizeFormatter.Human(node.Size), s_normal, 11, sub, r.Width - 12), new Point(r.X + 6, r.Y + 19));
                }
            }
        }

        // the one under the pointer, the selected one
        if (_hover is { } hover && !ReferenceEquals(hover, Selected) && BoundsOf(hover) is { } h)
        {
            context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)), 1.5), h.Deflate(0.75));
        }
        if (Selected is { } selected && BoundsOf(selected) is { } box)
        {
            context.DrawRectangle(new Pen(Brushes.White, 2.5), box.Deflate(1.25));
            if (box.Width > 8 && box.Height > 8)
            {
                context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)), 1), box.Deflate(3));
            }
        }
    }

    private static Rect R(TreemapRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>The area of a node: its own rectangle, or that of the nearest folder above it that has one.</summary>
    public Rect? BoundsOf(UsageNode node)
    {
        EnsureLayout();
        for (var n = node; n is not null; n = n.Parent)
        {
            if (_rects.TryGetValue(n, out var r))
            {
                return R(r);
            }
        }
        return null;
    }

    /// <summary>The file at a point, else the innermost folder there (its name strip, the gaps between files).</summary>
    public UsageNode? NodeAt(Point point)
    {
        EnsureLayout();
        foreach (var (node, rect) in _layout)
        {
            if (rect.Contains(point.X, point.Y))
            {
                return node;
            }
        }
        UsageNode? folder = null;
        foreach (var (node, rect, _) in _folders)
        {
            if (rect.Contains(point.X, point.Y))
            {
                folder = node; // later ones are deeper
            }
        }
        return folder;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (NodeAt(e.GetPosition(this)) is { } node)
        {
            Focus();
            if (e.ClickCount == 2)
            {
                NodeDoubleClicked?.Invoke(this, node);
            }
            else
            {
                NodeClicked?.Invoke(this, node);
            }
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHover(NodeAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    private void SetHover(UsageNode? node)
    {
        if (ReferenceEquals(node, _hover))
        {
            return;
        }
        _hover = node;
        ToolTip.SetTip(this, node is null ? null : Tip(node));
        Hovered?.Invoke(this, node);
        InvalidateVisual();
    }

    /// <summary>The tooltip: the name, its folder, then size, share and type or date.</summary>
    private StackPanel Tip(UsageNode node)
    {
        var panel = new StackPanel { MaxWidth = 360, Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = node.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } });
        if (node.Parent is { } parent)
        {
            panel.Children.Add(new TextBlock { Text = parent.Path.ToDisplayString(), FontSize = 11.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } });
        }
        var total = Root?.Size ?? 0;
        var facts = new List<string> { SizeFormatter.Human(node.Size) };
        if (total > 0)
        {
            facts.Add((node.Size * 100.0 / total).ToString("0.0", CultureInfo.CurrentCulture) + " %");
        }
        if (node.IsDirectory)
        {
            facts.Add(Core.Localization.Localizer.T("{0} files").Replace("{0}", node.Files.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal));
        }
        else
        {
            facts.Add(node.Extension.Length > 0 ? node.Extension : Core.Localization.Localizer.T("no extension"));
            if (node.LastWriteTime != default)
            {
                facts.Add(node.LastWriteTime.ToString("g", CultureInfo.CurrentCulture));
            }
        }
        panel.Children.Add(new TextBlock { Text = string.Join("   ", facts), FontSize = 12, Opacity = 0.85, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Classes = { "noloc" } });
        return panel;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }
}
