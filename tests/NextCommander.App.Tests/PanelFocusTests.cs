using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;
using NextCommander.App.Views;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The focus stays in the panel after operations (a dialog closed: the system left it on the main menu).</summary>
public sealed class PanelFocusTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static bool InList(MainWindow w) => w.LeftPanel.GetVisualDescendants().OfType<DataGrid>().First().IsKeyboardFocusWithin;

    [AvaloniaFact]
    public async Task The_focus_left_on_the_main_menu_goes_back_to_the_panel()
    {
        var (w, _) = await _fx.OpenAsync();
        await SettleAsync();
        var file = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First();
        file.Focus(); // where the system put it after a dialog closed
        Assert.True(file.IsKeyboardFocusWithin);

        w.KeepFocusInPanel();
        Assert.True(InList(w));
    }

    [AvaloniaFact]
    public async Task The_command_line_keeps_its_focus()
    {
        var (w, _) = await _fx.OpenAsync();
        await SettleAsync();
        var commandLine = w.FindControl<TextBox>("CommandLine")!;
        commandLine.Focus();
        w.KeepFocusInPanel();
        Assert.True(commandLine.IsKeyboardFocusWithin);
    }
}
