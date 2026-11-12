using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;

namespace NextCommander.App.Controls;

/// <summary>
/// Thumbnail view: a grid of tiles (image preview or large icon + name). Custom drawn and virtualized:
/// only visible rows are drawn and only their thumbnails are requested.
/// </summary>
public sealed class ThumbnailListControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<FileItemViewModel>?> ItemsProperty =
        AvaloniaProperty.Register<ThumbnailListControl, IReadOnlyList<FileItemViewModel>?>(nameof(Items));

    public static readonly StyledProperty<FileItemViewModel?> CursorItemProperty =
        AvaloniaProperty.Register<ThumbnailListControl, FileItemViewModel?>(nameof(CursorItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> TileSizeProperty =
        AvaloniaProperty.Register<ThumbnailListControl, int>(nameof(TileSize), 112);

    public static readonly StyledProperty<bool> IsActivePanelProperty =
        AvaloniaProperty.Register<ThumbnailListControl, bool>(nameof(IsActivePanel), true);

    public static readonly StyledProperty<int> FirstRowProperty =
        AvaloniaProperty.Register<ThumbnailListControl, int>(nameof(FirstRow), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            coerce: (o, v) => Math.Clamp(v, 0, ((ThumbnailListControl)o).MaxFirstRow));

    public static readonly DirectProperty<ThumbnailListControl, int> MaxFirstRowProperty =
        AvaloniaProperty.RegisterDirect<ThumbnailListControl, int>(nameof(MaxFirstRow), o => o.MaxFirstRow);

    private const double TextHeight = 20;

    /// <summary>A second line under the name for the fields of Configuration > Thumbnails (set by the application).</summary>
    public static bool ShowCaptions { get; set; }

    private const double CaptionHeight = 15;
    private const double Padding = 8;
    private static readonly Typeface s_typeface = new(FontFamily.Default);
    private IReadOnlyList<FileItemViewModel>? _subscribed;
    private int _maxFirstRow;

    static ThumbnailListControl()
    {
        FocusableProperty.OverrideDefaultValue<ThumbnailListControl>(true);
        AffectsRender<ThumbnailListControl>(ItemsProperty, CursorItemProperty, TileSizeProperty, IsActivePanelProperty, FirstRowProperty);
    }

    public ThumbnailListControl()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual(); // view mode background set on the panel
    }

    public ThumbnailService? Service { get; set; }

    /// <summary>Ctrl+wheel: the owner changes <see cref="TileSize"/> (and persists it).</summary>
    public event EventHandler<int>? ZoomRequested;

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

    public int TileSize
    {
        get => GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    public bool IsActivePanel
    {
        get => GetValue(IsActivePanelProperty);
        set => SetValue(IsActivePanelProperty, value);
    }

    public int FirstRow
    {
        get => GetValue(FirstRowProperty);
        set => SetValue(FirstRowProperty, value);
    }

    public int MaxFirstRow
    {
        get => _maxFirstRow;
        private set => SetAndRaise(MaxFirstRowProperty, ref _maxFirstRow, value);
    }

    private double CellWidth => TileSize + (2 * Padding);

    private double CellHeight => TileSize + TextHeight + (ShowCaptions ? CaptionHeight : 0) + (2 * Padding);

    public int Columns => Math.Max(1, (int)Math.Floor(Bounds.Width / CellWidth));

    public int VisibleRows => Math.Max(1, (int)Math.Floor(Bounds.Height / CellHeight));

    private int RowCount => Items is { Count: > 0 } items ? (items.Count + Columns - 1) / Columns : 0;

    public FileItemViewModel? HitTest(Point point)
    {
        if (Items is not { } items || point.X < 0 || point.Y < 0)
        {
            return null;
        }
        var column = (int)(point.X / CellWidth);
        if (column >= Columns)
        {
            return null;
        }
        var row = FirstRow + (int)(point.Y / CellHeight);
        var index = (row * Columns) + column;
        return index < items.Count ? items[index] : null;
    }

    public void ScrollIntoView(FileItemViewModel? item)
    {
        if (item is null || Items is not { } items)
        {
            return;
        }
        var index = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            return;
        }
        var row = index / Columns;
        if (row < FirstRow)
        {
            FirstRow = row;
        }
        else if (row >= FirstRow + VisibleRows)
        {
            FirstRow = row - VisibleRows + 1;
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
            FirstRow = 0;
            ScrollIntoView(CursorItem);
        }
        else if (change.Property == CursorItemProperty)
        {
            ScrollIntoView(CursorItem);
        }
        else if (change.Property == BoundsProperty || change.Property == TileSizeProperty)
        {
            UpdateScrollRange();
            ScrollIntoView(CursorItem);
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
        if (e.Delta.Y == 0)
        {
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomRequested?.Invoke(this, Math.Sign(e.Delta.Y));
        }
        else
        {
            FirstRow -= Math.Sign(e.Delta.Y);
        }
        e.Handled = true;
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
        var theme = ActualThemeVariant;
        var foreground = Resource("FmListTextBrush", theme) ?? Resource("TextControlForeground", theme) ?? Brushes.Black;
        var marked = Resource("FmMarkedBrush", theme) ?? Brushes.Red;
        var hidden = Resource("FmHiddenBrush", theme) ?? Brushes.Gray;
        var accent = Resource("SystemControlHighlightAccentBrush", theme) ?? Brushes.SteelBlue;
        var tileBackground = Resource("FmPanelHeaderBrush", theme) ?? Brushes.LightGray;
        var markedPen = new Pen(marked, 2);
        var culture = CultureInfo.CurrentCulture;
        var columns = Columns;
        var tile = TileSize;

        for (var r = 0; ; r++)
        {
            var y = r * CellHeight;
            if (y >= bounds.Height)
            {
                break;
            }
            for (var c = 0; c < columns; c++)
            {
                var index = ((FirstRow + r) * columns) + c;
                if (index >= items.Count)
                {
                    return;
                }
                var item = items[index];
                var x = c * CellWidth;
                var cell = new Rect(x, y, CellWidth, CellHeight).Deflate(2);
                if (ReferenceEquals(item, CursorItem))
                {
                    using (context.PushOpacity(IsActivePanel ? 0.45 : 0.2))
                    {
                        context.FillRectangle(accent, cell, 4);
                    }
                }
                var box = new Rect(x + Padding, y + Padding, tile, tile);
                DrawPreview(context, item, box, tileBackground);
                if (item.IsMarked)
                {
                    context.DrawRectangle(null, markedPen, box.Inflate(2), 3, 3);
                }

                var brush = item.IsMarked ? marked : item.IsDimmed ? hidden : item.RuleBrush ?? foreground;
                var text = new FormattedText(item.FullName, culture, FlowDirection.LeftToRight, s_typeface, 12, brush)
                {
                    MaxTextWidth = CellWidth - 6,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                };
                context.DrawText(text, new Point(x + 3, y + Padding + tile + 2));
                if (ShowCaptions && !string.IsNullOrEmpty(item.ThumbnailCaption))
                {
                    var caption = new FormattedText(item.ThumbnailCaption, culture, FlowDirection.LeftToRight, s_typeface, 10.5, brush)
                    {
                        MaxTextWidth = CellWidth - 6,
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = TextAlignment.Center,
                    };
                    using (context.PushOpacity(0.7))
                    {
                        context.DrawText(caption, new Point(x + 3, y + Padding + tile + TextHeight));
                    }
                }
            }
        }
    }

    private void DrawPreview(DrawingContext context, FileItemViewModel item, Rect box, IBrush tileBackground)
    {
        if (item.Entry is { } entry && Service?.Get(entry, TileSize, InvalidateVisual) is { } thumb)
        {
            var size = thumb.Bitmap.Size;
            var rotated = thumb.RotationDegrees is 90 or 270;
            var w = rotated ? size.Height : size.Width;
            var h = rotated ? size.Width : size.Height;
            var scale = Math.Min(box.Width / w, box.Height / h);
            var dw = w * scale;
            var dh = h * scale;
            var center = box.Center;
            var matrix = Matrix.CreateTranslation(-size.Width / 2, -size.Height / 2)
                * Matrix.CreateRotation(Math.PI * thumb.RotationDegrees / 180)
                * Matrix.CreateScale(scale, scale)
                * Matrix.CreateTranslation(center.X, center.Y);
            using (context.PushClip(new Rect(center.X - (dw / 2), center.Y - (dh / 2), dw, dh)))
            using (context.PushTransform(matrix))
            {
                context.DrawImage(thumb.Bitmap, new Rect(size));
            }
            return;
        }

        context.FillRectangle(tileBackground, box, 6);
        var iconSize = Math.Min(48, box.Width * 0.5);
        var iconRect = new Rect(box.Center.X - (iconSize / 2), box.Center.Y - (iconSize / 2), iconSize, iconSize);
        if (item.SystemIcon is { } image)
        {
            context.DrawImage(image, new Rect(image.Size), iconRect);
        }
        else if (item.Icon is { } geometry)
        {
            var s = iconSize / 16;
            using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(iconRect.X, iconRect.Y)))
            {
                context.DrawGeometry(item.IconBrush, item.IconStroke is { } stroke ? new Pen(stroke, 1 / s) : null, geometry);
            }
        }
    }

    private IBrush? Resource(string key, Avalonia.Styling.ThemeVariant theme) =>
        this.TryFindResource(key, theme, out var value) ? value as IBrush : null;

    private void UpdateScrollRange()
    {
        MaxFirstRow = Math.Max(0, RowCount - VisibleRows);
        if (FirstRow > MaxFirstRow)
        {
            FirstRow = MaxFirstRow;
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
}
