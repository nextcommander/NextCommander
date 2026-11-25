using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.Core.Compare;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Tools;

/// <summary>
/// Editing and merging in the compare window (the plan of 25 November 2026, phase 2): typing in a side compares again
/// after a short pause (the caret stays where it was), a difference is copied to the other side, undo / redo go back
/// and forth over both sides together (a run of typing is one step), the usual compare keys.
/// </summary>
public sealed partial class CompareWindow
{
    /// <summary>Both sides at one moment (one step of undo).</summary>
    private sealed record Snapshot(List<string> Left, List<string> Right);

    private readonly Stack<Snapshot> _undoStack = new();
    private readonly Stack<Snapshot> _redoStack = new();
    private readonly DispatcherTimer _editTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>Counts the edits: a compare started before the last edit is not shown.</summary>
    private int _editVersion;

    /// <summary>Typed in a pane and not taken into the sides' lines yet.</summary>
    private bool _pendingEdit;

    /// <summary>The difference to make current after the next compare (copying, undo).</summary>
    private int? _keepBlock;

    private DiffPane? _lastPane;

    /// <summary>Typed text waits this long before it is compared (tests set it lower).</summary>
    public TimeSpan EditDelay
    {
        get => _editTimer.Interval;
        set => _editTimer.Interval = value;
    }

    /// <summary>Steps that can be undone / redone (tests).</summary>
    public int UndoCount => _undoStack.Count; // a run of typing pushed its step when it began

    public int RedoCount => _redoStack.Count;

    private void WireEditing()
    {
        WireDialogs();
        _editTimer.Tick += (_, _) =>
        {
            _editTimer.Stop();
            if (CommitEdits())
            {
                _ = CompareAsync();
            }
        };
        foreach (var pane in new[] { _leftPane, _rightPane })
        {
            var current = pane;
            pane.Editor.GotFocus += (_, _) => _lastPane = current;
        }
    }

    /// <summary>Text was typed in a side: one undo step for the run of typing, compared again after a pause.</summary>
    private void OnPaneEdited(DiffPane pane)
    {
        if (!_pendingEdit)
        {
            PushUndo();
            _pendingEdit = true;
        }
        _lastPane = pane;
        _editVersion++;
        _editTimer.Stop();
        _editTimer.Start();
        UpdateButtons();
    }

    /// <summary>The typed text taken into the sides' lines (not compared yet); true when there was some.</summary>
    private bool CommitEdits()
    {
        if (!_pendingEdit)
        {
            return false;
        }
        _editTimer.Stop();
        _pendingEdit = false;
        _l.Lines = _leftPane.FileLines();
        _r.Lines = _rightPane.FileLines();
        return true;
    }

    /// <summary>Compares the typed text now (tests; F5 does it too).</summary>
    public Task CommitAndCompareAsync() => CommitEdits() ? CompareAsync() : Task.CompletedTask;

    private void PushUndo()
    {
        _undoStack.Push(new Snapshot([.. _l.Lines], [.. _r.Lines]));
        _redoStack.Clear();
    }

    // ---------------------------------------------------------------- copying

    /// <summary>
    /// Copies a difference to the other side (Copy to Right / Left): the lines it covers there are replaced
    /// by this side's.
    /// </summary>
    public async Task CopyBlockAsync(int block, bool toRight)
    {
        if (CommitEdits())
        {
            await CompareAsync(); // the differences of the text as it is now
        }
        var blocks = TextDiff.Blocks(_rows).Where(b => !b.Ignored).ToList(); // as the differences are visited
        if (IsBinary || block < 0 || block >= blocks.Count)
        {
            return;
        }
        var (source, target) = toRight ? (_l, _r) : (_r, _l);
        if (target.ReadOnly)
        {
            ShowToast($"{target.Entry.Name} cannot be written here.");
            return;
        }
        PushUndo();
        target.Lines = DiffMerge.Copy(source.Lines, target.Lines, [blocks[block]], toRight);
        _keepBlock = block;
        await CompareAsync();
    }

    /// <summary>Copies every difference to the other side: it becomes the same as this one.</summary>
    public async Task CopyAllAsync(bool toRight)
    {
        if (CommitEdits())
        {
            await CompareAsync();
        }
        var (source, target) = toRight ? (_l, _r) : (_r, _l);
        if (IsBinary || _hunks.Count == 0 || target.ReadOnly)
        {
            return;
        }
        PushUndo();
        target.Lines = DiffMerge.CopyAll(source.Lines, target.Lines, TextDiff.Blocks(_rows), toRight);
        _keepBlock = 0;
        await CompareAsync();
    }

    public async Task UndoAsync()
    {
        CommitEdits();
        if (_undoStack.Count == 0)
        {
            return;
        }
        _redoStack.Push(new Snapshot([.. _l.Lines], [.. _r.Lines]));
        Restore(_undoStack.Pop());
        await CompareAsync();
    }

    public async Task RedoAsync()
    {
        CommitEdits();
        if (_redoStack.Count == 0)
        {
            return;
        }
        _undoStack.Push(new Snapshot([.. _l.Lines], [.. _r.Lines]));
        Restore(_redoStack.Pop());
        await CompareAsync();
    }

    private void Restore(Snapshot snapshot)
    {
        _l.Lines = [.. snapshot.Left];
        _r.Lines = [.. snapshot.Right];
        _keepBlock = Math.Max(0, _currentBlock);
    }

    /// <summary>The two sides change places (their unsaved changes and the undo steps go with them).</summary>
    public async Task SwapAsync()
    {
        CommitEdits();
        (_l, _r) = (_r, _l);
        foreach (var stack in new[] { _undoStack, _redoStack })
        {
            var steps = stack.Reverse().Select(s => new Snapshot(s.Right, s.Left)).ToList();
            stack.Clear();
            foreach (var step in steps)
            {
                stack.Push(step);
            }
        }
        _leftPane.Editor.SyntaxHighlighting = Highlighting(_l.Entry);
        _rightPane.Editor.SyntaxHighlighting = Highlighting(_r.Entry);
        _inlinePane.Editor.SyntaxHighlighting = Highlighting(_r.Entry);
        BuildFileHeaders();
        _keepBlock = Math.Max(0, _currentBlock);
        await CompareAsync();
    }

    // ---------------------------------------------------------------- the caret across compares

    private sealed record CaretPlace(bool Left, int FileLine, int Column, double ScrollX, double ScrollY);

    /// <summary>The side being edited (the last one that had the focus; null in the one-column view).</summary>
    private DiffPane? FocusedPane() => IsInline ? null : _lastPane;

    /// <summary>Where the caret is in the file being edited (only while a side has the focus).</summary>
    private CaretPlace? SaveCaret()
    {
        if (FocusedPane() is not { IsLeft: { } left } pane || !pane.Editor.IsKeyboardFocusWithin || pane.Editor.Document.LineCount == 0)
        {
            return null;
        }
        var caret = pane.Editor.TextArea.Caret;
        // a caret on a filler line counts as the file line below it
        var fileLine = pane.FileLineOf(caret.Line - 1);
        for (var line = caret.Line; fileLine < 0 && line < pane.Editor.Document.LineCount; line++)
        {
            fileLine = pane.FileLineOf(line);
        }
        var scroll = pane.Editor.TextArea.TextView.ScrollOffset;
        return new CaretPlace(left, Math.Max(0, fileLine), caret.Column, scroll.X, scroll.Y);
    }

    private void RestoreCaret(CaretPlace? place)
    {
        if (place is null)
        {
            return;
        }
        var pane = place.Left ? _leftPane : _rightPane;
        var index = pane.PaneLineOf(place.FileLine);
        if (index < 0)
        {
            index = Math.Max(0, pane.Editor.Document.LineCount - 1);
        }
        var line = pane.Editor.Document.GetLineByNumber(index + 1);
        pane.Editor.TextArea.Caret.Position = new AvaloniaEdit.TextViewPosition(index + 1, Math.Min(place.Column, line.Length + 1));
        void Scroll()
        {
            pane.Editor.ScrollToVerticalOffset(place.ScrollY);
            pane.Editor.ScrollToHorizontalOffset(place.ScrollX);
        }
        Scroll();
        Dispatcher.UIThread.Post(Scroll, DispatcherPriority.Background); // after the new document is measured
    }

    // ---------------------------------------------------------------- buttons, keys, the menu

    private void UpdateButtons()
    {
        var text = !IsBinary;
        _copyLeft.IsEnabled = text && _currentBlock >= 0 && !_l.ReadOnly;
        _copyRight.IsEnabled = text && _currentBlock >= 0 && !_r.ReadOnly;
        _undo.IsEnabled = text && UndoCount > 0;
        _redo.IsEnabled = text && _redoStack.Count > 0;
        _save.IsEnabled = text && ((_l.Dirty && !_l.ReadOnly) || (_r.Dirty && !_r.ReadOnly));
        UpdateToggles();
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        var ctrl = mods == KeyModifiers.Control || mods == KeyModifiers.Meta;
        // j / k are letters while a side is typed in
        var typing = !IsInline && (_leftPane.Editor.IsKeyboardFocusWithin && !_leftPane.Editor.IsReadOnly || _rightPane.Editor.IsKeyboardFocusWithin && !_rightPane.Editor.IsReadOnly);
        switch (e.Key)
        {
            case Key.F2 when mods == KeyModifiers.Shift:
            case Key.Up when mods == KeyModifiers.Alt:
                MoveToBlock(-1);
                break;
            case Key.Down when mods == (KeyModifiers.Control | KeyModifiers.Alt):
                _ = OtherFileAsync(+1);
                break;
            case Key.Up when mods == (KeyModifiers.Control | KeyModifiers.Alt):
                _ = OtherFileAsync(-1);
                break;
            case Key.F2 when mods == KeyModifiers.None:
            case Key.Down when mods == KeyModifiers.Alt:
                MoveToBlock(+1);
                break;
            case Key.Right when mods == KeyModifiers.Alt:
                _ = CopyBlockAsync(_currentBlock, toRight: true);
                break;
            case Key.Left when mods == KeyModifiers.Alt:
                _ = CopyBlockAsync(_currentBlock, toRight: false);
                break;
            case Key.Z when ctrl:
                _ = UndoAsync();
                break;
            case Key.Y when ctrl:
            case Key.Z when mods == (KeyModifiers.Control | KeyModifiers.Shift):
                _ = RedoAsync();
                break;
            case Key.S when ctrl:
                _ = FocusedPane() is { IsLeft: { } left } ? SaveSideAsync(!left) : SaveAllAsync();
                break;
            case Key.S when mods == (KeyModifiers.Control | KeyModifiers.Shift):
                _ = SaveAllAsync();
                break;
            case Key.G when ctrl:
                _ = GoToLineAsync();
                break;
            case Key.F5 when mods == KeyModifiers.None:
                _ = RecompareAsync();
                break;
            case Key.J when mods == KeyModifiers.None && !typing:
                MoveToBlock(+1);
                break;
            case Key.K when mods == KeyModifiers.None && !typing:
                MoveToBlock(-1);
                break;
            case Key.Escape when mods == KeyModifiers.None:
                if (_leftPane.Search is { IsOpened: true } || _rightPane.Search is { IsOpened: true })
                {
                    _leftPane.Search?.Close();
                    _rightPane.Search?.Close();
                }
                else
                {
                    Close();
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Ctrl+G: a line of the file being edited (the left one when none is).</summary>
    private async Task GoToLineAsync()
    {
        var pane = FocusedPane() ?? _leftPane;
        var answer = await _context.Dialogs.PromptAsync("Go to line", "Line number:", string.Empty);
        if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.CurrentCulture, out var number) || number < 1)
        {
            return;
        }
        GoToLine(pane == _rightPane, number);
    }

    /// <summary>Brings a line of a file into view with the caret on it.</summary>
    public void GoToLine(bool right, int number)
    {
        if (IsInline)
        {
            SetView(inline: false);
        }
        var pane = right ? _rightPane : _leftPane;
        var index = pane.PaneLineOf(number - 1);
        if (index < 0)
        {
            index = Math.Max(0, pane.Editor.Document.LineCount - 1);
        }
        pane.Editor.TextArea.Caret.Position = new AvaloniaEdit.TextViewPosition(index + 1, 1);
        pane.Editor.ScrollTo(index + 1, 1);
        pane.Editor.Focus();
    }

    private void OpenMoreMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string icon, Func<Task> run, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = MenuIcons.Create(icon) };
            if (gesture is not null)
            {
                item.InputGesture = KeyGesture.Parse(gesture);
            }
            item.Click += (_, _) => _ = run();
            return item;
        }
        var text = !IsBinary;
        menu.Items.Add(Item("Next difference", "compare.next", () => Run(() => MoveToBlock(+1)), text && _hunks.Count > 0, "Alt+Down"));
        menu.Items.Add(Item("Previous difference", "compare.previous", () => Run(() => MoveToBlock(-1)), text && _hunks.Count > 0, "Alt+Up"));
        if (Neighbor is not null)
        {
            menu.Items.Add(Item("Next different file", "compare.nextfile", () => OtherFileAsync(+1), true, "Ctrl+Alt+Down"));
            menu.Items.Add(Item("Previous different file", "compare.previousfile", () => OtherFileAsync(-1), true, "Ctrl+Alt+Up"));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy to the right", "compare.copyright", () => CopyBlockAsync(_currentBlock, toRight: true), _copyRight.IsEnabled, "Alt+Right"));
        menu.Items.Add(Item("Copy to the left", "compare.copyleft", () => CopyBlockAsync(_currentBlock, toRight: false), _copyLeft.IsEnabled, "Alt+Left"));
        menu.Items.Add(Item("Copy all to the right", "compare.copyallright", () => CopyAllAsync(toRight: true), text && _hunks.Count > 0 && !_r.ReadOnly));
        menu.Items.Add(Item("Copy all to the left", "compare.copyallleft", () => CopyAllAsync(toRight: false), text && _hunks.Count > 0 && !_l.ReadOnly));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Undo", "editor.undo", UndoAsync, _undo.IsEnabled, "Ctrl+Z"));
        menu.Items.Add(Item("Redo", "editor.redo", RedoAsync, _redo.IsEnabled, "Ctrl+Y"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Save the left file", "editor.save", () => SaveSideAsync(false), text && _l.Dirty && !_l.ReadOnly));
        menu.Items.Add(Item("Save the right file", "editor.save", () => SaveSideAsync(true), text && _r.Dirty && !_r.ReadOnly));
        menu.Items.Add(Item("Save both", "editor.save", SaveAllAsync, _save.IsEnabled, "Ctrl+Shift+S"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Swap the sides", "compare.swap", SwapAsync, text));
        menu.Items.Add(Item("Load the left file again", "editor.reload", () => ReloadSideAsync(false), text));
        menu.Items.Add(Item("Load the right file again", "editor.reload", () => ReloadSideAsync(true), text));
        menu.Items.Add(new Separator());
        menu.Items.Add(OptionsMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Find...", "editor.find", () => Run(OpenSearch), text, "Ctrl+F"));
        menu.Items.Add(Item("Go to line...", "editor.gotoline", GoToLineAsync, text, "Ctrl+G"));
        if (!_context.Settings.ShowMenuIcons)
        {
            MenuIcons.Clear(menu.Items);
        }
        menu.Open(_more);

        static Task Run(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The options of the comparison (the same as Configuration > Operation/Compare, kept there), then the settings page
    /// itself.
    /// </summary>
    private MenuItem OptionsMenu()
    {
        var options = new MenuItem { Header = "Compare options", Icon = MenuIcons.Create("compare.options") };
        void Toggle(string header, bool on, Action<bool> set)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
            item.Click += (_, _) =>
            {
                set(!on);
                SyncSwitches();
                _ = CompareAsync();
            };
            options.Items.Add(item);
        }
        Toggle("Ignore blank lines", Options.IgnoreBlankLines, v => Options.IgnoreBlankLines = v);
        Toggle("Ignore line ends (CRLF / LF)", Options.IgnoreLineEndings, v => Options.IgnoreLineEndings = v);
        Toggle("Ignore all spaces", Options.Whitespace == CompareWhitespace.IgnoreAll,
            v => Options.Whitespace = v ? CompareWhitespace.IgnoreAll : CompareWhitespace.Compare);
        Toggle("Mark moved lines", Options.DetectMoved, v => Options.DetectMoved = v);
        Toggle("Put alike lines side by side", Options.MatchSimilarLines, v => Options.MatchSimilarLines = v);
        Toggle("Mark differences by words", Options.WordLevel, v => Options.WordLevel = v);
        if (Options.LineFilters.Count + Options.Substitutions.Count > 0)
        {
            var filters = Options.LineFilters.Where(f => f.Enabled).Count() + Options.Substitutions.Where(f => f.Enabled).Count();
            options.Items.Add(new MenuItem { Header = $"Filters on: {filters}", IsEnabled = false });
        }
        options.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Compare settings...", Icon = MenuIcons.Create("nc_Config"), IsEnabled = _context.RunCommand is not null };
        settings.Click += (_, _) => _ = _context.RunCommand?.Invoke("nc_Config Operation/Compare");
        options.Items.Add(settings);
        return options;
    }

    /// <summary>The two switches of the bar as the settings are now.</summary>
    private void SyncSwitches()
    {
        _ignoreCase.IsChecked = Options.IgnoreCase;
        _ignoreSpace.IsChecked = Options.Whitespace != CompareWhitespace.Compare;
    }

    /// <summary>The search of the side being edited (the left one when none is).</summary>
    private void OpenSearch()
    {
        if (IsInline)
        {
            SetView(inline: false);
        }
        var pane = FocusedPane() ?? _leftPane;
        pane.Editor.Focus();
        pane.Search?.Open();
    }
}
