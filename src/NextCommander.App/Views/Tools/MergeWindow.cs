using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>The three files of a 3-way merge: the common ancestor and the two versions made from it.</summary>
public sealed record MergeSources(VfsEntry Base, VfsEntry Left, VfsEntry Right);

/// <summary>
/// The 3-way merge (the compare and merge plan, phase 6; 25 November 2026): the result with its conflicts marked as git writes
/// them, edited freely; for each conflict the left, the base, the right or both sides are taken (Alt+1 .. Alt+4), F2 /
/// Alt+↓ go to the next one. Given three files, they are shown above the result with their changes colored and the
/// conflict in view; given one file with conflict markers (a git merge that stopped), it is resolved in place. Saving
/// writes the result in the output file's encoding and line ends.
/// </summary>
public sealed class MergeWindow : Window
{
    private readonly ToolContext _context;
    private readonly VfsEntry _output;
    private readonly MergeSources? _sources;
    private readonly TextEditor _result;
    private readonly TextEditor[] _panes;
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 0) };
    private readonly TextBlock _counter = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Center, Classes = { "noloc" } };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly List<Button> _takes = [];
    private IReadOnlyList<ConflictBlock> _blocks = [];
    private IReadOnlyList<MergeChunk> _chunks = [];
    private TextContent _format = TextContent.Empty();
    private bool _modified;
    private bool _closingConfirmed;
    private int _current;

    public MergeWindow(VfsEntry output, MergeSources? sources, ToolContext context)
    {
        _output = output;
        _sources = sources;
        _context = context;
        Title = $"Merge: {output.Name}";
        Width = 1240;
        Height = 820;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var font = new FontFamily(string.IsNullOrWhiteSpace(context.Settings.CompareOptions.Font) ? context.Settings.ListerFont : context.Settings.CompareOptions.Font);
        var size = context.Settings.CompareOptions.FontSize > 0 ? context.Settings.CompareOptions.FontSize : context.Settings.ListerFontSize;
        var highlighting = HighlightingManager.Instance.GetDefinitionByExtension("." + output.Extension);
        TextEditor Editor(bool readOnly)
        {
            var editor = new TextEditor { FontFamily = font, FontSize = size, ShowLineNumbers = true, IsReadOnly = readOnly, SyntaxHighlighting = highlighting };
            editor.Options.EnableHyperlinks = false;
            ThemeAwareColors.Attach(editor);
            return editor;
        }
        _result = Editor(readOnly: false);
        _result.TextArea.TextView.BackgroundRenderers.Add(new ResultColors(this));
        _panes = sources is null ? [] : [Editor(true), Editor(true), Editor(true)];
        for (var i = 0; i < _panes.Length; i++)
        {
            _panes[i].TextArea.TextView.BackgroundRenderers.Add(new PaneColors(this, i));
        }
        Content = BuildLayout();
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AskSaveOnClose = async () =>
        {
            var dialog = new ChoiceDialog<SaveChoice>("Unsaved changes", $"{_output.Name} changed. Save the changes?",
                [("Save", SaveChoice.Save), ("Don't save", SaveChoice.Discard), ("Cancel", SaveChoice.Cancel)], cancelValue: SaveChoice.Cancel);
            await dialog.ShowDialog(this);
            return dialog.Result;
        };
        AskSaveWithConflicts = async count =>
        {
            var dialog = new ChoiceDialog<bool>("Merge", count == 1 ? "1 conflict is not resolved. Save with its markers?" : $"{count} conflicts are not resolved. Save with their markers?",
                [("Save", true), ("Cancel", false)], cancelValue: false);
            await dialog.ShowDialog(this);
            return dialog.Result;
        };
        Opened += (_, _) => Loading = LoadAsync();
    }

    // ---------------------------------------------------------------- what tests and callers see

    public Task? Loading { get; private set; }

    /// <summary>The conflicts left in the result.</summary>
    public int ConflictCount => _blocks.Count;

    /// <summary>The current conflict (-1: none).</summary>
    public int Current => _blocks.Count == 0 ? -1 : _current;

    public string ResultText => _result.Text;

    public TextEditor ResultEditor => _result;

    /// <summary>The result was saved with no conflict left (git can stage it).</summary>
    public bool Resolved { get; private set; }

    public Func<Task<SaveChoice>> AskSaveOnClose { get; set; }

    /// <summary>Asked when saving with conflicts left (their number): save anyway.</summary>
    public Func<int, Task<bool>> AskSaveWithConflicts { get; set; }

    // ---------------------------------------------------------------- layout

    private Button Action(string text, string tip, Action click)
    {
        var button = new Button { Content = text, Classes = { "dialog" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private Control BuildLayout()
    {
        var previous = Action("↑", "Previous conflict (Shift+F2, Alt+Up)", () => Move(-1));
        var next = Action("↓", "Next conflict (F2, Alt+Down)", () => Move(+1));
        _takes.AddRange([
            Action("Left", "Take the left side (Alt+1)", () => Take(ConflictChoice.Left)),
            Action("Base", "Take the base (Alt+2)", () => Take(ConflictChoice.Base)),
            Action("Right", "Take the right side (Alt+3)", () => Take(ConflictChoice.Right)),
            Action("Both", "Take the left, then the right side (Alt+4)", () => Take(ConflictChoice.LeftThenRight)),
        ]);
        var save = Action("Save", "Save the result (Ctrl+S)", () => _ = SaveAsync());
        save.Classes.Add("accent");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 10) };
        bar.Children.AddRange([_count, previous, _counter, next, new Border { Width = 12 }, new TextBlock { Text = "Take", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 }]);
        bar.Children.AddRange(_takes);
        bar.Children.AddRange([new Border { Width = 12 }, save]);

        var result = new DockPanel();
        var resultHead = new TextBlock { Text = "Result → " + _output.Path.ToDisplayString(), Margin = new Thickness(12, 6), Opacity = 0.75, Classes = { "noloc" }, TextTrimming = TextTrimming.LeadingCharacterEllipsis };
        DockPanel.SetDock(resultHead, Dock.Top);
        result.Children.AddRange([resultHead, _result]);

        var body = new Grid();
        if (_sources is { } sources)
        {
            body.RowDefinitions = new RowDefinitions("*,Auto,1.3*");
            var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
            var names = new[] { ("Left", sources.Left), ("Base", sources.Base), ("Right", sources.Right) };
            for (var i = 0; i < 3; i++)
            {
                // the side's name stays whole; its path gives way from the start
                var label = new TextBlock { Text = names[i].Item1, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
                var path = new TextBlock { Text = names[i].Item2.Path.ToDisplayString(), TextTrimming = TextTrimming.LeadingCharacterEllipsis, Classes = { "noloc" } };
                ToolTip.SetTip(path, path.Text);
                var head = new DockPanel { Margin = new Thickness(12, 6), Opacity = 0.75 };
                DockPanel.SetDock(label, Dock.Left);
                head.Children.AddRange([label, path]);
                var column = new DockPanel();
                DockPanel.SetDock(head, Dock.Top);
                column.Children.AddRange([head, _panes[i]]);
                Grid.SetColumn(column, i);
                panes.Children.Add(column);
            }
            var splitter = new GridSplitter { Height = 6, ResizeDirection = GridResizeDirection.Rows };
            Grid.SetRow(splitter, 1);
            Grid.SetRow(result, 2);
            body.Children.AddRange([panes, splitter, result]);
        }
        else
        {
            body.Children.Add(result);
        }

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        _status.Margin = new Thickness(12, 6);
        root.Children.AddRange([bar, _status, body]);
        return root;
    }

    // ---------------------------------------------------------------- loading

    private async Task<TextContent> ReadAsync(VfsEntry entry)
    {
        await using var stream = await _context.Registry.Resolve(entry.Path).OpenReadAsync(entry.Path, CancellationToken.None);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return TextContent.Read(memory.ToArray());
    }

    private async Task LoadAsync()
    {
        try
        {
            List<string> lines;
            if (_sources is { } sources)
            {
                var @base = await ReadAsync(sources.Base);
                var left = await ReadAsync(sources.Left);
                var right = await ReadAsync(sources.Right);
                _panes[0].Document = new TextDocument(string.Join('\n', left.Lines));
                _panes[1].Document = new TextDocument(string.Join('\n', @base.Lines));
                _panes[2].Document = new TextDocument(string.Join('\n', right.Lines));
                (lines, _chunks) = ConflictMarkers.Merge(@base.Lines, left.Lines, right.Lines, sources.Left.Name, sources.Base.Name, sources.Right.Name,
                    _context.Settings.CompareOptions.ToOptions());
                // the result is written as the output file is (or as the left one when it is new)
                _format = await _context.Registry.Resolve(_output.Path).GetEntryAsync(_output.Path, CancellationToken.None) is { } existing
                    && existing.Path != sources.Left.Path ? await ReadAsync(existing) : left;
            }
            else
            {
                _format = await ReadAsync(_output);
                lines = _format.Lines;
            }
            _result.Document = new TextDocument(string.Join('\n', lines));
            _result.Document.Changed += (_, _) =>
            {
                _modified = true;
                Refresh(scroll: false);
            };
            _modified = _sources is not null; // a merge of three files is new text
            Refresh(scroll: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
    }

    // ---------------------------------------------------------------- conflicts

    /// <summary>The conflicts found again in the result, the counts and the current one.</summary>
    private void Refresh(bool scroll)
    {
        _blocks = ConflictMarkers.Find(Lines());
        _current = Math.Clamp(_current, 0, Math.Max(0, _blocks.Count - 1));
        _count.Text = _blocks.Count == 0 ? "No conflicts left" : _blocks.Count == 1 ? "1 conflict" : $"{_blocks.Count} conflicts";
        _counter.Text = _blocks.Count == 0 ? "–" : $"{_current + 1} / {_blocks.Count}";
        foreach (var take in _takes)
        {
            take.IsEnabled = _blocks.Count > 0;
        }
        _takes[1].IsEnabled = _blocks.Count > 0 && _blocks[_current].Base is not null;
        _status.Text = _sources is null
            ? "Conflict markers of the file: take a side for each conflict or edit the text; Save writes the file."
            : "Changes of one side are merged; the conflicts are marked in the result.";
        Title = $"Merge: {_output.Name}{(_modified ? "*" : string.Empty)}";
        _result.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var pane in _panes)
        {
            pane.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        if (scroll)
        {
            ShowCurrent();
        }
    }

    private List<string> Lines() => [.. _result.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    /// <summary>Goes to the next (+1) or previous (-1) conflict, around from the last to the first.</summary>
    public void Move(int direction)
    {
        if (_blocks.Count == 0)
        {
            return;
        }
        _current = ((_current + direction) % _blocks.Count + _blocks.Count) % _blocks.Count;
        Refresh(scroll: true);
    }

    /// <summary>The current conflict in view: in the result and, for three files, where it is in each of them.</summary>
    private void ShowCurrent()
    {
        if (_blocks.Count == 0)
        {
            return;
        }
        var block = _blocks[_current];
        _result.ScrollTo(block.Start + 1, 1);
        _result.TextArea.Caret.Line = block.Start + 1;
        if (_panes.Length == 3 && ConflictMarkers.NumberOf(block) is { } number
            && _chunks.Where(c => c.Kind == MergeChunkKind.Conflict).ElementAtOrDefault(number - 1) is { } chunk)
        {
            _panes[0].ScrollTo(chunk.LeftStart + 1, 1);
            _panes[1].ScrollTo(chunk.BaseStart + 1, 1);
            _panes[2].ScrollTo(chunk.RightStart + 1, 1);
        }
    }

    /// <summary>The current conflict becomes a side (or the base, or both): one step of the editor's undo.</summary>
    public void Take(ConflictChoice choice)
    {
        if (_blocks.Count == 0)
        {
            return;
        }
        var block = _blocks[_current];
        if (choice == ConflictChoice.Base && block.Base is null)
        {
            return;
        }
        var document = _result.Document;
        var first = document.GetLineByNumber(block.Start + 1);
        var last = document.GetLineByNumber(block.End);
        var lines = ConflictMarkers.LinesOf(block, choice);
        var text = string.Join('\n', lines) + (lines.Count > 0 && last.DelimiterLength > 0 ? "\n" : string.Empty);
        var end = last.EndOffset + last.DelimiterLength;
        if (lines.Count == 0 && last.DelimiterLength == 0 && first.Offset > 0)
        {
            // the conflict is the end of the text: the line break before it goes too
            document.Replace(first.Offset - 1, end - first.Offset + 1, string.Empty);
        }
        else
        {
            document.Replace(first.Offset, end - first.Offset, text);
        }
        Refresh(scroll: true);
    }

    // ---------------------------------------------------------------- saving

    /// <summary>Writes the result (asked first when conflicts are left); true when saved.</summary>
    public async Task<bool> SaveAsync()
    {
        if (_blocks.Count > 0 && !await AskSaveWithConflicts(_blocks.Count))
        {
            return false;
        }
        var content = new TextContent
        {
            Lines = Lines(),
            Encoding = _format.Encoding,
            Bom = _format.Bom,
            NewLine = _format.NewLine,
            EndsWithNewline = _format.EndsWithNewline,
        };
        try
        {
            var provider = _context.Registry.Resolve(_output.Path);
            await using (var stream = await provider.OpenWriteAsync(_output.Path, overwrite: true, CancellationToken.None))
            {
                await stream.WriteAsync(content.Bytes());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.EncoderFallbackException)
        {
            _status.Text = ex.Message;
            return false;
        }
        _modified = false;
        Resolved = _blocks.Count == 0;
        Refresh(scroll: false);
        _status.Text = $"Saved {_output.Name}";
        return true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || !_modified)
        {
            return;
        }
        e.Cancel = true;
        Dispatcher.UIThread.Post(async () =>
        {
            var choice = await AskSaveOnClose();
            if (choice == SaveChoice.Cancel || (choice == SaveChoice.Save && !await SaveAsync()))
            {
                return;
            }
            _closingConfirmed = true;
            Close();
        });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.F2 when mods == KeyModifiers.Shift:
            case Key.Up when mods == KeyModifiers.Alt:
                Move(-1);
                break;
            case Key.F2 when mods == KeyModifiers.None:
            case Key.Down when mods == KeyModifiers.Alt:
                Move(+1);
                break;
            case Key.D1 or Key.NumPad1 when mods == KeyModifiers.Alt:
                Take(ConflictChoice.Left);
                break;
            case Key.D2 or Key.NumPad2 when mods == KeyModifiers.Alt:
                Take(ConflictChoice.Base);
                break;
            case Key.D3 or Key.NumPad3 when mods == KeyModifiers.Alt:
                Take(ConflictChoice.Right);
                break;
            case Key.D4 or Key.NumPad4 when mods == KeyModifiers.Alt:
                Take(ConflictChoice.LeftThenRight);
                break;
            case Key.S when mods == KeyModifiers.Control || mods == KeyModifiers.Meta:
                _ = SaveAsync();
                break;
            case Key.Escape when mods == KeyModifiers.None:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- colors

    private bool Dark => ActualThemeVariant == ThemeVariant.Dark;

    private IBrush Tint(string light, string dark) => new SolidColorBrush(Color.Parse(Dark ? dark : light));

    /// <summary>The conflicts in the result: the markers gray, the left side blue, the base yellow, the right side green; the current one framed.</summary>
    private sealed class ResultColors(MergeWindow owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (!textView.VisualLinesValid || owner._blocks.Count == 0)
            {
                return;
            }
            var marker = owner.Tint("#E6EAF0", "#2A303B");
            var left = owner.Tint("#E3EDFD", "#16263D");
            var @base = owner.Tint("#FFF6E5", "#3A3020");
            var right = owner.Tint("#E9F7EE", "#16301F");
            var frame = new Pen(owner.Tint("#1F6FEB", "#58A6FF"), 1.5);
            foreach (var visual in textView.VisualLines)
            {
                var line = visual.FirstDocumentLine.LineNumber - 1;
                for (var b = 0; b < owner._blocks.Count; b++)
                {
                    var block = owner._blocks[b];
                    if (line < block.Start || line >= block.End)
                    {
                        continue;
                    }
                    var baseStart = block.Base is null ? -1 : block.Start + 1 + block.Left.Count;
                    var middle = block.Start + 1 + block.Left.Count + (block.Base is null ? 0 : block.Base.Count + 1);
                    var brush = line == block.Start || line == block.End - 1 || line == middle || line == baseStart ? marker
                        : line < (baseStart >= 0 ? baseStart : middle) ? left
                        : line < middle ? @base : right;
                    var rect = new Rect(0, visual.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, visual.Height);
                    context.FillRectangle(brush, rect);
                    if (b == owner._current)
                    {
                        if (line == block.Start)
                        {
                            context.DrawLine(frame, rect.TopLeft, rect.TopRight);
                        }
                        if (line == block.End - 1)
                        {
                            context.DrawLine(frame, rect.BottomLeft, rect.BottomRight);
                        }
                    }
                }
            }
        }
    }

    /// <summary>The changes in one of the three files: conflicts red, the others amber.</summary>
    private sealed class PaneColors(MergeWindow owner, int pane) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (!textView.VisualLinesValid)
            {
                return;
            }
            var conflict = owner.Tint("#FDEDEF", "#3A1C20");
            var change = owner.Tint("#FFF6E5", "#3A3020");
            foreach (var visual in textView.VisualLines)
            {
                var line = visual.FirstDocumentLine.LineNumber - 1;
                foreach (var chunk in owner._chunks)
                {
                    if (chunk.Kind == MergeChunkKind.Unchanged)
                    {
                        continue;
                    }
                    var (start, count) = pane switch
                    {
                        0 => (chunk.LeftStart, chunk.LeftCount),
                        1 => (chunk.BaseStart, chunk.BaseCount),
                        _ => (chunk.RightStart, chunk.RightCount),
                    };
                    var changedHere = chunk.Kind == MergeChunkKind.Conflict || chunk.Kind == MergeChunkKind.BothSame
                        || (pane == 0 && chunk.Kind == MergeChunkKind.LeftChanged) || (pane == 2 && chunk.Kind == MergeChunkKind.RightChanged);
                    if (changedHere && line >= start && line < start + count)
                    {
                        context.FillRectangle(chunk.Kind == MergeChunkKind.Conflict ? conflict : change,
                            new Rect(0, visual.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, visual.Height));
                    }
                }
            }
        }
    }
}
