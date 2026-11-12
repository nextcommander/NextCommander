using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

public sealed class MainWindowTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Panels_ListDirectoriesFirst_InNaturalOrder()
    {
        var (_, vm) = await _fx.OpenAsync();
        var names = vm.Left.Items.Select(i => i.FullName).ToArray();
        Assert.Equal(["..", "sub", "a.txt", "b.log", "file2.txt", "file10.txt"], names);
        Assert.True(vm.Left.IsActive);
        Assert.Contains("0 / 4 files, 0 / 1 folders", vm.Left.StatusText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Keyboard_EnterBackspace_Navigate_AndCursorReturnsToFolder()
    {
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.Down);
        Assert.Equal("sub", vm.Left.CursorItem?.FullName);
        Press(w, Key.Enter);
        await SettleAsync();
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);

        Press(w, Key.Back);
        await SettleAsync();
        Assert.Equal(_fx.L(), vm.Left.CurrentPath!.Path);
        Assert.Equal("sub", vm.Left.CursorItem?.FullName);
    }

    [AvaloniaFact]
    public async Task Keyboard_InsertMarks_TabSwitchesPanel()
    {
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.Down);
        Press(w, Key.Down);
        Press(w, Key.Insert);
        Press(w, Key.Insert);
        Assert.Equal(["a.txt", "b.log"], vm.Left.Items.Where(i => i.IsMarked).Select(i => i.FullName).ToArray());
        Assert.Equal("file2.txt", vm.Left.CursorItem?.FullName);
        Assert.Contains("2 / 4 files", vm.Left.StatusText, StringComparison.Ordinal);

        Press(w, Key.Tab);
        Assert.True(vm.Right.IsActive);
        Assert.False(vm.Left.IsActive);
    }

    [AvaloniaFact]
    public async Task NumPlus_MarksByPattern()
    {
        var (w, vm) = await _fx.OpenAsync();
        _fx.Dialogs.NextPrompt = "*.txt";
        Press(w, Key.Add);
        await SettleAsync();
        Assert.Equal(["a.txt", "file2.txt", "file10.txt"], vm.Left.Items.Where(i => i.IsMarked).Select(i => i.FullName).ToArray());
    }

    [AvaloniaFact]
    public async Task F5_CopiesMarkedFilesToOtherPanel()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Left.MarkByPattern("*.txt", mark: true);
        Press(w, Key.F5);
        await WaitUntilAsync(() => File.Exists(_fx.R("file10.txt")));
        Assert.True(File.Exists(_fx.R("a.txt")));
        Assert.True(File.Exists(_fx.R("file2.txt")));
        Assert.True(File.Exists(_fx.R("file10.txt")));
        Assert.False(File.Exists(_fx.R("b.log")));
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), _fx.Dialogs.LastCopyTarget, StringComparison.Ordinal);
        Assert.Contains(_fx.Dialogs.LastSuggestions, s => s.StartsWith(_fx.R(), StringComparison.OrdinalIgnoreCase));
        await SettleAsync();
        Assert.Contains(vm.Right.Items, i => i.FullName == "file10.txt");
        Assert.DoesNotContain(vm.Left.Items, i => i.IsMarked);
        Assert.Equal(_fx.R() + Path.DirectorySeparatorChar, vm.Settings.CopyTargetHistory[0]);
    }

    [AvaloniaFact]
    public async Task F7_CreatesNestedDirectory_AndShiftDeleteRemovesIt()
    {
        var (w, vm) = await _fx.OpenAsync();
        _fx.Dialogs.NextPrompt = "x/y";
        Press(w, Key.F7);
        await SettleAsync();
        Assert.True(Directory.Exists(_fx.L("x", "y")));
        Assert.Equal("x", vm.Left.CursorItem?.FullName);

        Press(w, Key.Delete, RawInputModifiers.Shift);
        await SettleAsync();
        Assert.False(Directory.Exists(_fx.L("x")));
    }

    [AvaloniaFact]
    public async Task TypingGoesToCommandLine_AndCdNavigates()
    {
        var (w, vm) = await _fx.OpenAsync();
        w.KeyTextInput("cd sub");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("cd sub", vm.CommandLineText);
        var commandLine = w.FindControl<TextBox>("CommandLine")!;
        Assert.True(commandLine.IsKeyboardFocusWithin);
        Press(w, Key.Enter);
        await SettleAsync();
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);
        Assert.Equal(string.Empty, vm.CommandLineText);
    }

    [AvaloniaFact]
    public async Task SortBySize_BiggestFirst_TiesByName()
    {
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.F6, RawInputModifiers.Control);
        var files = vm.Left.Items.Where(i => !i.IsDirectory).Select(i => i.FullName).ToArray();
        Assert.Equal(["b.log", "file10.txt", "a.txt", "file2.txt"], files);
        Press(w, Key.F3, RawInputModifiers.Control);
        Assert.Equal(SortColumn.Name, vm.Left.SortColumn);
    }
}
