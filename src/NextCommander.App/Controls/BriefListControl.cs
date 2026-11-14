using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NextCommander.App.ViewModels;

namespace NextCommander.App.Controls;

/// <summary>
/// The "brief" view: names only, flowing top-to-bottom in several columns, scrolled horizontally.
/// Fully virtualized by drawing only the visible columns, so 100k entries cost the same as 100.
/// </summary>
public sealed class BriefListControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<FileItemViewModel>?> ItemsProperty =
        AvaloniaProperty.Register<BriefListControl, IReadOnlyList<FileItemViewModel>?>(nameof(Items));

    public static readonly StyledProperty<FileItemViewModel?> CursorItemProperty =
        AvaloniaProperty.Register<BriefListControl, FileItemViewModel?>(nameof(CursorItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<BriefListControl, double>(nameof(ColumnWidth), 220);

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<BriefListControl, double>(nameof(RowHeight), 22);

    public static readonly StyledProperty<bool> IsActivePanelProperty =
        AvaloniaProperty.Register<BriefListControl, bool>(nameof(IsActivePanel), true);

    public static readonly StyledProperty<int> FirstColumnProperty =
        AvaloniaProperty.Register<BriefListControl, int>(nameof(FirstColumn), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            coerce: (o, v) => Math.Clamp(v, 0, ((BriefListControl)o).MaxFirstColumn));

    public static readonly DirectProperty<BriefListControl, int> MaxFirstColumnProperty =
        AvaloniaProperty.RegisterDirect<BriefListControl, int>(nameof(MaxFirstColumn), o => o.MaxFirstColumn);

    /// <summary>Panel font (settings).</summary>
    public static Typeface Font { get; set; } = new(FontFamily.Default);

    public static double PanelFontSize { get; set; } = 13;

    /// <summary>Icon size (Configuration > Icons).</summary>
    public static double IconSize { get; set; } = 16;
    private IReadOnlyList<FileItemViewModel>? _subscribed;
    private int _maxFirstColumn;

    static BriefListControl()
    {
        FocusableProperty.OverrideDefaultValue<BriefListControl>(true);
        AffectsRender<BriefListControl>(ItemsProperty, CursorItemProperty, ColumnWidthProperty, RowHeightProperty, IsActivePanelProperty, FirstColumnProperty);
    }

    public BriefListControl()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual(); // view mode background set on the panel
    }

    public IReadOnlyList<FileItemViewModel>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public FileItemViewModel? CursorItem
    {
        get => GetValue(CursorItemProperty);
        set => SetValue(CursorItemProperty, value);
    }

    public double ColumnWidth
    {
        get => GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    public double RowHeight
    {
        get => GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public bool IsActivePanel
    {
        get => GetValue(IsActivePanelProperty);
        set => SetValue(IsActivePanelProperty, value);
    }

    /// <summary>Horizontal scroll position, in columns.</summary>
    public int FirstColumn
    {
        get => GetValue(FirstColumnProperty);
        set => SetValue(FirstColumnProperty, value);
    }

    public int MaxFirstColumn
    {
        get => _maxFirstColumn;
        private set => SetAndRaise(MaxFirstColumnProperty, ref _maxFirstColumn, value);
    }

    public int RowsPerColumn => Math.Max(1, (int)Math.Floor(Bounds.Height / RowHeight));

    private int FullyVisibleColumns => Math.Max(1, (int)Math.Floor(Bounds.Width / ColumnWidth));

    private int ColumnCount => Items is { Count: > 0 } items ? (items.Count + RowsPerColumn - 1) / RowsPerColumn : 0;

    /// <summary>Whether a point is on the icon of an item (Operation > select by clicking the icon).</summary>
    public bool IsOnIcon(Point point)
    {
        var inColumn = point.X - ((int)(point.X / ColumnWidth) * ColumnWidth);
        return inColumn >= 2 && inColumn <= 8 + IconSize && HitTest(point) is not null;
    }

    /// <summary>Item at a point in control coordinates, or null for empty space.</summary>
    public FileItemViewModel? HitTest(Point point)
    {
        if (Items is not { } items || point.X < 0 || point.Y < 0)
        {
            return null;
        }
        var row = (int)(point.Y / RowHeight);
        if (row >= RowsPerColumn)
        {
            return null;
        }
        var column = FirstColumn + (int)(point.X / ColumnWidth);
        var index = (column * RowsPerColumn) + row;
        return index < items.Count ? items[index] : null;
    }

    /// <summary>Where the name of an item is drawn (control coordinates); null when it is not in view.</summary>
    public Rect? NameRect(FileItemViewModel item)
    {
        if (Items is not { } items || IndexOf(items, item) is var index && index < 0)
        {
            return null;
        }
        var column = (index / RowsPerColumn) - FirstColumn;
        if (column < 0 || column >= FullyVisibleColumns + 1)
        {
            return null;
        }
        var x = column * ColumnWidth;
        return new Rect(x + 8 + IconSize, (index % RowsPerColumn) * RowHeight, Math.Max(40, ColumnWidth - 12 - IconSize), RowHeight);
    }

    public void ScrollIntoView(FileItemViewModel? item)
    {
        if (item is null || Items is not { } items)
        {
            return;
        }
        var index = IndexOf(items, item);
        if (index < 0)
        {
            return;
        }
        var column = index / RowsPerColumn;
        if (column < FirstColumn)
        {
            FirstColumn = column;
        }
        else if (column >= FirstColumn + FullyVisibleColumns)
        {
            FirstColumn = column - FullyVisibleColumns + 1;
        }
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            Resubscribe();
            UpdateScrollRange();
            FirstColumn = 0;
            ScrollIntoView(CursorItem);
        }
        else if (change.Property == CursorItemProperty)
        {
            ScrollIntoView(CursorItem);
        }
        else if (change.Property == BoundsProperty || change.Property == ColumnWidthProperty || change.Property == RowHeightProperty)
        {
            UpdateScrollRange();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (delta != 0)
        {
            FirstColumn -= Math.Sign(delta);
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        // list background (view mode background of the panel, Color theme), transparent at least: hit testing for empty space
        context.FillRectangle(Resource("FmListBackgroundBrush", ActualThemeVariant) ?? Brushes.Transparent, new Rect(bounds.Size));
        if (Items is not { Count: > 0 } items)
        {
            return;
        }

        var rows = RowsPerColumn;
        var rowHeight = RowHeight;
        var columnWidth = ColumnWidth;
        var theme = ActualThemeVariant;
        var foreground = Resource("FmListTextBrush", theme) ?? Resource("TextControlForeground", theme) ?? Brushes.Black;
        var marked = Resource("FmMarkedBrush", theme) ?? Brushes.Red;
        var hidden = Resource("FmHiddenBrush", theme) ?? Brushes.Gray;
        var accent = Resource("SystemControlHighlightAccentBrush", theme) ?? Brushes.SteelBlue;
        var cursor = CursorItem;
        var culture = CultureInfo.CurrentCulture;

        for (var c = 0; ; c++)
        {
            var x = c * columnWidth;
            if (x >= bounds.Width)
            {
                break;
            }
            var column = FirstColumn + c;
            for (var r = 0; r < rows; r++)
            {
                var index = (column * rows) + r;
                if (index >= items.Count)
                {
                    return;
                }
                var item = items[index];
                var y = r * rowHeight;
                var cell = new Rect(x, y, columnWidth, rowHeight);

                if (ReferenceEquals(item, cursor))
                {
                    using (context.PushOpacity(IsActivePanel ? 0.45 : 0.2))
                    {
                        context.FillRectangle(accent, cell.Deflate(new Thickness(1, 0)));
                    }
                }

                var iconRect = new Rect(x + 6, y + ((rowHeight - IconSize) / 2), IconSize, IconSize);
                if (item.SystemIcon is { } image)
                {
                    context.DrawImage(image, new Rect(image.Size), iconRect);
                }
                else if (item.Icon is { } geometry)
                {
                    using (context.PushTransform(Matrix.CreateTranslation(iconRect.X, iconRect.Y)))
                    {
                        context.DrawGeometry(item.IconBrush, item.IconStroke is { } stroke ? new Pen(stroke, 1) : null, geometry);
                    }
                }

                var brush = item.IsMarked ? marked : item.IsDimmed ? hidden : item.RuleBrush ?? foreground;
                var text = new FormattedText(item.BriefName, culture, FlowDirection.LeftToRight,
                    item.IsMarked ? new Typeface(Font.FontFamily, FontStyle.Normal, FontWeight.SemiBold) : Font, PanelFontSize, brush)
                {
                    MaxTextWidth = Math.Max(1, columnWidth - 34),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(text, new Point(x + 10 + IconSize, y + ((rowHeight - text.Height) / 2)));
            }
        }
    }

    private IBrush? Resource(string key, Avalonia.Styling.ThemeVariant theme) =>
        this.TryFindResource(key, theme, out var value) ? value as IBrush : null;

    private void UpdateScrollRange()
    {
        MaxFirstColumn = Math.Max(0, ColumnCount - FullyVisibleColumns);
        if (FirstColumn > MaxFirstColumn)
        {
            FirstColumn = MaxFirstColumn;
        }
        InvalidateVisual();
    }

    private void Resubscribe()
    {
        Unsubscribe();
        _subscribed = Items;
        if (_subscribed is null)
        {
            return;
        }
        foreach (var item in _subscribed)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed is null)
        {
            return;
        }
        foreach (var item in _subscribed)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        _subscribed = null;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private static int IndexOf(IReadOnlyList<FileItemViewModel> items, FileItemViewModel item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }
        return -1;
    }
}
