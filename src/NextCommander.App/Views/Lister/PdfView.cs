using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NextCommander.Core.Documents;

namespace NextCommander.App.Views.Lister;

/// <summary>
/// The pages of a PDF one below the other, as wide as the view (Ctrl+wheel or Ctrl +/- zooms, Ctrl+0 fits again). Only
/// the pages in and near the view are drawn (in the background, by PDFium); far ones give their pictures back.
/// </summary>
public sealed class PdfView : UserControl, IDisposable
{
    private const double Gap = 12;

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _stack;
    private readonly List<Page> _pages = [];
    private PdfDocument? _document;
    private double _zoom = 1;
    private int _generation;

    private sealed class Page(Border frame, Image image, double width, double height)
    {
        public Border Frame { get; } = frame;

        public Image Image { get; } = image;

        /// <summary>The page's size in points.</summary>
        public double Width { get; } = width;

        public double Height { get; } = height;

        /// <summary>The pixel width it was drawn at (0: not drawn), and the width being drawn now.</summary>
        public int Drawn { get; set; }

        public int Drawing { get; set; }
    }

    public PdfView()
    {
        _stack = new StackPanel { Spacing = Gap, Margin = new Thickness(Gap), HorizontalAlignment = HorizontalAlignment.Center };
        _scroll = new ScrollViewer { Content = _stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _scroll.ScrollChanged += (_, _) =>
        {
            DrawVisible();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        };
        Content = _scroll;
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                Zoom(e.Delta.Y > 0 ? _zoom * 1.15 : _zoom / 1.15);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Focusable = true;
    }

    /// <summary>The page in view or the zoom changed (the Lister's status line).</summary>
    public event EventHandler? StatusChanged;

    public int PageCount => _pages.Count;

    /// <summary>The page at the top of the view (1-based).</summary>
    public int CurrentPage
    {
        get
        {
            var offset = _scroll.Offset.Y;
            for (var i = 0; i < _pages.Count; i++)
            {
                if (_pages[i].Frame.Bounds.Bottom + Gap > offset)
                {
                    return i + 1;
                }
            }
            return _pages.Count;
        }
    }

    public double ZoomFactor => _zoom;

    /// <summary>How many pages have a picture now (tests).</summary>
    public int DrawnPages => _pages.Count(p => p.Drawn > 0);

    /// <summary>Shows a document (the view owns it from now on).</summary>
    public void Show(PdfDocument document)
    {
        Clear();
        _document = document;
        _zoom = 1;
        for (var i = 0; i < document.PageCount; i++)
        {
            var (width, height) = document.PageSize(i);
            var image = new Image { Stretch = Stretch.Fill };
            var frame = new Border
            {
                Child = image,
                Background = Brushes.White,
                BoxShadow = BoxShadows.Parse("0 1 4 0 #40000000"),
            };
            _pages.Add(new Page(frame, image, Math.Max(1, width), Math.Max(1, height)));
            _stack.Children.Add(frame);
        }
        _scroll.Offset = default;
        Arrange();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Zoom(double factor)
    {
        _zoom = Math.Clamp(factor, 0.25, 6);
        Arrange();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    Zoom(_zoom * 1.15);
                    e.Handled = true;
                    return;
                case Key.OemMinus or Key.Subtract:
                    Zoom(_zoom / 1.15);
                    e.Handled = true;
                    return;
                case Key.D0 or Key.NumPad0:
                    Zoom(1);
                    e.Handled = true;
                    return;
            }
        }
        base.OnKeyDown(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
        {
            Arrange();
        }
    }

    /// <summary>The pages' sizes for the view's width and the zoom; pictures of another size are drawn again.</summary>
    private void Arrange()
    {
        var available = Math.Max(120, (Bounds.Width > 0 ? Bounds.Width : 800) - 2 * Gap - 18);
        foreach (var page in _pages)
        {
            var width = available * _zoom;
            page.Frame.Width = width;
            page.Frame.Height = width * page.Height / page.Width;
        }
        Dispatcher.UIThread.Post(DrawVisible, DispatcherPriority.Background); // after the layout
    }

    /// <summary>Draws the pages in and near the view; pictures of pages far away are given back.</summary>
    private void DrawVisible()
    {
        if (_document is not { } document || _pages.Count == 0)
        {
            return;
        }
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var top = _scroll.Offset.Y;
        var height = Math.Max(_scroll.Viewport.Height, 200);
        var y = Gap;
        var generation = _generation;
        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var pageHeight = page.Frame.Height;
            var near = y + pageHeight >= top - height && y <= top + 2 * height;
            var far = y + pageHeight < top - 4 * height || y > top + 6 * height;
            var pixels = (int)Math.Round(page.Frame.Width * scale);
            if (near && page.Drawn != pixels && page.Drawing != pixels)
            {
                page.Drawing = pixels;
                var index = i;
                var pixelHeight = (int)Math.Round(pixels * page.Height / page.Width);
                _ = Task.Run(() =>
                {
                    try
                    {
                        return document.Render(index, pixels, pixelHeight);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        return null;
                    }
                }).ContinueWith(t =>
                {
                    if (generation != _generation || page.Drawing != pixels || t.Result is not { } bgra)
                    {
                        return;
                    }
                    var old = page.Image.Source as IDisposable;
                    page.Image.Source = ToBitmap(bgra, pixels, pixelHeight, scale);
                    old?.Dispose();
                    page.Drawn = pixels;
                    page.Drawing = 0;
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            else if (far && page.Drawn > 0)
            {
                (page.Image.Source as IDisposable)?.Dispose();
                page.Image.Source = null;
                page.Drawn = 0;
            }
            y += pageHeight + Gap;
        }
    }

    private static WriteableBitmap ToBitmap(byte[] bgra, int width, int height, double scale)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var target = bitmap.Lock();
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(bgra, row * width * 4, target.Address + row * target.RowBytes, width * 4);
        }
        return bitmap;
    }

    private void Clear()
    {
        _generation++;
        foreach (var page in _pages)
        {
            (page.Image.Source as IDisposable)?.Dispose();
        }
        _pages.Clear();
        _stack.Children.Clear();
        _document?.Dispose();
        _document = null;
    }

    public void Dispose() => Clear();
}
