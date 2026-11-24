using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Macro recording and JavaScript scripts (Commands menu; user's request, 24 November 2026).</summary>
public sealed class AutomationTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task A_recorded_macro_becomes_a_user_command()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_RecordMacro");
        Assert.True(vm.IsRecordingMacro);
        w.UpdateTitle();
        Assert.StartsWith("●", w.Title, StringComparison.Ordinal);
        await vm.ExecuteAsync("nc_SrcByExt");
        await vm.ExecuteAsync("nc_PanelsSplitPercent 40");
        _fx.Dialogs.NextPrompt = "Sorted view";
        await vm.ExecuteAsync("nc_RecordMacro"); // stop: a name with a space is refused, the next prompt is empty
        Assert.False(vm.IsRecordingMacro);
        Assert.Empty(vm.Settings.UserCommands); // the second prompt was cancelled

        await vm.ExecuteAsync("nc_RecordMacro");
        await vm.ExecuteAsync("nc_SrcByExt");
        await vm.ExecuteAsync("nc_PanelsSplitPercent 40");
        _fx.Dialogs.NextPrompt = "SortedView";
        await vm.ExecuteAsync("nc_RecordMacro");
        var macro = vm.Settings.UserCommands["uc_SortedView"];
        Assert.Equal("nc_SrcByExt, nc_PanelsSplitPercent 40", macro.Command);
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("uc_SortedView", StringComparison.Ordinal));

        // the macro runs; a macro run while recording is one step, not its commands
        vm.Left.SetSort(Core.Config.SortColumn.Name);
        await vm.ExecuteAsync("nc_RecordMacro");
        await vm.ExecuteAsync("uc_SortedView");
        Assert.Equal(Core.Config.SortColumn.Extension, vm.Left.SortColumn);
        Assert.Equal(["uc_SortedView"], vm.RecordedMacro);
        _fx.Dialogs.NextPrompt = null;
        await vm.ExecuteAsync("nc_RecordMacro");

        await vm.ExecuteAsync("nc_RecordMacro");
        await vm.ExecuteAsync("nc_RecordMacro"); // nothing recorded
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("nothing to save", StringComparison.Ordinal));
        Assert.Null(ViewModels.MainWindowViewModel.MacroNameProblem("uc_Fine-1"));
        Assert.NotNull(ViewModels.MainWindowViewModel.MacroNameProblem("not fine"));
    }

    [AvaloniaFact]
    public async Task A_script_uses_the_panels_the_dialogs_and_the_files()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_RunScript");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("no script yet", StringComparison.Ordinal));

        Directory.CreateDirectory(vm.ScriptsFolder);
        File.WriteAllText(Path.Combine(vm.ScriptsFolder, "report.js"), """
            var p = nc.panel();
            var name = nc.prompt("Name of the report:", "report.txt");
            if (name !== null && nc.confirm("Write " + p.files.length + " names?")) {
              nc.writeText(nc.otherPanel().path + "/" + name, nc.args + ":" + p.files.join(","));
              nc.run("nc_RereadSource");
              nc.message("done " + nc.exists(nc.otherPanel().path + "/" + name));
            }
            nc.log("ran");
            """);
        _fx.Dialogs.NextPrompt = "out.txt";
        vm.ActivateSide(vm.LeftSide);
        await vm.ExecuteAsync("nc_RunScript report hello");
        var written = File.ReadAllText(_fx.R("out.txt"));
        Assert.StartsWith("hello:", written, StringComparison.Ordinal);
        Assert.Contains("a.txt", written, StringComparison.Ordinal);
        Assert.Contains("done true", _fx.Dialogs.Messages);
        Assert.Contains("ran", File.ReadAllText(Path.Combine(_fx.Root, "settings", "logs", "scripts.log")), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(vm.ScriptsFolder, "broken.js"), "var a = 1;\nundefinedThing();\n");
        await vm.ExecuteAsync("nc_RunScript broken.js");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("broken.js: line 2", StringComparison.Ordinal));
        await vm.ExecuteAsync("nc_RunScript missing");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("No script found: missing", StringComparison.Ordinal));

        w.FillScriptsMenu();
        var menu = w.FindControl<MenuItem>("ScriptsMenu")!;
        Assert.Contains(menu.Items.OfType<MenuItem>(), m => m.Header as string == "report.js" && m.Classes.Contains("noloc"));
    }

    [AvaloniaFact]
    public async Task New_scripts_start_from_examples_that_work()
    {
        var (_, vm) = await _fx.OpenAsync();
        _fx.Dialogs.NextPrompt = "list";
        await vm.ExecuteAsync("nc_NewScript");
        var script = Path.Combine(vm.ScriptsFolder, "list.js");
        Assert.True(File.Exists(script));
        Assert.Equal(VfsPathOf(vm.ScriptsFolder), vm.ActivePanel.CurrentPath);

        _fx.Dialogs.NextPrompt = "lines";
        await vm.ExecuteAsync("nc_NewColumnScript");
        var column = Core.Scripting.ScriptColumnProvider.Load(Path.Combine(vm.ScriptColumnsFolder, "lines.js"),
            File.ReadAllText(Path.Combine(vm.ScriptColumnsFolder, "lines.js")));
        Assert.Equal("js_lines.lines", Assert.Single(column.Fields).Id);

        // the command example: nothing selected but the cursor's file; answered "no"
        await vm.Left.NavigateAsync(VfsPathOf(vm.ScriptsFolder)); // the column script took the panel to its folder
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "list.js");
        _fx.Dialogs.ConfirmAnswer = false;
        await vm.ExecuteAsync("nc_RunScript list.js");
        Assert.Single(_fx.Dialogs.ConfirmMessages);
    }

    private static Core.Vfs.VfsPath VfsPathOf(string path) => Core.Vfs.VfsPath.Local(path);
}
