using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Views.Dialogs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Commands are chosen from a list instead of typed: the picker and the screens that offer it.</summary>
public sealed class CommandPickerTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static List<Button> CommandButtons(Control root) =>
        [.. root.GetLogicalDescendants().OfType<Button>().Where(b => b.Content as string == "Command...")];

    [AvaloniaFact]
    public async Task The_picker_lists_searches_and_chooses_commands()
    {
        var (_, vm) = await _fx.OpenAsync();
        var picker = _fx.Track(new CommandPickerDialog());
        picker.Show();
        Assert.True(picker.Shown.Count > 50, picker.Shown.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(vm.Commands.All.Count, picker.Shown.Count);

        picker.SearchText = "nc_copy";
        Assert.Contains(picker.Shown, e => e.Name == "nc_Copy");
        Assert.All(picker.Shown, e => Assert.True(e.Name.Contains("copy", StringComparison.OrdinalIgnoreCase)
            || e.Description.Contains("copy", StringComparison.OrdinalIgnoreCase)));
        picker.SearchText = "Settings"; // by the description
        Assert.Contains(picker.Shown, e => e.Name == "nc_Config");

        picker.SearchText = "nc_RenMov";
        picker.SelectedIndex = picker.Shown.ToList().FindIndex(e => e.Name == "nc_RenMov");
        picker.Choose();
        Assert.Equal("nc_RenMov", picker.Result);

        // the command already in the field is selected when the list opens
        var again = _fx.Track(new CommandPickerDialog("*nc_SrcByName extra"));
        again.Show();
        Assert.Equal("nc_SrcByName", again.Shown[again.SelectedIndex].Name);
    }

    [AvaloniaFact]
    public async Task Command_fields_offer_the_picker()
    {
        await _fx.OpenAsync();

        var button = _fx.Track(new ButtonEditDialog(null, () => Task.FromResult<string?>(null)));
        button.Show();
        Assert.Single(CommandButtons(button));

        var bar = _fx.Track(new ButtonBarEditorDialog("Bar", [new Core.Config.ButtonDefinition { Command = "nc_Copy" }], 24, false,
            (_, _) => null, _ => Task.FromResult<string?>(null)));
        bar.Show();
        Assert.Single(CommandButtons(bar));

        var sets = new List<Core.Columns.ColumnSet>();
        var columns = _fx.Track(new ColumnSetDialog(sets, []));
        columns.Show();
        Assert.Equal(2, CommandButtons(columns).Count); // entering and leaving a view mode

        // the hotlist window: a command entry has the picker, a folder entry not
        var hotlist = _fx.Track(new HotlistEditorDialog([new() { Name = "Eşitle", Path = "nc_SyncDirs" }, new() { Name = "Proje", Path = _fx.L() }]));
        hotlist.Show();
        Assert.Single(CommandButtons(hotlist));
        hotlist.Select("Proje");
        Assert.Empty(CommandButtons(hotlist));
    }

    [AvaloniaFact]
    public async Task Settings_lists_with_commands_offer_the_picker()
    {
        var (_, vm) = await _fx.OpenAsync();
        var settings = vm.Settings;
        settings.Associations = [new Core.Config.FileAssociation { Mask = "*.log", Command = "editor" }];
        settings.StartMenu = [new Core.Config.ButtonDefinition { Label = "Notepad", Command = "notepad.exe" }];
        var window = _fx.Track(new SettingsWindow(settings, () => { }, []));
        window.Show();
        foreach (var page in new[] { "Operation/Associations", "Misc." })
        {
            window.SelectPage(page);
            await SettleAsync(4);
            Assert.True(CommandButtons(window).Count >= 1, page);
        }
    }
}
