using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The internal terminal maximized over the panels and restored; the name of cmd.exe.</summary>
public sealed class TerminalMaximizeTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Maximized_terminal_starts_where_the_panels_start_and_restores()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.TerminalEmulation = false;
        vm.Settings.TerminalTabs = [];
        await vm.ExecuteAsync("nc_ToggleTerminal");
        await SettleAsync();
        var terminal = w.Terminal!;
        var panels = w.FindControl<Grid>("PanelsGrid")!;
        var host = w.FindControl<Border>("TerminalHost")!;
        var panelsTop = panels.TranslatePoint(new Avalonia.Point(0, 0), w)!.Value.Y;
        var normalHeight = terminal.Bounds.Height;

        await vm.ExecuteAsync("nc_MaximizeTerminal");
        await SettleAsync();
        Assert.True(terminal.IsMaximized);
        Assert.False(panels.IsVisible);
        // the terminal's frame starts at the top of the panels
        Assert.Equal(panelsTop, host.TranslatePoint(new Avalonia.Point(0, 0), w)!.Value.Y, 1);
        Assert.True(terminal.Bounds.Height > normalHeight);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "terminal-maximized.png"));
        }

        // a taller window: the terminal still reaches the top of the panels
        w.Height += 100;
        await SettleAsync();
        Assert.Equal(panelsTop, host.TranslatePoint(new Avalonia.Point(0, 0), w)!.Value.Y, 1);

        // the button in the title bar restores the size and the panels
        var button = terminal.GetVisualDescendants().OfType<Button>().Single(b => b.Tag as string == "maximize");
        Assert.Equal("❐", button.Content);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        Assert.False(terminal.IsMaximized);
        Assert.True(panels.IsVisible);
        Assert.Equal("□", button.Content);
        Assert.True(terminal.Bounds.Height < w.Bounds.Height * 0.9);

        // closing a maximized terminal brings the panels back
        await vm.ExecuteAsync("nc_MaximizeTerminal");
        await SettleAsync();
        Assert.False(panels.IsVisible);
        terminal.ActiveConsole!.Focus();
        await vm.ExecuteAsync("nc_ToggleTerminal");
        await SettleAsync();
        Assert.False(host.IsVisible);
        Assert.True(panels.IsVisible);
        Assert.False(terminal.IsMaximized);
        terminal.Stop();
    }

    [AvaloniaFact]
    public void Cmd_is_called_Command_Prompt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var cmd = TerminalSession.AvailableShells()[0];
        Assert.Equal("Command Prompt", cmd.Name);
        Assert.True(cmd.IsCommandPrompt);
        // "cmd" kept in the settings of earlier versions is still the Command Prompt
        Assert.Same(cmd, TerminalSession.FindShell("cmd"));
        Assert.Same(cmd, TerminalSession.ShellFor("cmd"));
        Assert.Equal("cd /d \"C:\\x\"", TerminalSession.ChangeDirectoryCommand(cmd, "C:\\x"));
    }
}
