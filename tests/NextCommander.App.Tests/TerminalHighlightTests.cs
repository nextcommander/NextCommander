using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Words such as error, warning, done colored in the terminal's output (MobaXterm's syntax highlighting).</summary>
[Collection("Appearance")]
public sealed class TerminalHighlightTests
{
    private sealed class QuietTerminal : PseudoTerminal
    {
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

    [AvaloniaFact]
    public void Rules_match_whole_words_and_are_checked()
    {
        var rule = new HighlightRule { Words = "error, failed;denied", Color = "#E5484D" };
        var pattern = rule.Pattern()!;
        Assert.Equal(["ERROR", "failed", "denied"], pattern.Matches("ERROR: build failed (access denied), errorlevel, non-error").Select(m => m.Value));
        Assert.Null(new HighlightRule { Words = " , " }.Pattern());
        Assert.Equal(3, HighlightRule.Defaults().Count);
        Assert.Equal("No words", SettingsView.ValidateEntry(new HighlightRule { Words = "" }));
        Assert.StartsWith("Not a color", SettingsView.ValidateEntry(new HighlightRule { Words = "x", Color = "red-ish" }), StringComparison.Ordinal);
        Assert.Null(SettingsView.ValidateEntry(new HighlightRule { Words = "x", Color = "#112233" }));
    }

    [AvaloniaFact]
    public async Task The_words_are_colored_in_uncolored_output()
    {
        var red = Color.Parse("#E5484D");
        var amber = Color.Parse("#E2A400");
        var old = EmulatedTerminal.Highlights;
        try
        {
            EmulatedTerminal.Highlights = [.. HighlightRule.Defaults().Select(r => (r.Pattern()!, Color.Parse(r.Color)))];
            var view = new TerminalView();
            var window = new Window { Content = view, Width = 900, Height = 300 };
            window.Show();
            var tab = (EmulatedTerminal)view.NewTab(new TerminalSession.ShellInfo("t", "t", string.Empty, true, string.Empty), (_, _) => new QuietTerminal());
            tab.Screen.Feed("Build FAILED: 2 errors, 1 warning\r\n");
            tab.Screen.Feed("\u001b[32mdone\u001b[0m in 3 s; status ok\r\n");
            await SettleAsync();

            var first = TerminalScreen.HighlightsOf(tab.Screen.Line(0))!;
            Assert.Equal(red, first[6]); // FAILED
            Assert.Null(first[0]); // Build
            Assert.Equal(red, first[16]); // errors
            Assert.Equal(amber, first[26]); // warning
            var second = TerminalScreen.HighlightsOf(tab.Screen.Line(1))!;
            Assert.Equal(Color.Parse("#2E9E44"), second[20]); // ok (the program's own green "done" keeps its color when drawn)
            if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
            {
                Directory.CreateDirectory(shots);
                window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "terminal-highlight.png"));
            }

            EmulatedTerminal.Highlights = [];
            Assert.Null(TerminalScreen.HighlightsOf(tab.Screen.Line(0)));
            view.Stop();
            window.Close();
        }
        finally
        {
            EmulatedTerminal.Highlights = old;
        }
    }
}
