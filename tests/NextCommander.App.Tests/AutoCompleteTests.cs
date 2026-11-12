using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Views;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Operation: auto-complete paths, add the suggested name.</summary>
public sealed class AutoCompleteTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public void Suggestions_are_the_matching_folders_and_files()
    {
        Directory.CreateDirectory(_fx.L("subdir2"));
        var sep = Path.DirectorySeparatorChar;
        var prefix = _fx.L() + sep;
        Assert.Equal([prefix + "sub" + sep, prefix + "subdir2" + sep], PathAutoComplete.SuggestionsFor(prefix + "SU", null, files: false));
        Assert.Contains(prefix + "a.txt", PathAutoComplete.SuggestionsFor(prefix + "a", null, files: true));
        Assert.Equal(["sub" + sep + "inner.txt"], PathAutoComplete.SuggestionsFor("sub" + sep + "in", _fx.L(), files: true)); // relative to the panel
        Assert.Empty(PathAutoComplete.SuggestionsFor("nc_Copy", _fx.L(), files: true)); // not a path
    }

    [AvaloniaFact]
    public async Task Path_line_and_command_line_complete_paths()
    {
        var (w, vm) = await _fx.OpenAsync();
        var sep = Path.DirectorySeparatorChar;
        var bar = w.LeftPanel.GetVisualDescendants().OfType<PathBarView>().First();
        bar.BeginEdit();
        await SettleAsync();
        var box = bar.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("pathbox"));
        box.Text = _fx.L() + sep;
        box.CaretIndex = box.Text.Length;
        w.KeyTextInput("s");
        await WaitForAsync(() => bar.Completion.IsOpen);
        Assert.Equal([_fx.L("sub") + sep], bar.Completion.Suggestions);
        w.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); // takes the suggestion, does not navigate yet
        Assert.Equal(_fx.L("sub") + sep, box.Text);
        Assert.True(bar.IsEditing);
        Assert.Equal(Core.Vfs.VfsPath.Local(_fx.L()), vm.Left.CurrentPath);
        bar.Completion.Close();

        // the command line: the last word, relative to the panel's folder, files too
        w.CommandLineCompletion.Close();
        vm.Settings.AutoCompleteAppend = true; // the first suggestion is written in, selected
        var commandLine = w.FindControl<TextBox>("CommandLine")!;
        commandLine.Focus();
        vm.CommandLineText = "notepad ";
        commandLine.CaretIndex = commandLine.Text!.Length;
        w.KeyTextInput("b");
        await Task.Delay(200);
        await SettleAsync();
        Assert.False(w.CommandLineCompletion.IsOpen); // "b" has no folder part: no completion (plain words stay words)
        Assert.Equal("notepad b", commandLine.Text);
        vm.CommandLineText = "notepad sub" + sep;
        commandLine.CaretIndex = commandLine.Text!.Length;
        w.KeyTextInput("i");
        await WaitForAsync(() => commandLine.Text == "notepad sub" + sep + "inner.txt");
        Assert.Equal("inner.txt".Length - 1, commandLine.SelectionEnd - commandLine.SelectionStart); // the added part is selected
        vm.Settings.AutoCompleteAppend = false;
        vm.Settings.AutoCompletePaths = false;
        vm.CommandLineText = string.Empty;
        w.KeyTextInput("x" + sep);
        await SettleAsync();
        Assert.False(w.CommandLineCompletion.IsOpen); // switched off
        vm.Settings.AutoCompletePaths = true;
    }
}
