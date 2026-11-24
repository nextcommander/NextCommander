using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Automation;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Rules in the program (Commands > Rules; user's request, 23 November 2026).</summary>
[Collection("Appearance")]
public sealed class RulesUiTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Carrying_out_the_rules_moves_the_files_and_shows_the_log()
    {
        var (_, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_RunRules");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("no rule", StringComparison.Ordinal));
        Assert.Null(vm.Rules); // no rule: no watcher, no timer

        var target = _fx.R("texts");
        vm.Settings.Rules.Add(new FileRule { Name = "texts", Folder = _fx.L(), Mask = "*.txt", Target = target });
        await vm.ExecuteAsync("nc_RunRules");
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
        Assert.False(File.Exists(_fx.L("a.txt")));
        Assert.NotNull(vm.Rules);
        var shown = Assert.Single(_fx.Dialogs.LastLister!.Files);
        Assert.Equal(vm.RuleLog.Path, shown.Path.Path);
        Assert.Contains(vm.RuleLog.Read(), l => l.Contains("[texts] Moved", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_fields_of_a_rule_follow_its_kind()
    {
        var (_, vm) = await _fx.OpenAsync();
        var window = _fx.Track(new SettingsWindow(vm.Settings, () => { }, []) { Width = 1100, Height = 760 });
        window.Show();
        window.SelectPage("Operation/Rules");
        await SettleAsync(4);
        var editor = window.GetVisualDescendants().OfType<ListEditor<FileRule>>().Single();
        editor.Add(new FileRule { Name = "pdf", Folder = _fx.L(), Mask = "*.pdf", Target = _fx.R() });
        await SettleAsync();
        Assert.NotNull(editor.FieldEditor("Files"));
        Assert.Null(editor.FieldEditor("At (time of day)"));
        Assert.True(window.CanApply());

        var kind = (ComboBox)editor.FieldEditor("What it does")!;
        kind.SelectedIndex = 2; // synchronize every day
        await SettleAsync();
        Assert.Null(editor.FieldEditor("Files"));
        var at = (TextBox)editor.FieldEditor("At (time of day)")!;
        at.Text = "soon";
        await SettleAsync();
        Assert.False(window.CanApply());
        at.Text = "3:15";
        await SettleAsync();
        Assert.True(window.CanApply());
        window.Apply();
        var rule = Assert.Single(vm.Settings.Rules);
        Assert.Equal((FileRuleKind.Synchronize, "3:15"), (rule.Kind, rule.At));
    }
}
