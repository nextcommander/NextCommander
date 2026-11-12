using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NextCommander.Core.Text;

namespace NextCommander.App.Controls;

public enum ByteViewMode
{
    /// <summary>Lines split at line breaks (streaming text view for files of any size).</summary>
    Text,
    /// <summary>Fixed width rows, line breaks ignored (binary view).</summary>
    Binary,
    Hex,
}

/// <summary>
/// Virtualized byte view for the Lister: text lines, fixed-width binary or hex rows. Only the visible rows are
/// read from the <see cref="IRandomAccessSource"/>; the scroll position is a byte offset.
/// </summary>
public sealed class ByteViewControl : Control
{
    public const int HexBytesPerRow = 16;
    public const int BinaryBytesPerRow = 80;

    private IRandomAccessSource? _source;
    private LineNavigator? _navigator;
    private Encoding _encoding = Encoding.UTF8;
    private int _dataStart;
    private ByteViewMode _mode;
    private long _position;
    private int _horizontalOffset;
    private double _fontSize = 13;
    private Typeface _typeface = new("Consolas");
    private double _lineHeight = 17;
    private double _charWidth = 7.5;
    private IReadOnlyList<TextLine> _rows = [];
    private long _selectionAnchor = -1;
    private long _selectionStart = -1;
    private long _selectionEnd = -1;
    private ByteHitRange? _highlight;

    static ByteViewControl()
    {
        FocusableProperty.OverrideDefaultValue<ByteViewControl>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ByteViewControl>(true);
    }

    /// <summary>Raised when the top position changes (scroll bar sync).</summary>
    public event EventHandler? PositionChanged;

    public long Position => _position;

    public long Length => _source?.Length ?? 0;

    public ByteViewMode Mode => _mode;

    public Encoding Encoding => _encoding;

    public int VisibleRows => Math.Max(1, (int)(Bounds.Height / _lineHeight));

    public (long Start, long End)? Selection => _selectionStart >= 0 && _selectionEnd > _selectionStart ? (_selectionStart, _selectionEnd) : null;

    /// <summary>Text of the rows currently on screen (tests, diagnostics).</summary>
    public IReadOnlyList<string> VisibleText => _rows.Select(r => r.Text).ToList();

    public void SetSource(IRandomAccessSource? source, Encoding encoding, int dataStart, ByteViewMode mode, long position = 0)
    {
        if (source is not null && ReferenceEquals(source, _source))
        {
            // Same file in another mode / encoding: keep the selection and the place.
            var keep = Selection?.Start ?? _position;
            _encoding = encoding;
            _dataStart = dataStart;
            _mode = mode;
            _navigator = new LineNavigator(source, encoding, dataStart);
            _horizontalOffset = 0;
            _position = Align(position > 0 ? position : keep);
            Refresh();
            return;
        }
        _source = source;
        _encoding = encoding;
        _dataStart = dataStart;
        _mode = mode;
        _navigator = source is null ? null : new LineNavigator(source, encoding, dataStart);
        _highlight = null;
        _selectionStart = _selectionEnd = _selectionAnchor = -1;
        _horizontalOffset = 0;
        _position = Align(position);
        Refresh();
    }

    public void SetFont(string family, double size)
    {
        _typeface = new Typeface(new FontFamily(family));
        _fontSize = Math.Clamp(size, 6, 72);
        var probe = Format("XXXXXXXXXX", Brushes.Black);
        _charWidth = probe.WidthIncludingTrailingWhitespace / 10;
        _lineHeight = Math.Ceiling(probe.Height + 1);
        Refresh();
    }

    public double FontSize => _fontSize;

    /// <summary>Re-reads the visible rows (after a reload or resize).</summary>
    public void Refresh()
    {
        _rows = ReadRows(_position, VisibleRows + 1);
        InvalidateVisual();
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scrolls to the row containing <paramref name="offset"/> (scroll bar, search hits).</summary>
    public void ScrollTo(long offset)
    {
        _position = Align(Math.Clamp(offset, 0, Math.Max(0, Length - 1)));
        Refresh();
    }

    /// <summary>Makes <paramref name="offset"/> visible, keeping a few rows of context above it.</summary>
    public void Reveal(long offset)
    {
        if (_rows.Count > 0 && offset >= _rows[0].Start && offset < _rows[Math.Min(_rows.Count, VisibleRows) - 1].End)
        {
            InvalidateVisual();
            return;
        }
        ScrollTo(offset);
        ScrollLines(-Math.Min(3, VisibleRows / 3));
    }

    public void ScrollLines(int lines)
    {
        if (_source is null || lines == 0)
        {
            return;
        }
        if (_mode == ByteViewMode.Text)
        {
            var target = _navigator!.Move(_position, lines);
            if (lines > 0)
            {
                target = Math.Min(target, LastPagePosition());
            }
            _position = Math.Max(target, _dataStart);
        }
        else
        {
            var row = RowBytes;
            _position = Math.Clamp(_position + (long)lines * row, 0, LastPagePosition());
        }
        Refresh();
    }

    public void ScrollToStart()
    {
        _position = _mode == ByteViewMode.Text ? _dataStart : 0;
        Refresh();
    }

    public void ScrollToEnd()
    {
        _position = LastPagePosition();
        Refresh();
    }

    public void ScrollHorizontally(int columns)
    {
        _horizontalOffset = Math.Max(0, _horizontalOffset + columns);
        InvalidateVisual();
    }

    /// <summary>Highlights a search hit and scrolls to it.</summary>
    public void Highlight(long offset, int length)
    {
        _highlight = new ByteHitRange(offset, length);
        _selectionStart = offset;
        _selectionEnd = offset + length;
        Reveal(offset);
        InvalidateVisual();
    }

    public void SelectAll()
    {
        _selectionStart = _mode == ByteViewMode.Text ? _dataStart : 0;
        _selectionEnd = Length;
        InvalidateVisual();
    }

    /// <summary>Selected bytes as text (decoded) or as hex pairs in hex mode; null without selection.</summary>
    public string? SelectedText(int maxBytes = 16 * 1024 * 1024)
    {
        if (Selection is not { } sel || _source is null)
        {
            return null;
        }
        var bytes = _source.ReadBytes(sel.Start, (int)Math.Min(maxBytes, sel.End - sel.Start));
        if (_mode == ByteViewMode.Hex)
        {
            return string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        }
        return _encoding.GetString(bytes);
    }

    /// <summary>Byte offset where the next search starts (after the current hit / at the top row).</summary>
    public long SearchStart(bool forward) => _highlight is { } h
        ? (forward ? h.Offset + Math.Max(1, h.Length) : h.Offset)
        : forward ? _position : (_rows.Count > 0 ? _rows[^1].End : _position);

    private int RowBytes => _mode == ByteViewMode.Hex ? HexBytesPerRow : BinaryBytesPerRow;

    private long Align(long offset)
    {
        if (_source is null)
        {
            return 0;
        }
        return _mode == ByteViewMode.Text ? _navigator!.LineStartOf(offset) : offset / RowBytes * RowBytes;
    }

    private long LastPagePosition()
    {
        if (_source is null || Length == 0)
        {
            return 0;
        }
        if (_mode == ByteViewMode.Text)
        {
            var last = _navigator!.LineStartOf(Length - 1);
            return Math.Max(_dataStart, _navigator.Move(last, -(VisibleRows - 1)));
        }
        var lastRow = (Length - 1) / RowBytes * RowBytes;
        return Math.Max(0, lastRow - (long)(VisibleRows - 1) * RowBytes);
    }

    private IReadOnlyList<TextLine> ReadRows(long position, int count)
    {
        if (_source is null)
        {
            return [];
        }
        if (_mode == ByteViewMode.Text)
        {
            return _navigator!.ReadLines(position, count);
        }
        var rows = new List<TextLine>(count);
        var row = RowBytes;
        var data = _source.ReadBytes(position, row * count);
        for (var i = 0; i * row < data.Length; i++)
        {
            var slice = data.AsSpan(i * row, Math.Min(row, data.Length - i * row));
            var start = position + (long)i * row;
            rows.Add(new TextLine(start, start + slice.Length, _mode == ByteViewMode.Hex ? HexRow(start, slice) : BinaryRow(slice)));
        }
        return rows;
    }

    private static string HexRow(long offset, ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(80);
        sb.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
        for (var i = 0; i < HexBytesPerRow; i++)
        {
            sb.Append(i < bytes.Length ? bytes[i].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
            if (i == 7)
            {
                sb.Append(' ');
            }
        }
        sb.Append(' ');
        foreach (var b in bytes)
        {
            sb.Append(b is >= 32 and < 127 ? (char)b : '.');
        }
        return sb.ToString();
    }

    private string BinaryRow(ReadOnlySpan<byte> bytes)
    {
        var text = _encoding.GetString(bytes);
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c < ' ' || c == '�' ? '·' : c);
        }
        return sb.ToString();
    }

    /// <summary>Hex row text column of byte <paramref name="index"/> within the row.</summary>
    private static int HexColumn(int index) => 10 + index * 3 + (index >= 8 ? 1 : 0);

    private static int HexAsciiColumn(int index) => 10 + HexBytesPerRow * 3 + 2 + index;

    private FormattedText Format(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, _fontSize, brush);

    public override void Render(DrawingContext context)
    {
        var background = this.FindResource("SystemControlBackgroundAltHighBrush") as IBrush ?? Brushes.White;
        context.FillRectangle(background, new Rect(Bounds.Size));
        var foreground = this.FindResource("SystemControlForegroundBaseHighBrush") as IBrush ?? Brushes.Black;
        var selection = this.FindResource("TextControlSelectionHighlightColor") as IBrush ?? new SolidColorBrush(Color.FromArgb(90, 0, 120, 215));
        var hitBrush = new SolidColorBrush(Color.FromArgb(150, 255, 200, 0));
        var offsetBrush = this.FindResource("FmHiddenBrush") as IBrush ?? Brushes.Gray;

        var x0 = 4 - _horizontalOffset * _charWidth;
        var visible = Math.Min(_rows.Count, VisibleRows + 1);
        for (var i = 0; i < visible; i++)
        {
            var row = _rows[i];
            var y = i * _lineHeight;
            DrawRange(context, row, y, x0, selection, _selectionStart, _selectionEnd);
            if (_highlight is { } h)
            {
                DrawRange(context, row, y, x0, hitBrush, h.Offset, h.Offset + Math.Max(1, h.Length));
            }
            if (_mode == ByteViewMode.Hex)
            {
                context.DrawText(Format(row.Text[..8], offsetBrush), new Point(x0, y));
                context.DrawText(Format(row.Text[8..], foreground), new Point(x0 + 8 * _charWidth, y));
            }
            else
            {
                context.DrawText(Format(row.Text, foreground), new Point(x0, y));
            }
        }
    }

    private void DrawRange(DrawingContext context, TextLine row, double y, double x0, IBrush brush, long start, long end)
    {
        if (start < 0 || end <= row.Start || start >= row.End)
        {
            return;
        }
        var from = Math.Max(start, row.Start);
        var to = Math.Min(end, row.End);
        if (_mode == ByteViewMode.Hex)
        {
            var a = (int)(from - row.Start);
            var b = (int)(to - row.Start) - 1;
            context.FillRectangle(brush, new Rect(x0 + HexColumn(a) * _charWidth, y, (HexColumn(b) - HexColumn(a) + 2) * _charWidth, _lineHeight));
            context.FillRectangle(brush, new Rect(x0 + HexAsciiColumn(a) * _charWidth, y, (b - a + 1) * _charWidth, _lineHeight));
            return;
        }
        var startColumn = ColumnOf(row, from);
        var endColumn = to >= row.End && end > row.End ? Math.Max(row.Text.Length, startColumn) + 1 : ColumnOf(row, to);
        context.FillRectangle(brush, new Rect(x0 + startColumn * _charWidth, y, Math.Max(1, endColumn - startColumn) * _charWidth, _lineHeight));
    }

    /// <summary>Text column of a byte offset inside a row.</summary>
    private int ColumnOf(TextLine row, long offset)
    {
        if (_source is null || offset <= row.Start)
        {
            return 0;
        }
        var bytes = _source.ReadBytes(row.Start, (int)(offset - row.Start));
        return _mode == ByteViewMode.Text ? _navigator!.Decode(bytes).Length : _encoding.GetCharCount(bytes);
    }

    /// <summary>Byte offset under a point (row start for text rows, exact byte in hex/binary).</summary>
    private long HitTest(Point p)
    {
        if (_rows.Count == 0)
        {
            return -1;
        }
        var index = Math.Clamp((int)(p.Y / _lineHeight), 0, _rows.Count - 1);
        var row = _rows[index];
        var column = (int)((p.X - 4) / _charWidth) + _horizontalOffset;
        switch (_mode)
        {
            case ByteViewMode.Hex:
                for (var i = HexBytesPerRow - 1; i >= 0; i--)
                {
                    if (column >= HexAsciiColumn(i) || (column < HexAsciiColumn(0) - 1 && column >= HexColumn(i)))
                    {
                        return Math.Min(row.Start + i, row.End - 1);
                    }
                }
                return row.Start;
            case ByteViewMode.Binary:
                return Math.Min(row.Start + Math.Max(0, column), row.End - 1);
            default:
                return row.Start;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var offset = HitTest(e.GetPosition(this));
        if (offset < 0)
        {
            return;
        }
        _highlight = null;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selectionAnchor >= 0)
        {
            ExtendSelection(offset);
        }
        else
        {
            _selectionAnchor = offset;
            _selectionStart = offset;
            _selectionEnd = _mode == ByteViewMode.Text ? RowEndOf(offset) : offset + 1;
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selectionAnchor < 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var p = e.GetPosition(this);
        if (p.Y < 0)
        {
            ScrollLines(-1);
        }
        else if (p.Y > Bounds.Height)
        {
            ScrollLines(1);
        }
        var offset = HitTest(p);
        if (offset >= 0)
        {
            ExtendSelection(offset);
        }
    }

    private void ExtendSelection(long offset)
    {
        if (offset >= _selectionAnchor)
        {
            _selectionStart = _selectionAnchor;
            _selectionEnd = _mode == ByteViewMode.Text ? RowEndOf(offset) : offset + 1;
        }
        else
        {
            _selectionStart = offset;
            _selectionEnd = _mode == ByteViewMode.Text ? RowEndOf(_selectionAnchor) : _selectionAnchor + 1;
        }
        InvalidateVisual();
    }

    private long RowEndOf(long offset) => _rows.FirstOrDefault(r => offset >= r.Start && offset < r.End)?.End ?? offset + 1;

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return; // zoom: handled by the Lister
        }
        if (e.Delta.X != 0 || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ScrollHorizontally((int)(-(e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 8));
        }
        else
        {
            ScrollLines((int)(-e.Delta.Y * 3));
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var handled = true;
        switch (e.Key)
        {
            case Key.Down:
                ScrollLines(1);
                break;
            case Key.Up:
                ScrollLines(-1);
                break;
            case Key.PageDown:
            case Key.Space when e.KeyModifiers == KeyModifiers.None:
                ScrollLines(VisibleRows - 1);
                break;
            case Key.PageUp:
                ScrollLines(-(VisibleRows - 1));
                break;
            case Key.Home when e.KeyModifiers.HasFlag(KeyModifiers.Control) || _horizontalOffset == 0:
                ScrollToStart();
                break;
            case Key.Home:
                _horizontalOffset = 0;
                InvalidateVisual();
                break;
            case Key.End:
                ScrollToEnd();
                break;
            case Key.Right:
                ScrollHorizontally(e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 40 : 4);
                break;
            case Key.Left:
                ScrollHorizontally(e.KeyModifiers.HasFlag(KeyModifiers.Control) ? -40 : -4);
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
        if (!handled)
        {
            base.OnKeyDown(e);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Height > e.PreviousSize.Height)
        {
            Refresh();
        }
    }

    private readonly record struct ByteHitRange(long Offset, int Length);
}
