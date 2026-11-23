using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The terminal's new tab buttons follow the tabs; files dropped on it are uploaded (SSH) or their paths typed (local).</summary>
public sealed class TerminalDropTests
{
    private sealed class RecordingTerminal : PseudoTerminal
    {
        public StringBuilder Written { get; } = new();

        public override void Write(byte[] data) => Written.Append(Encoding.UTF8.GetString(data));

        public override void Resize(int columns, int rows)
        {
        }

        public override void Dispose()
        {
        }
    }

    private static TerminalSession.ShellInfo Shell(string name) => new(name, name, string.Empty, true, string.Empty);

    [AvaloniaFact]
    public async Task The_new_tab_buttons_follow_the_tabs_and_stay_before_the_actions()
    {
        var view = new TerminalView();
        var window = new Window { Content = view, Width = 1200, Height = 500 };
        window.Show();
        view.NewTab(Shell("one"), (_, _) => new RecordingTerminal());
        await SettleAsync();
        var add = view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "+"));
        var tabs = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Content is StackPanel);
        double Right(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width, 0), window)!.Value.X;
        double Left(Visual v) => v.TranslatePoint(default, window)!.Value.X;
        // one tab: the buttons a short gap after it, far from the actions at the right
        Assert.InRange(Left(add) - Right(tabs), 0, 12);
        Assert.True(Left(add) < 600, $"+ at {Left(add)}");

        // many tabs: they scroll, the buttons stay at the right end of the room, before the actions
        for (var i = 0; i < 30; i++)
        {
            view.NewTab(Shell("tab " + i), (_, _) => new RecordingTerminal());
        }
        await SettleAsync();
        var actions = view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Tag, "log"));
        Assert.True(Right(add) <= Left(actions), $"+ ends at {Right(add)}, the actions start at {Left(actions)}");
        Assert.True(Left(add) > 600, $"+ at {Left(add)}");
        window.Close();
    }

    [AvaloniaFact]
    public void Files_dropped_on_a_local_shell_are_typed_as_paths()
    {
        var view = new TerminalView();
        var terminal = new RecordingTerminal();
        view.NewTab(Shell("local"), (_, _) => terminal);
        var spaced = OperatingSystem.IsWindows() ? @"C:\my files\a.txt" : "/home/u/my files/a.txt";
        var plain = OperatingSystem.IsWindows() ? @"C:\data\b.txt" : "/data/b.txt";
        view.DropForTest([], [spaced, plain], null);
        var quoted = OperatingSystem.IsWindows() ? $"\"{spaced}\"" : $"'{spaced}'";
        Assert.Contains($"{quoted} {plain} ", terminal.Written.ToString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Files_dropped_on_an_ssh_session_are_uploaded_into_its_folder()
    {
        var view = new TerminalView();
        var ssh = new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Ssh, Host = "web.example.com", User = "deploy" };
        view.NewTab(Shell("web"), (_, _) => new RecordingTerminal(), ssh);
        TerminalDrop? asked = null;
        view.UploadRequested += (_, drop) => asked = drop;
        view.DropForTest([], ["C:/a.txt"], null);
        Assert.NotNull(asked);
        Assert.Same(ssh, asked!.Connection);
        Assert.Equal(["C:/a.txt"], asked.LocalPaths);

        // the window's side: the files go to the session's computer over SFTP
        using var fx = new UiFixture();
        var (_, vm) = await fx.OpenAsync();
        File.WriteAllText(fx.L("upload.txt"), "x");
        VfsPath? target = null;
        vm.UploadStarted += (_, path) => target = path;
        // (the test window has no SFTP provider: the copy itself stops there)
        await Record.ExceptionAsync(() => vm.UploadToSshAsync(new TerminalDrop(ssh, "/var/www", [], [fx.L("upload.txt")], null)));
        Assert.NotNull(target);
        Assert.Equal("sftp", target!.Scheme);
        Assert.Equal("/var/www", target.Path.TrimEnd('/'));
        vm.Dispose();
    }
}
