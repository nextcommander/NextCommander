using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AvaloniaEdit;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The compare window of the design: counts, counter, side by side / inline, editing both files, the toast.</summary>
[Collection("Appearance")] // the screenshots switch the color theme
public sealed class CompareWindowTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private const string Original = """
        using Microsoft.AspNetCore.Builder;

        namespace Website;

        internal static class Program
        {
            private static void Main(string[] args)
            {
                var app = WebApplication.CreateBuilder(args).Build();
                app.MapGet("/", () => "Hello to the example website");
                    app.Run();

            }
        }
        """;

    private const string Current = """
        using Microsoft.AspNetCore.Builder;

        namespace Website.App;

        internal static class Program
        {
            private static void Main(string[] args)
            {
                var app = WebApplication.CreateBuilder(args).Build();
                app.MapGet("/", () => "Welcome to the example website");
                app.MapGet("/gallery", () => "photos");
                app.Run();
            }
        }
        """;

    private static T Find<T>(CompareWindow window, string tip) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().First(c => Equals(ToolTip.GetTip(c), tip));

    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static List<string?> Texts(Control control) => [.. control.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)];

    [AvaloniaFact]
    public async Task Counts_counter_views_and_editing_both_files()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("Program.cs"), Original);
        File.WriteAllText(_fx.R("Program.cs"), Current);
        var left = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("Program.cs"))))!;
        var right = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.R("Program.cs"))))!;
        var window = _fx.Track(new CompareWindow(left, right, vm.Tools) { Width = 1200, Height = 760 });
        window.Show();
        await WaitUntilAsync(() => window.Comparing is { IsCompleted: true });
        await SettleAsync(4);

        // the bar: the file, its type and the counts; the tags of the two files
        var texts = Texts(window);
        Assert.Contains("Program.cs", texts);
        Assert.Contains("C#", texts);
        Assert.Equal(2, window.BlockCount);
        Assert.Contains("2 changes", texts);
        Assert.Contains("Original", texts);
        Assert.Contains("Current", texts);
        Assert.Equal($"1 / {window.BlockCount}", window.CounterText);
        Assert.Contains("lines →", window.StatusText, StringComparison.Ordinal);
        Shot(window, "compare-light.png");

        // round from the last difference to the first
        for (var i = 1; i < window.BlockCount; i++)
        {
            window.MoveToBlock(+1);
        }
        Assert.Equal($"{window.BlockCount} / {window.BlockCount}", window.CounterText);
        window.MoveToBlock(+1);
        Assert.Equal(0, window.CurrentBlock);
        window.MoveToBlock(-1);
        Assert.Equal(window.BlockCount - 1, window.CurrentBlock);

        // one column
        Click(Find<ToggleButton>(window, "Inline"));
        await SettleAsync();
        Assert.True(window.IsInline);
        App.ApplyColorTheme("dark");
        await SettleAsync(4);
        Shot(window, "compare-inline-dark.png");
        App.ApplyColorTheme(null);
        Click(Find<ToggleButton>(window, "Side by side"));
        Assert.False(window.IsInline);

        // both sides are edited in place; every difference copied to the right makes them match
        var editors = window.GetLogicalDescendants().OfType<TextEditor>().Where(e => !e.IsReadOnly).ToList();
        Assert.Equal(2, editors.Count);
        await window.CopyAllAsync(toRight: true);
        Assert.Equal(0, window.BlockCount);
        Assert.True(window.RightModified);
        Click(Find<Button>(window, "Compare again (F5)"));
        await WaitUntilAsync(() => window.Comparing is { IsCompleted: true });
        Assert.Equal("0 / 0", window.CounterText);
        Assert.Equal("Compared · files match", window.LastToast);
        window.AskSaveOnClose = _ => Task.FromResult(Views.Editor.SaveChoice.Discard);
    }

    private static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, name));
        }
    }
}
