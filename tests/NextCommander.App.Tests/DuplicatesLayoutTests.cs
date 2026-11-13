using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The result list of Duplicate files scrolls (also with scaled dialogs) and shows the icons of the files.</summary>
[Collection("Appearance")]
public sealed class DuplicatesLayoutTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
    }

    [AvaloniaTheory]
    [InlineData(100)]
    [InlineData(150)]
    public async Task Many_results_scroll(int scale)
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.DialogScalePercent = scale;
        vm.ApplySettings();
        try
        {
            var a = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("a.txt"))))!;
            var window = _fx.Track(new DuplicatesWindow(new DuplicatesRequest(vm.Tools, [_fx.L()], (_, _) => Task.CompletedTask, _ => Task.CompletedTask)));
            for (var g = 0; g < 150; g++)
            {
                window.Rows.Add(new DuplicateRow("Group " + g, g));
                window.Rows.Add(new DuplicateRow(a, g));
                window.Rows.Add(new DuplicateRow(a, g));
            }
            window.Show();
            await SettleAsync(4);
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, window.Rows));
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 100, $"extent {scroll.Extent.Height}, viewport {scroll.Viewport.Height}");
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Visible, ScrollViewer.GetVerticalScrollBarVisibility(list));
            Assert.False(ScrollViewer.GetAllowAutoHide(list)); // the bar stays, like the panels'
            // the list ends inside the window: its bottom is reachable
            var bottom = list.TranslatePoint(new Avalonia.Point(0, list.Bounds.Height), window)!.Value.Y;
            Assert.True(bottom <= window.ClientSize.Height + 1, $"list bottom {bottom}, window {window.ClientSize.Height}");
            // the Mark menu has icons
            window.SetMenuIcons();
            Assert.All(window.MarkMenu!.Items.OfType<MenuItem>(), i => Assert.True(i.Icon is Control, $"no icon: {i.Header}"));
            // file rows have the icon of their kind
            Assert.NotEmpty(list.GetVisualDescendants().OfType<ListBoxItem>().Skip(1).First().GetVisualDescendants().OfType<Viewbox>());
            if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
            {
                Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(dir, $"duplicates-scroll-{scale}.png"));
            }
        }
        finally
        {
            vm.Settings.DialogScalePercent = 100;
            vm.ApplySettings();
        }
    }
}
