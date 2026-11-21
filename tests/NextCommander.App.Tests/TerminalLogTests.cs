using System.Text;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using NextCommander.Core.Config;
using NextCommander.Core.Text;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The output of a terminal tab written to a text file.</summary>
public sealed class TerminalLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fm-tlog-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>A terminal whose output the test writes.</summary>
    private sealed class FakeTerminal : PseudoTerminal
    {
        public void Say(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            RaiseOutput(bytes, bytes.Length);
        }

        public override void Write(byte[] data)
        {
        }

        public override void Resize(int columns, int rows)
        {
        }

        public override void Dispose()
        {
        }
    }

    private static string Body(string file) => string.Join('\n', File.ReadAllText(file).Split('\n').Where(l => !l.StartsWith("=====", StringComparison.Ordinal)));

    [AvaloniaFact]
    public void The_log_is_plain_text_even_when_sequences_are_split()
    {
        using (var log = TerminalLog.Start(_folder, "web: prod/1", new DateTime(2026, 11, 21, 9, 30, 5)))
        {
            Assert.Equal(Path.Combine(_folder, "web_ prod_1 2026-11-21 093005.log"), log.Path);
            log.Write("\u001b]0;deploy@web: ~\u0007\u001b[01;32mdeploy@web\u001b[00m:~$ ls\r\n");
            log.Write("a.txt\tb\u001b[0"); // a sequence split between two writes
            log.Write("1;34mdir\u001b[0m\r\n");
            log.Write("10%\r20%\r\n\u001b(B\u001b=bell\u0007 done\u001bP+q544e\u001b\\\n");
        }
        Assert.Equal("deploy@web:~$ ls\na.txt\tbdir\n10%\n20%\nbell done\n\n", Body(Path.Combine(_folder, "web_ prod_1 2026-11-21 093005.log")));
    }

    [AvaloniaFact]
    public void The_log_button_writes_the_tab_output_to_a_file()
    {
        var terminal = new FakeTerminal();
        var view = new TerminalView { LogFolder = _folder };
        var connection = new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Ssh, Host = "web.example.com" };
        var tab = (EmulatedTerminal)view.NewTab(new TerminalSession.ShellInfo("web", "ssh", string.Empty, true, string.Empty), (_, _) => terminal, connection);
        Assert.False(view.IsLogging);
        terminal.Say("before\r\n");

        view.ToggleLog();
        Assert.True(view.IsLogging);
        var file = tab.Log!.Path;
        Assert.StartsWith(Path.Combine(_folder, "web "), file, StringComparison.Ordinal);
        terminal.Say("\u001b[1mhello\u001b[0m\r\n");
        view.ToggleLog();
        Assert.False(view.IsLogging);
        terminal.Say("after\r\n");
        Assert.Equal("hello\n\n", Body(file));

        // "every tab" opens new tabs with a log; closing the tab ends it
        view.LogAll = true;
        var other = view.NewTab(new TerminalSession.ShellInfo("web", "ssh", string.Empty, true, string.Empty), (_, _) => new FakeTerminal(), connection);
        Assert.True(view.IsLogging);
        Assert.NotNull(other.Log);
        view.CloseTab(other);
        Assert.Null(other.Log);

        string? asked = null;
        view.LogFolderRequested += (_, folder) => asked = folder;
        view.LogFolder = string.Empty;
        TerminalLog.DefaultFolder = _folder;
        Assert.Equal(_folder, view.LogFolderPath);
        view.Stop();
        Assert.Null(asked);
    }
}
