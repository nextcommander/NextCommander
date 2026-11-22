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
/// found (the Lister's find bar: the match is marked and scrolled to) and selected with the mouse, also across pages
/// (dragging past the view's edge scrolls; Shift+click extends the selection; Ctrl+A: the page's text, Ctrl+C copies).
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

    /// <summary>The selected characters: from the first to the last (both included, in reading order), or none.</summary>
    private (TextPosition From, TextPosition To)? _selection;

    /// <summary>Where a mouse selection started (kept for Shift+click), and whether the button is down.</summary>
    private TextPosition? _anchor;
    private bool _dragging;

    /// <summary>A character of the document: its page and its index in the page's text.</summary>
    private readonly record struct TextPosition(int Page, int Character) : IComparable<TextPosition>
    {
        public int CompareTo(TextPosition other) => Page != other.Page ? Page.CompareTo(other.Page) : Character.CompareTo(other.Character);
    }

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

    /// <summary>
    /// At zoom 1 a whole page fits the view (its width and height; the user's report, 22 November 2026: a tall page was
    /// cut at the bottom), else the page width fills the view. F switches between them.
    /// </summary>
    public bool FitsWholePage { get; private set; } = true;

    /// <summary>The size a page is shown at (tests).</summary>
    public Size DisplayedPageSize(int index) => new(_pages[index].Frame.Width, _pages[index].Frame.Height);

    /// <summary>F: whole page ↔ page width, at zoom 1.</summary>
    public void ToggleFit()
    {
        FitsWholePage = !FitsWholePage;
        _zoom = 1;
        Arrange();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

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
            marks.PointerPressed += (_, e) => Press(e);
            marks.PointerMoved += (_, e) => Drag(e);
            marks.PointerReleased += (_, _) => _dragging = false;
            _pages.Add(new Page(frame, image, marks, Math.Max(1, width), Math.Max(1, height)));
            _stack.Children.Add(frame);
        }
        _scroll.Offset = default;
        _match = null;
        _selection = null;
        _anchor = null;
        Arrange();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- find and select

    /// <summary>The selected text, or empty; the pages' parts on lines of their own.</summary>
    public string SelectedText
    {
        get
        {
            if (_document is not { } document || _selection is not { } s)
            {
                return string.Empty;
            }
            var parts = new List<string>();
            for (var page = s.From.Page; page <= s.To.Page; page++)
            {
                var (start, length) = RangeOn(page, s);
                var text = document.PageText(page);
                start = Math.Clamp(start, 0, text.Length);
                parts.Add(text.Substring(start, Math.Clamp(length, 0, text.Length - start)));
            }
            return string.Join(Environment.NewLine, parts);
        }
    }

    /// <summary>The middle of a character in the view's coordinates, or null when the page has no such character.</summary>
    public Point? PointOf(int page, int character)
    {
        if (_document is not { } document || page < 0 || page >= _pages.Count || document.TextRectangles(page, character, 1) is not [var box, ..])
        {
            return null;
        }
        var frame = _pages[page].Frame;
        var scale = frame.Width / _pages[page].Width;
        return frame.TranslatePoint(new Point((box.Left + box.Right) / 2 * scale, (box.Top + box.Bottom) / 2 * scale), this);
    }

    /// <summary>The selected pages (1-based, first and last), or null.</summary>
    public (int First, int Last)? SelectedPages => _selection is { } s ? (s.From.Page + 1, s.To.Page + 1) : null;

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
        if (length > 0)
        {
            Select(new TextPosition(page, start), new TextPosition(page, start + length - 1));
        }
        else
        {
            _selection = null;
            Mark();
        }
    }

    /// <summary>Selects from a character of one page to a character of another (both included).</summary>
    public void Select(int fromPage, int fromCharacter, int toPage, int toCharacter) =>
        Select(new TextPosition(fromPage, fromCharacter), new TextPosition(toPage, toCharacter));

    private void Select(TextPosition a, TextPosition b)
    {
        _selection = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        Mark();
    }

    /// <summary>The characters of a page within a selection: first and count.</summary>
    private (int Start, int Length) RangeOn(int page, (TextPosition From, TextPosition To) selection)
    {
        var start = page == selection.From.Page ? selection.From.Character : 0;
        var end = page == selection.To.Page ? selection.To.Character + 1 : _document!.PageText(page).Length;
        return (start, Math.Max(0, end - start));
    }

    private void Press(PointerPressedEventArgs e)
    {
        if (_document is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Focus();
        var at = CharacterAt(e.GetPosition(_stack), near: false);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _anchor is { Character: >= 0 } anchor && at is { Character: >= 0 } extended)
        {
            Select(anchor, extended); // Shift+click: from where the selection started to here
        }
        else
        {
            _anchor = at;
            _selection = null;
            Mark();
        }
        _dragging = true;
        e.Handled = true;
    }

    /// <summary>
    /// A drag selects from the anchor to the character under the mouse, on any page (the pressed page keeps the
    /// pointer, so the position is taken over the whole stack); past the view's top or bottom it scrolls.
    /// </summary>
    private void Drag(PointerEventArgs e)
    {
        if (!_dragging || _document is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var inView = e.GetPosition(_scroll);
        var scroll = inView.Y < 0 ? inView.Y : inView.Y > _scroll.Viewport.Height ? inView.Y - _scroll.Viewport.Height : 0;
        if (scroll != 0)
        {
            _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(_scroll.Offset.Y + scroll, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
        }
        if (CharacterAt(e.GetPosition(_stack), near: true) is not { Character: >= 0 } at)
        {
            return;
        }
        if (_anchor is not { Character: >= 0 } anchor)
        {
            _anchor = at; // started beside the text: from the first character reached
            return;
        }
        Select(anchor, at);
    }

    /// <summary>
    /// The character at a point of the page stack; <paramref name="near"/> (a drag) also takes one further away, so a
    /// selection reaches the end of a line or page when the mouse is in the margin or between pages.
    /// </summary>
    private TextPosition? CharacterAt(Point inStack, bool near)
    {
        if (_document is not { } document || _pages.Count == 0)
        {
            return null;
        }
        var index = _pages.FindIndex(p => p.Frame.Bounds.Bottom + Gap / 2 > inStack.Y);
        index = index < 0 ? _pages.Count - 1 : index;
        var bounds = _pages[index].Frame.Bounds;
        var point = ToPoints(index, new Point(Math.Clamp(inStack.X - bounds.X, 0, bounds.Width), Math.Clamp(inStack.Y - bounds.Y, 0, bounds.Height)));
        var character = document.CharacterAt(index, point.X, point.Y);
        if (character < 0 && near)
        {
            character = document.CharacterAt(index, point.X, point.Y, 40);
        }
        return new TextPosition(index, character);
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
        if (_selection is { } selection)
        {
            for (var page = selection.From.Page; page <= selection.To.Page && page < _pages.Count; page++)
            {
                var (start, length) = RangeOn(page, selection);
                Add((page, start, length), s_selectionBrush);
            }
        }
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
                    var page = _selection?.To.Page ?? CurrentPage - 1;
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
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5
            || FitsWholePage && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5)
        {
            Arrange();
        }
    }

    /// <summary>The pages' sizes for the view (its width, and its height for a whole page) and the zoom; pictures of another size are drawn again.</summary>
    private void Arrange()
    {
        var available = Math.Max(120, (Bounds.Width > 0 ? Bounds.Width : 800) - 2 * Gap - 18);
        var tall = Math.Max(120, (Bounds.Height > 0 ? Bounds.Height : 600) - 2 * Gap - 18);
        foreach (var page in _pages)
        {
            var fit = FitsWholePage ? Math.Min(available, tall * page.Width / page.Height) : available;
            var width = fit * _zoom;
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
                    page.Image.Source = ToBitmap(bgra, pixels, pixelHeight);
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

    /// <summary>
    /// The picture of a page. Always at 96 dpi: the image fills its frame (Stretch.Fill), and a bitmap of another dpi was
    /// drawn from its top-left part only (the source rectangle taken in device-independent units as pixels), so with a
    /// screen scale of 150 % the page was cut at the right and the bottom (the user's report, 22 November 2026). The
    /// pixels are the frame's size times the scale, so the page stays sharp.
    /// </summary>
    internal static WriteableBitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
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
        _dragging = false;
        _document?.Dispose();
        _document = null;
    }

    public void Dispose() => Clear();
}
