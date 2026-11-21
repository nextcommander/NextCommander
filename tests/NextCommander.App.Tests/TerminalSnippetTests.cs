using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Saved commands sent to the terminal with a click (MobaXterm's macros).</summary>
public sealed class TerminalSnippetTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private sealed class RecordingTerminal : PseudoTerminal
    {
        public StringBuilder Sent { get; } = new();

        public override void Write(byte[] data) => Sent.Append(Encoding.UTF8.GetString(data));

        public override void Resize(int columns, int rows)
        {
        }

        public override void Dispose()
        {
        }
    }

    [AvaloniaFact]
    public async Task A_snippet_is_typed_into_the_active_tab_and_with_multiexec_into_the_others()
    {
        var view = new TerminalView();
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        var first = new RecordingTerminal();
        var second = new RecordingTerminal();
        view.NewTab(new TerminalSession.ShellInfo("a", "a", string.Empty, true, string.Empty), (_, _) => first);
        var b = view.NewTab(new TerminalSession.ShellInfo("b", "b", string.Empty, true, string.Empty), (_, _) => second);
        await SettleAsync();

        view.SendSnippet(new TerminalSnippet { Name = "Disk", Command = "df -h" });
        Assert.Equal("df -h\r", second.Sent.ToString());
        Assert.Equal(string.Empty, first.Sent.ToString());
        view.SendSnippet(new TerminalSnippet { Name = "Grep", Command = "grep -i error ", Enter = false });
        Assert.Equal("df -h\rgrep -i error ", second.Sent.ToString());

        view.MultiExec = true;
        view.Select(b);
        second.Sent.Clear();
        view.SendSnippet(new TerminalSnippet { Command = "uptime" });
        Assert.Equal(("uptime\r", "uptime\r"), (first.Sent.ToString(), second.Sent.ToString()));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_list_is_kept_in_the_settings_and_edited_from_the_terminal()
    {
        Assert.Equal("Command is empty", SettingsView.ValidateEntry(new TerminalSnippet { Name = "x" }));
        Assert.Null(SettingsView.ValidateEntry(new TerminalSnippet { Command = "ls" }));
        Assert.Equal("ls -la\r", new TerminalSnippet { Command = "ls -la" }.Text);

        var (_, vm) = await _fx.OpenAsync();
        await vm.EditTerminalSnippetsAsync();
        Assert.Equal("Saved commands", _fx.Dialogs.LastSettingsSearch);
    }
}
