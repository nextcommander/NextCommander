using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AvaloniaEdit.Document;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Editing and merging in the compare window (the compare and merge plan, phase 2; 25 November 2026).</summary>
public sealed class CompareMergeTests : IDisposable
{
    private readonly UiFixture _fx = new();

    private ViewModels.MainWindowViewModel? _vm;

    public void Dispose() => _fx.Dispose();

    private async Task<CompareWindow> OpenAsync(string left, string right, Encoding? leftEncoding = null)
    {
        var (_, vm) = await _fx.OpenAsync();
        _vm = vm;
        File.WriteAllText(_fx.L("a.txt"), left, leftEncoding ?? new UTF8Encoding(false));
        File.WriteAllText(_fx.R("a.txt"), right, new UTF8Encoding(false));
        var l = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("a.txt"))))!;
        var r = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.R("a.txt"))))!;
        var window = _fx.Track(new CompareWindow(l, r, vm.Tools) { Width = 1100, Height = 700, EditDelay = TimeSpan.FromMilliseconds(30) });
        window.AskSaveOnClose = _ => Task.FromResult(SaveChoice.Discard);
        window.Show();
        await WaitUntilAsync(() => window.Comparing is { IsCompleted: true });
        await SettleAsync(2);
        return window;
    }

    [AvaloniaFact]
    public async Task A_difference_is_copied_undone_redone_and_saved()
    {
        var window = await OpenAsync("one\r\ntwo\r\nthree\r\nfour\r\n", "one\ntwo!\nthree\nfour\nfive\n", Encoding.UTF8);
        Assert.Equal(2, window.BlockCount);

        // the first difference to the right, the second (an added line) to the left
        await window.CopyBlockAsync(0, toRight: true);
        Assert.Equal(["one", "two", "three", "four", "five"], window.RightLines);
        Assert.Equal(1, window.BlockCount);
        Assert.True(window.RightModified);
        Assert.False(window.LeftModified);
        Assert.Contains("*", window.Title, StringComparison.Ordinal);
        await window.CopyBlockAsync(0, toRight: false);
        Assert.Equal(["one", "two", "three", "four", "five"], window.LeftLines);
        Assert.Equal(0, window.BlockCount);

        // undo both steps, redo one
        await window.UndoAsync();
        Assert.Equal(["one", "two", "three", "four"], window.LeftLines);
        await window.UndoAsync();
        Assert.Equal(["one", "two!", "three", "four", "five"], window.RightLines);
        Assert.Equal(2, window.BlockCount);
        Assert.False(window.RightModified);
        await window.RedoAsync();
        Assert.Equal(["one", "two", "three", "four", "five"], window.RightLines);

        // saved as each file was: the left one with its BOM and CRLF, the right one LF without BOM
        await window.CopyBlockAsync(0, toRight: false);
        Assert.True(await window.SaveAllAsync());
        Assert.False(window.LeftModified || window.RightModified);
        var leftBytes = File.ReadAllBytes(_fx.L("a.txt"));
        Assert.Equal([0xEF, 0xBB, 0xBF], leftBytes.Take(3));
        Assert.Equal("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\n", Encoding.UTF8.GetString(leftBytes, 3, leftBytes.Length - 3));
        Assert.Equal("one\ntwo\nthree\nfour\nfive\n", File.ReadAllText(_fx.R("a.txt")));
    }

    [AvaloniaFact]
    public async Task Typing_in_a_side_compares_again_and_is_one_undo_step()
    {
        var window = await OpenAsync("alpha\nbeta\ngamma\n", "alpha\nBETA\ngamma\n");
        Assert.Equal(1, window.BlockCount);
        var editor = window.RightEditor;
        var line = editor.Document.GetLineByNumber(2);
        editor.Document.Replace(line.Offset, line.Length, "beta");
        Assert.Equal(1, window.UndoCount);
        await WaitUntilAsync(() => window.BlockCount == 0);
        Assert.Equal(["alpha", "beta", "gamma"], window.RightLines);
        Assert.True(window.RightModified);
        await window.UndoAsync();
        Assert.Equal(["alpha", "BETA", "gamma"], window.RightLines);
        Assert.Equal(1, window.BlockCount);
    }

    [AvaloniaFact]
    public async Task Filler_lines_are_kept_while_editing()
    {
        var window = await OpenAsync("a\nb\nc\n", "a\nc\n");
        // the right side shows "a", a filler for "b", "c"
        var document = window.RightEditor.Document;
        Assert.Equal(3, document.LineCount);
        var guard = window.RightEditor.TextArea.ReadOnlySectionProvider;
        var filler = document.GetLineByNumber(2);
        Assert.False(guard.CanInsert(filler.Offset));
        Assert.True(guard.CanInsert(document.GetLineByNumber(1).EndOffset)); // at the end of "a"
        // joining "a" with the filler line is refused; deleting the whole line "a" above it is not
        Assert.Empty(guard.GetDeletableSegments(new TextSegment { StartOffset = document.GetLineByNumber(1).EndOffset, Length = 1 }));
        Assert.NotEmpty(guard.GetDeletableSegments(new TextSegment { StartOffset = 0, Length = filler.Offset }));

        // typing on a real line: the file's lines leave the filler out
        document.Insert(document.GetLineByNumber(3).EndOffset, "!");
        await window.CommitAndCompareAsync();
        Assert.Equal(["a", "c!"], window.RightLines);
    }

    [AvaloniaFact]
    public async Task Closing_asks_about_unsaved_changes_and_saves()
    {
        var window = await OpenAsync("x\n", "y\n");
        await window.CopyBlockAsync(0, toRight: true);
        var asked = 0;
        window.AskSaveOnClose = _ =>
        {
            asked++;
            return Task.FromResult(SaveChoice.Save);
        };
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
        Assert.Equal(1, asked);
        Assert.Equal("x\n", File.ReadAllText(_fx.R("a.txt")));
    }

    [AvaloniaFact]
    public async Task Sides_swap_formats_change_and_a_file_changed_elsewhere_is_loaded_again()
    {
        var window = await OpenAsync("left\n", "right\n");
        await window.SwapAsync();
        Assert.Equal(["right"], window.LeftLines);
        Assert.Equal(["left"], window.RightLines);

        // line ends of a side changed from its menu: the file is changed though its lines are not
        window.SetSideFormat(right: true, new UTF8Encoding(false), bom: false, "\r\n");
        Assert.True(window.RightModified);
        Assert.True(await window.SaveSideAsync(right: true));
        Assert.Equal("left\r\n", File.ReadAllText(_fx.L("a.txt")));

        // another program changes the (now left) right file: loaded again when there is nothing unsaved
        File.WriteAllText(_fx.R("a.txt"), "changed\n");
        File.SetLastWriteTimeUtc(_fx.R("a.txt"), DateTime.UtcNow.AddMinutes(1));
        await window.ExternalChangeAsync(window.SideOf(right: false));
        Assert.Equal(["changed"], window.LeftLines);

        // with unsaved changes it asks first
        await window.CopyBlockAsync(0, toRight: false);
        window.AskReload = _ => Task.FromResult(false);
        File.WriteAllText(_fx.R("a.txt"), "again\n");
        File.SetLastWriteTimeUtc(_fx.R("a.txt"), DateTime.UtcNow.AddMinutes(2));
        await window.ExternalChangeAsync(window.SideOf(right: false));
        Assert.Equal(["left"], window.LeftLines);
    }

    [AvaloniaFact]
    public async Task The_compare_settings_are_used_and_the_switches_keep_them()
    {
        // blank lines only: shown faintly, not a difference to visit
        var window = await OpenAsync("a\nb\n", "a\n\nb\n");
        Assert.Equal(1, window.BlockCount);
        var settings = window.Settings;
        settings.IgnoreBlankLines = true;
        await window.CompareAsync();
        Assert.Equal(0, window.BlockCount);
        Assert.Contains(window.Rows, r => r.Ignored);

        // line ends that count: CRLF against LF differs on every line
        await window.OpenSideAsync(right: true, await EntryAsync("crlf.txt", "a\r\nb\r\n"));
        Assert.Equal(0, window.BlockCount);
        settings.IgnoreLineEndings = false;
        await window.CompareAsync();
        Assert.Equal(1, window.BlockCount);
        Assert.Equal(["a", "b"], window.Rows.Where(r => r.Right is not null).Select(r => r.Right)); // shown without the line ends
        settings.IgnoreLineEndings = true;

        // moved lines
        await window.OpenSideAsync(right: true, await EntryAsync("moved.txt", "b\nfirst line\n"));
        await window.OpenSideAsync(right: false, await EntryAsync("moved0.txt", "first line\nb\n"));
        Assert.Contains(window.Rows, r => r.MovedTo is not null);

        // the switch of the bar is the setting
        var toggle = window.GetLogicalDescendants().OfType<Avalonia.Controls.ToggleSwitch>().First(t => Equals(t.OnContent, "Ignore case"));
        toggle.IsChecked = true;
        Assert.True(settings.IgnoreCase);
    }

    [AvaloniaFact]
    public async Task Saving_keeps_a_backup_when_asked()
    {
        var window = await OpenAsync("old\n", "new\n");
        window.Settings.Backup = true;
        await window.CopyBlockAsync(0, toRight: false);
        Assert.True(await window.SaveSideAsync(right: false));
        Assert.Equal("old\n", File.ReadAllText(_fx.L("a.txt.bak")));
        Assert.Equal("new\n", File.ReadAllText(_fx.L("a.txt")));
        // only the first save keeps the original
        await window.UndoAsync();
        Assert.True(await window.SaveSideAsync(right: false));
        Assert.Equal("old\n", File.ReadAllText(_fx.L("a.txt.bak")));
    }

    private async Task<VfsEntry> EntryAsync(string name, string text)
    {
        File.WriteAllText(_fx.R(name), text);
        return (await _vm!.Left.GetEntryAsync(VfsPath.Local(_fx.R(name))))!;
    }

    [AvaloniaFact]
    public async Task Go_to_line_finds_a_file_line()
    {
        var window = await OpenAsync(string.Concat(Enumerable.Range(1, 200).Select(i => $"line {i}\n")), string.Concat(Enumerable.Range(1, 200).Where(i => i != 50).Select(i => $"line {i}\n")));
        // the right file has no "line 50": its line 120 is "line 121"
        window.GoToLine(right: true, 120);
        var caret = window.RightEditor.TextArea.Caret;
        Assert.Equal("line 121", window.RightEditor.Document.GetText(window.RightEditor.Document.GetLineByNumber(caret.Line)));
        window.GoToLine(right: false, 120);
        caret = window.LeftEditor.TextArea.Caret;
        Assert.Equal("line 120", window.LeftEditor.Document.GetText(window.LeftEditor.Document.GetLineByNumber(caret.Line)));
    }
}
