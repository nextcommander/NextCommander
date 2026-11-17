using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NextCommander.Core.Documents;

namespace NextCommander.App.Views.Lister;

/// <summary>
/// The pages of a PDF one below the other, as wide as the view (Ctrl+wheel or Ctrl +/- zooms, Ctrl+0 fits again). Only
/// the pages in and near the view are drawn (in the background, by PDFium); far ones give their pictures back. Text is
/// found (the Lister's find bar: the match is marked and scrolled to) and selected with the mouse within a page
/// (Ctrl+A: the page's text, Ctrl+C copies).
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

    private static readonly IBrush s_matchBrush = new SolidColorBrush(Color.FromArgb(110, 255, 170, 0));
    private static readonly IBrush s_selectionBrush = new SolidColorBrush(Color.FromArgb(90, 0, 120, 215));

    /// <summary>The match of the last search (page, first character, length), or none.</summary>
    private (int Page, int Start, int Length)? _match;

    /// <summary>The selected characters (page, first, length), or none; and where a mouse selection started.</summary>
    private (int Page, int Start, int Length)? _selection;
    private (int Page, int Character)? _anchor;

    private sealed class Page(Border frame, Image image, Canvas marks, double width, double height)
    {
        public Border Frame { get; } = frame;

        public Image Image { get; } = image;

        /// <summary>The marks over the page: the search match and the selection.</summary>
        public Canvas Marks { get; } = marks;

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

    /// <summary>The page being read (1-based): the one at a third of the view's height (where a found text is put).</summary>
    public int CurrentPage
    {
        get
        {
            var offset = _scroll.Offset.Y + (_scroll.Offset.Y > 0 ? _scroll.Viewport.Height / 3 : 0);
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
            var marks = new Canvas { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Ibeam) };
            var layers = new Grid();
            layers.Children.AddRange([image, marks]);
            var frame = new Border
            {
                Child = layers,
                Background = Brushes.White,
                BoxShadow = BoxShadows.Parse("0 1 4 0 #40000000"),
            };
            var index = i;
            marks.PointerPressed += (_, e) => Press(index, marks, e);
            marks.PointerMoved += (_, e) => Drag(index, marks, e);
            marks.PointerReleased += (_, _) => _anchor = null;
            _pages.Add(new Page(frame, image, marks, Math.Max(1, width), Math.Max(1, height)));
            _stack.Children.Add(frame);
        }
        _scroll.Offset = default;
        _match = null;
        _selection = null;
        Arrange();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- find and select

    /// <summary>The selected text, or empty.</summary>
    public string SelectedText =>
        _document is { } document && _selection is { } s ? document.PageText(s.Page).Substring(s.Start, Math.Min(s.Length, document.PageText(s.Page).Length - s.Start)) : string.Empty;

    /// <summary>The match of the last search: its page (1-based) and text; null when there is none.</summary>
    public (int Page, string Text)? Match =>
        _document is { } document && _match is { } m ? (m.Page + 1, document.PageText(m.Page).Substring(m.Start, m.Length)) : null;

    /// <summary>
    /// The next (or previous) match of a search, from the last match or the page in view; the match is marked and
    /// scrolled into view. False when there is none (the last one stays marked).
    /// </summary>
    public async Task<bool> FindAsync(Core.Search.TextMatcher matcher, bool forward, CancellationToken cancellationToken = default)
    {
        if (_document is not { } document || _pages.Count == 0)
        {
            return false;
        }
        var (page, from) = _match is { } last
            ? (last.Page, forward ? last.Start + Math.Max(1, last.Length) : last.Start)
            : (CurrentPage - 1, forward ? 0 : int.MaxValue);
        for (var step = 0; step <= _pages.Count; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = page;
            var text = await Task.Run(() => document.PageText(index), cancellationToken);
            var start = Math.Clamp(from, 0, text.Length);
            var (at, length) = forward ? matcher.Find(text, start) : matcher.FindLast(text, start);
            if (at >= 0 && length > 0)
            {
                _match = (page, at, length);
                Mark();
                ScrollTo(page, at, length);
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            page = forward ? page + 1 : page - 1;
            if (page < 0 || page >= _pages.Count)
            {
                return false; // the end of the document
            }
            from = forward ? 0 : int.MaxValue;
        }
        return false;
    }

    /// <summary>Selects characters of a page (Ctrl+A: all of it).</summary>
    public void Select(int page, int start, int length)
    {
        _selection = length > 0 ? (page, start, length) : null;
        Mark();
    }

    private void Press(int index, Control marks, PointerPressedEventArgs e)
    {
        if (_document is not { } document || !e.GetCurrentPoint(marks).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Focus();
        var character = document.CharacterAt(index, ToPoints(index, e.GetPosition(marks)).X, ToPoints(index, e.GetPosition(marks)).Y);
        _anchor = (index, character);
        Select(index, 0, 0);
        e.Handled = true;
    }

    private void Drag(int index, Control marks, PointerEventArgs e)
    {
        if (_anchor is not { } anchor || anchor.Page != index || _document is not { } document || !e.GetCurrentPoint(marks).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var point = ToPoints(index, e.GetPosition(marks));
        var character = document.CharacterAt(index, point.X, point.Y);
        if (anchor.Character < 0)
        {
            _anchor = (index, character); // started beside the text: from the first character reached
            return;
        }
        if (character >= 0)
        {
            var (first, end) = (Math.Min(anchor.Character, character), Math.Max(anchor.Character, character));
            Select(index, first, end - first + 1);
        }
    }

    /// <summary>A point of a page's view in page points.</summary>
    private Point ToPoints(int index, Point view)
    {
        var page = _pages[index];
        var scale = page.Width / Math.Max(1, page.Frame.Width);
        return new Point(view.X * scale, view.Y * scale);
    }

    /// <summary>Draws the match and the selection over their pages.</summary>
    private void Mark()
    {
        foreach (var page in _pages)
        {
            page.Marks.Children.Clear();
        }
        if (_document is not { } document)
        {
            return;
        }
        void Add((int Page, int Start, int Length)? range, IBrush brush)
        {
            if (range is not { } r || r.Page >= _pages.Count)
            {
                return;
            }
            var page = _pages[r.Page];
            var scale = page.Frame.Width / page.Width;
            foreach (var (left, top, right, bottom) in document.TextRectangles(r.Page, r.Start, r.Length))
            {
                var mark = new Rectangle { Fill = brush, Width = Math.Max(2, (right - left) * scale), Height = Math.Max(2, (bottom - top) * scale), IsHitTestVisible = false };
                Canvas.SetLeft(mark, left * scale);
                Canvas.SetTop(mark, top * scale);
                page.Marks.Children.Add(mark);
            }
        }
        Add(_match, s_matchBrush);
        Add(_selection, s_selectionBrush);
    }

    /// <summary>Scrolls so that characters of a page are in view (a third from the top).</summary>
    private void ScrollTo(int index, int start, int length)
    {
        if (_document is not { } document)
        {
            return;
        }
        var y = Gap;
        for (var i = 0; i < index; i++)
        {
            y += _pages[i].Frame.Height + Gap;
        }
        var page = _pages[index];
        var top = document.TextRectangles(index, start, length) is [var first, ..] ? first.Top * page.Frame.Width / page.Width : 0;
        _scroll.Offset = new Vector(_scroll.Offset.X, Math.Max(0, y + top - _scroll.Viewport.Height / 3));
    }

    private async Task CopyAsync()
    {
        if (SelectedText is { Length: > 0 } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
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
                case Key.C or Key.Insert when _selection is not null:
                    _ = CopyAsync();
                    e.Handled = true;
                    return;
                case Key.A when _document is { } document && _pages.Count > 0:
                    var page = _selection?.Page ?? CurrentPage - 1;
                    Select(page, 0, document.PageText(page).Length);
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
        Mark(); // the marks follow the pages' new size
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
        _match = null;
        _selection = null;
        _anchor = null;
        _document?.Dispose();
        _document = null;
    }

    public void Dispose() => Clear();
}
