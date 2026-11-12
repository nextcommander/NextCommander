using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace NextCommander.App.Controls;

/// <summary>Lister image view: fit to window (shrink only) or zoom, 90° rotation, drag to pan.</summary>
public sealed class ImageViewControl : Control
{
    private Bitmap? _bitmap;
    private int _rotation;
    private double _zoom = 1;
    private bool _fit = true;
    private Vector _pan;
    private Point? _dragStart;
    private Vector _panAtDragStart;

    static ImageViewControl()
    {
        FocusableProperty.OverrideDefaultValue<ImageViewControl>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ImageViewControl>(true);
    }

    public event EventHandler? ViewChanged;

    public Bitmap? Bitmap => _bitmap;

    public int Rotation => _rotation;

    public bool IsFit => _fit;

    /// <summary>Effective zoom factor (in fit mode the computed one).</summary>
    public double Zoom => _fit ? FitZoom() : _zoom;

    public void SetImage(Bitmap? bitmap, int rotation)
    {
        _bitmap = bitmap;
        _rotation = ((rotation % 360) + 360) % 360;
        _fit = true;
        _pan = default;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Rotate(int degrees)
    {
        _rotation = (((_rotation + degrees) % 360) + 360) % 360;
        _pan = default;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleFit()
    {
        _fit = !_fit;
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomBy(double factor)
    {
        _zoom = Math.Clamp(Zoom * factor, 0.02, 32);
        _fit = false;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private Size RotatedSize()
    {
        if (_bitmap is null)
        {
            return default;
        }
        var s = _bitmap.Size;
        return _rotation is 90 or 270 ? new Size(s.Height, s.Width) : s;
    }

    private double FitZoom()
    {
        var size = RotatedSize();
        if (size.Width <= 0 || size.Height <= 0 || Bounds.Width <= 0)
        {
            return 1;
        }
        return Math.Min(1, Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height));
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(this.FindResource("SystemControlBackgroundAltHighBrush") as IBrush ?? Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is null)
        {
            return;
        }
        var zoom = Zoom;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2) + _pan;
        var size = _bitmap.Size * zoom;
        var matrix = Matrix.CreateTranslation(-size.Width / 2, -size.Height / 2)
            * Matrix.CreateRotation(Math.PI * _rotation / 180)
            * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(matrix))
        {
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), new Rect(size));
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.ClickCount == 2)
        {
            ToggleFit();
            return;
        }
        _dragStart = e.GetPosition(this);
        _panAtDragStart = _pan;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is { } start)
        {
            _pan = _panAtDragStart + (e.GetPosition(this) - start);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomBy(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }
}
