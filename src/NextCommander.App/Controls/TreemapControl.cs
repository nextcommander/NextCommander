using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NextCommander.Core.Formatting;
using NextCommander.Core.Tools;

namespace NextCommander.App.Controls;

/// <summary>
/// The treemap of a disk usage scan (as in WinDirStat): every file is a rectangle sized by its bytes, colored by its
/// extension and shaded like a cushion; a folder is the area of its files. Clicking selects a file, the selected
/// file or folder is outlined.
/// </summary>
public sealed class TreemapControl : Control
{
    private List<(UsageNode Node, TreemapRect Rect)> _layout = [];
    private Size _laidOutFor;
    private UsageNode? _laidOutRoot;
    private UsageNode? _hover;
    private readonly Dictionary<Color, IBrush> _cushions = [];

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
    }

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

    /// <summary>A file was clicked.</summary>
    public event EventHandler<UsageNode>? NodeClicked;

    /// <summary>A file was double-clicked.</summary>
    public event EventHandler<UsageNode>? NodeDoubleClicked;

    /// <summary>The laid out rectangles (tests).</summary>
    public IReadOnlyList<(UsageNode Node, TreemapRect Rect)> Rectangles
    {
        get
        {
            EnsureLayout();
            return _layout;
        }
    }

    /// <summary>The tree changed (a file was deleted): lays it out again.</summary>
    public void Invalidate()
    {
        _laidOutRoot = null;
        _cushions.Clear();
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
        _layout = Root is null ? [] : TreemapLayout.Layout(Root, new TreemapRect(0, 0, Bounds.Width, Bounds.Height), minimumArea: 0.5);
    }

    /// <summary>The cushion of a color: light in the upper left, darker towards the lower right.</summary>
    private IBrush Cushion(Color color)
    {
        if (!_cushions.TryGetValue(color, out var brush))
        {
            static Color Scale(Color c, double f) => Color.FromRgb((byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));
            brush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.35, 0.35, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.35, 0.35, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.9, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Scale(color, 1.35), 0), new GradientStop(color, 0.55), new GradientStop(Scale(color, 0.55), 1) },
            };
            _cushions[color] = brush;
        }
        return brush;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), new Rect(Bounds.Size));
        EnsureLayout();
        foreach (var (node, rect) in _layout)
        {
            if (node.IsDirectory)
            {
                continue; // a folder too small to divide: background
            }
            var r = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            var color = ColorOf(node);
            if (r.Width >= 4 && r.Height >= 4)
            {
                context.FillRectangle(Cushion(color), r.Deflate(0.5));
            }
            else
            {
                context.FillRectangle(new SolidColorBrush(color), r);
            }
        }
        if (Selected is { } selected && BoundsOf(selected) is { } box)
        {
            context.DrawRectangle(new Pen(Brushes.White, 2), box.Deflate(1));
            context.DrawRectangle(new Pen(Brushes.Black, 1), box.Deflate(2.5));
        }
    }

    /// <summary>The area of a node: its own rectangle (a file) or the box around its files (a folder).</summary>
    public Rect? BoundsOf(UsageNode node)
    {
        EnsureLayout();
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var (n, r) in _layout)
        {
            if (IsUnder(n, node))
            {
                left = Math.Min(left, r.X);
                top = Math.Min(top, r.Y);
                right = Math.Max(right, r.X + r.Width);
                bottom = Math.Max(bottom, r.Y + r.Height);
            }
        }
        return left == double.MaxValue ? null : new Rect(left, top, right - left, bottom - top);
    }

    private static bool IsUnder(UsageNode node, UsageNode ancestor)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (ReferenceEquals(n, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The file (or undivided folder) at a point.</summary>
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
        return null;
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
        var node = NodeAt(e.GetPosition(this));
        if (ReferenceEquals(node, _hover))
        {
            return;
        }
        _hover = node;
        ToolTip.SetTip(this, node is null ? null : node.Path.ToDisplayString() + "\n" + SizeFormatter.Human(node.Size) + " (" + SizeFormatter.Bytes(node.Size) + ")");
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }
}
