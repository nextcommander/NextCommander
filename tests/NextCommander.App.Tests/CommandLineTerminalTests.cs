using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Command line drop-down of the last commands and the internal terminal.</summary>
public sealed class CommandLineTerminalTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Command_line_drop_down_lists_the_last_20_commands()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.CommandLineHistory = Enumerable.Range(1, 30).Select(i => $"komut {i}").ToList();
        await vm.ExecuteAsync("nc_ShowCmdLineHistory");
        await SettleAsync();
        Assert.True(w.FindControl<Avalonia.Controls.Primitives.Popup>("CommandHistoryPopup")!.IsOpen);
        var items = ((IEnumerable<string>)w.FindControl<Avalonia.Controls.ListBox>("CommandHistoryList")!.ItemsSource!).ToList();
        Assert.Equal(20, items.Count);
        Assert.Equal("komut 1", items[0]);
        w.FindControl<Avalonia.Controls.ListBox>("CommandHistoryList")!.SelectedIndex = 2;
        w.FindControl<Avalonia.Controls.ListBox>("CommandHistoryList")!.RaiseEvent(new Avalonia.Input.TappedEventArgs(Avalonia.Input.InputElement.TappedEvent, null!));
        Assert.False(w.FindControl<Avalonia.Controls.Primitives.Popup>("CommandHistoryPopup")!.IsOpen);
        Assert.Equal("komut 3", vm.CommandLineText);
    }

    private static int CountOf(string text, string part) => text.Split(part).Length - 1;

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task Terminal_button_opens_a_shell_in_the_panel_folder()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.TerminalEmulation = false; // the line console (the emulated terminal: TerminalEmulationTests)
        vm.Settings.TerminalTabs = [];
        await vm.ExecuteAsync("nc_ToggleTerminal");
        await SettleAsync();
        var terminal = w.Terminal!;
        Assert.True(w.FindControl<Avalonia.Controls.Border>("TerminalHost")!.IsVisible);
        Assert.NotNull(terminal.Session);
        // the command line makes room for the terminal, which takes a good part of the window
        Assert.False(vm.ShowCommandLine);
        Assert.False(w.FindControl<Avalonia.Controls.Grid>("CommandLineRow")!.IsVisible);
        Assert.True(terminal.Height > w.Bounds.Height * 0.3);
        await WaitForAsync(() => terminal.InputStart > 0); // the prompt
        terminal.Run("echo merhaba-terminal");
        await WaitForAsync(() => CountOf(terminal.OutputText, "merhaba-terminal") >= 2);
        // the command is shown once (behind the prompt), then its output
        Assert.Equal(1, CountOf(terminal.OutputText, "echo merhaba-terminal"));
        Assert.Contains("merhaba-terminal\n", terminal.OutputText.Replace("echo merhaba-terminal", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        // started in the folder of the active panel
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(vm.ActivePanel.CurrentPath!.Path.TrimEnd('\\'), terminal.OutputText, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal("dir", Views.TerminalView.Clean("\u001b[32mdir\u001b[0m\r"));

        // typed right behind the prompt; the output before it cannot be changed
        await WaitForAsync(() => terminal.OutputText.EndsWith('>') || terminal.OutputText.EndsWith("$ ", StringComparison.Ordinal));
        var outputEnd = terminal.InputStart;
        terminal.Console!.Focus();
        terminal.Console!.CaretIndex = 2; // in the output
        w.KeyTextInput("echo ikinci");
        Assert.Equal("echo ikinci", terminal.CurrentInput);
        Assert.Equal(outputEnd, terminal.InputStart);
        terminal.Console!.CaretIndex = terminal.InputStart;
        w.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Assert.Equal("echo ikinci", terminal.CurrentInput);
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitForAsync(() => CountOf(terminal.OutputText, "ikinci") >= 2);
        Assert.Equal(string.Empty, terminal.CurrentInput);

        // resized by dragging: the part of the window is kept when the window changes its size
        terminal.ResizeTo(w.Bounds.Height / 4);
        terminal.FitWindow(w.Bounds.Height * 2);
        Assert.Equal(w.Bounds.Height / 2, terminal.Height, 1.0);
        terminal.FitWindow(w.Bounds.Height);

        // tabs: another shell, closing it goes back to the first one
        Assert.NotEmpty(Services.TerminalSession.AvailableShells());
        var first = terminal.ActiveConsole!;
        var second = (Views.TerminalConsole)terminal.NewTab(Services.TerminalSession.AvailableShells()[^1]);
        Assert.Equal(2, terminal.Consoles.Count);
        Assert.Same(second, terminal.ActiveConsole);
        await WaitForAsync(() => second.InputStart > 0);
        terminal.CloseTab(second);
        Assert.Same(first, terminal.ActiveConsole);
        Assert.Single(terminal.Consoles);

        // focus in the terminal: the command hides it, the shell keeps running and the command line comes back
        terminal.Console!.Focus();
        await vm.ExecuteAsync("nc_ToggleTerminal");
        Assert.False(w.FindControl<Avalonia.Controls.Border>("TerminalHost")!.IsVisible);
        Assert.False(terminal.Session!.HasExited);
        Assert.True(vm.ShowCommandLine);
        terminal.Stop();
    }
}
