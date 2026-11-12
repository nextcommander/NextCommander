using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using NextCommander.App.Services;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Compare;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>
/// Compare by content: side-by-side text diff with aligned lines, in-line character differences, synchronized
/// scrolling and next/previous difference (F2 / Shift+F2). Binary files get a list of differing offsets.
/// </summary>
public sealed class CompareWindow : Window
{
    private const long MaxTextBytes = 64L * 1024 * 1024;

    private readonly ToolContext _context;
    private readonly VfsEntry _left;
    private readonly VfsEntry _right;
    private readonly TextEditor _leftEditor = NewEditor();
    private readonly TextEditor _rightEditor = NewEditor();
    private readonly CheckBox _ignoreCase = new() { Content = "Ignore case" };
    private readonly CheckBox _ignoreSpace = new() { Content = "Ignore whitespace" };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 3), FontSize = 12 };
    private readonly TextBlock _leftTitle = new() { Margin = new Thickness(6, 3), TextTrimming = TextTrimming.LeadingCharacterEllipsis };
    private readonly TextBlock _rightTitle = new() { Margin = new Thickness(6, 3), TextTrimming = TextTrimming.LeadingCharacterEllipsis };
    private readonly ListBox _binaryList = new() { IsVisible = false };
    private readonly Grid _textView;
    private IReadOnlyList<DiffLine> _rows = [];
    private List<int> _blocks = [];
    private int _currentBlock = -1;
    private bool _syncing;

    public CompareWindow(VfsEntry left, VfsEntry right, ToolContext context)
    {
        _left = left;
        _right = right;
        _context = context;
        Title = $"Compare: {left.Name} ↔ {right.Name}";
        Width = 1200;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        EncodingDetector.EnsureCodePages();

        foreach (var (editor, isLeft) in new[] { (_leftEditor, true), (_rightEditor, false) })
        {
            editor.FontFamily = new FontFamily(context.Settings.ListerFont);
            editor.FontSize = context.Settings.ListerFontSize;
            editor.TextArea.TextView.BackgroundRenderers.Add(new DiffBackground(this, isLeft));
            ThemeAwareColors.Attach(editor);
            var other = isLeft ? _rightEditor : _leftEditor;
            editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
            {
                if (_syncing)
                {
                    return;
                }
                _syncing = true;
                var offset = editor.TextArea.TextView.ScrollOffset;
                other.ScrollToVerticalOffset(offset.Y);
                other.ScrollToHorizontalOffset(offset.X);
                _syncing = false;
            };
        }
        _ignoreCase.IsCheckedChanged += (_, _) => _ = CompareAsync();
        _ignoreSpace.IsCheckedChanged += (_, _) => _ = CompareAsync();

        var next = new Button { Content = "Next difference (F2)" };
        next.Click += (_, _) => MoveToBlock(+1);
        var previous = new Button { Content = "Previous difference (Shift+F2)" };
        previous.Click += (_, _) => MoveToBlock(-1);
        var reload = new Button { Content = "Compare again (F5)" };
        reload.Click += (_, _) => _ = CompareAsync();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 6) };
        toolbar.Children.AddRange([next, previous, reload, _ignoreCase, _ignoreSpace]);
        foreach (var check in new[] { _ignoreCase, _ignoreSpace })
        {
            check.VerticalAlignment = VerticalAlignment.Center;
        }

        _textView = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*"), RowDefinitions = new RowDefinitions("Auto,*") };
        AddAt(_textView, Header(_leftTitle), 0, 0);
        AddAt(_textView, Header(_rightTitle), 0, 2);
        AddAt(_textView, _leftEditor, 1, 0);
        AddAt(_textView, new GridSplitter { ResizeDirection = GridResizeDirection.Columns }, 1, 1);
        AddAt(_textView, _rightEditor, 1, 2);

        var statusBar = new Border { Child = _status };
        statusBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(statusBar);
        var body = new Panel();
        body.Children.Add(_textView);
        body.Children.Add(_binaryList);
        root.Children.Add(body);
        Content = root;
        Opened += async (_, _) => await CompareAsync();
    }

    public IReadOnlyList<DiffLine> Rows => _rows;

    public int BlockCount => _blocks.Count;

    public int CurrentBlock => _currentBlock;

    public bool IsBinary { get; private set; }

    public IReadOnlyList<string> BinaryDifferences => _binaryList.ItemsSource as IReadOnlyList<string> ?? [];

    public string StatusText => _status.Text ?? string.Empty;

    public Task? Comparing { get; private set; }

    private static TextEditor NewEditor()
    {
        var editor = new TextEditor { IsReadOnly = true, ShowLineNumbers = false, Padding = new Thickness(4, 0) };
        editor.Options.EnableHyperlinks = false;
        return editor;
    }

    private static Border Header(TextBlock text)
    {
        var border = new Border { Child = text };
        border[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmPanelHeaderBrush");
        return border;
    }

    private static void AddAt(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    public Task CompareAsync()
    {
        Comparing = CompareCoreAsync();
        return Comparing;
    }

    private async Task CompareCoreAsync()
    {
        _leftTitle.Text = _left.Path.ToDisplayString();
        _rightTitle.Text = _right.Path.ToDisplayString();
        _status.Text = "Comparing...";
        byte[] a, b;
        try
        {
            a = await ReadAsync(_left);
            b = await ReadAsync(_right);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
            return;
        }
        IsBinary = EncodingDetector.LooksBinary(a.AsSpan(0, Math.Min(a.Length, 8192))) || EncodingDetector.LooksBinary(b.AsSpan(0, Math.Min(b.Length, 8192)));
        _textView.IsVisible = !IsBinary;
        _binaryList.IsVisible = IsBinary;
        if (IsBinary)
        {
            CompareBinary(a, b);
            return;
        }
        var options = new DiffOptions { IgnoreCase = _ignoreCase.IsChecked == true, IgnoreWhitespace = _ignoreSpace.IsChecked == true };
        _rows = await Task.Run(() => TextDiff.Compare(SplitLines(a), SplitLines(b), options));
        var left = new StringBuilder();
        var right = new StringBuilder();
        foreach (var row in _rows)
        {
            left.Append(row.Left ?? string.Empty).Append('\n');
            right.Append(row.Right ?? string.Empty).Append('\n');
        }
        _leftEditor.Document = new TextDocument(left.ToString());
        _rightEditor.Document = new TextDocument(right.ToString());
        _blocks = [];
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Kind != DiffKind.Equal && (i == 0 || _rows[i - 1].Kind == DiffKind.Equal))
            {
                _blocks.Add(i);
            }
        }
        _currentBlock = -1;
        var changed = _rows.Count(r => r.Kind == DiffKind.Changed);
        var removed = _rows.Count(r => r.Kind == DiffKind.Removed);
        var added = _rows.Count(r => r.Kind == DiffKind.Added);
        _status.Text = _blocks.Count == 0
            ? "Files are identical" + (options.IgnoreCase || options.IgnoreWhitespace ? " (selected differences ignored)." : ".")
            : $"{_blocks.Count} difference blocks: {changed} changed, {removed} left-only, {added} right-only lines.";
        if (_blocks.Count > 0)
        {
            MoveToBlock(+1);
        }
    }

    private async Task<byte[]> ReadAsync(VfsEntry entry)
    {
        if (entry.Size > MaxTextBytes)
        {
            throw new IOException($"{entry.Name} is too large to compare ({entry.Size:N0} bytes).");
        }
        await using var stream = await _context.Registry.Resolve(entry.Path).OpenReadAsync(entry.Path, CancellationToken.None);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static List<string> SplitLines(byte[] data)
    {
        var encoding = EncodingDetector.Detect(data, out var bom, complete: true);
        var text = encoding.GetString(data, bom, data.Length - bom);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1); // the final line break does not start a new line
        }
        return lines;
    }

    private void CompareBinary(byte[] a, byte[] b)
    {
        var diffs = new List<string>();
        var count = 0L;
        var common = Math.Min(a.Length, b.Length);
        for (var i = 0; i < common; i++)
        {
            if (a[i] != b[i])
            {
                count++;
                if (diffs.Count < 5000)
                {
                    diffs.Add(string.Format(CultureInfo.InvariantCulture, "0x{0:X8}   {1:X2} ≠ {2:X2}", i, a[i], b[i]));
                }
            }
        }
        if (a.Length != b.Length)
        {
            diffs.Add($"Sizes differ: {a.Length:N0} / {b.Length:N0} bytes");
        }
        _binaryList.ItemsSource = diffs;
        _status.Text = count == 0 && a.Length == b.Length
            ? "The binary files are identical."
            : $"Binary comparison: {count:N0} different bytes" + (a.Length != b.Length ? ", sizes differ." : ".");
    }

    public void MoveToBlock(int direction)
    {
        if (_blocks.Count == 0)
        {
            return;
        }
        _currentBlock = Math.Clamp(_currentBlock + direction, 0, _blocks.Count - 1);
        var line = _blocks[_currentBlock] + 1;
        foreach (var editor in new[] { _leftEditor, _rightEditor })
        {
            editor.ScrollToLine(line);
            editor.TextArea.Caret.Line = line;
        }
        _leftEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        _rightEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        _status.Text = $"Difference {_currentBlock + 1}/{_blocks.Count} (line {line})";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.F2 when e.KeyModifiers == KeyModifiers.Shift:
                MoveToBlock(-1);
                e.Handled = true;
                break;
            case Key.F2:
                MoveToBlock(+1);
                e.Handled = true;
                break;
            case Key.F5:
                _ = CompareAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Colors the rows by kind and the changed characters of changed rows.</summary>
    private sealed class DiffBackground(CompareWindow owner, bool left) : IBackgroundRenderer
    {
        private static readonly IBrush s_removed = new SolidColorBrush(Color.FromArgb(70, 230, 80, 80));
        private static readonly IBrush s_added = new SolidColorBrush(Color.FromArgb(70, 60, 190, 90));
        private static readonly IBrush s_changed = new SolidColorBrush(Color.FromArgb(60, 230, 190, 40));
        private static readonly IBrush s_inline = new SolidColorBrush(Color.FromArgb(120, 230, 150, 20));
        private static readonly IBrush s_filler = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        private static readonly IBrush s_current = new SolidColorBrush(Color.FromArgb(90, 80, 140, 255));

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var rows = owner._rows;
            if (rows.Count == 0 || !textView.VisualLinesValid)
            {
                return;
            }
            var currentStart = owner._currentBlock >= 0 && owner._currentBlock < owner._blocks.Count ? owner._blocks[owner._currentBlock] : -1;
            foreach (var visual in textView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= rows.Count)
                {
                    continue;
                }
                var row = rows[index];
                var text = left ? row.Left : row.Right;
                var brush = text is null ? s_filler : row.Kind switch
                {
                    DiffKind.Removed => s_removed,
                    DiffKind.Added => s_added,
                    DiffKind.Changed => s_changed,
                    _ => null,
                };
                var y = visual.VisualTop - textView.ScrollOffset.Y;
                if (brush is not null)
                {
                    drawingContext.FillRectangle(brush, new Rect(0, y, textView.Bounds.Width, visual.Height));
                }
                if (index == currentStart)
                {
                    drawingContext.FillRectangle(s_current, new Rect(0, y, 3, visual.Height));
                }
                if (row.Kind == DiffKind.Changed && row.Left is not null && row.Right is not null)
                {
                    var (l, r) = TextDiff.InlineDiff(row.Left, row.Right);
                    var line = visual.FirstDocumentLine;
                    foreach (var span in (left ? l : r).Where(s => s.Different))
                    {
                        var segment = new TextSegment { StartOffset = line.Offset + span.Start, Length = span.Length };
                        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                        {
                            drawingContext.FillRectangle(s_inline, rect);
                        }
                    }
                }
            }
        }
    }
}
