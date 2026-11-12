using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using NextCommander.App.Views;
using NextCommander.Core.Columns;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > View modes: background, tab color, enter / exit commands.</summary>
public sealed class ConfigViewModeTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task View_mode_colors_and_commands()
    {
        Directory.CreateDirectory(_fx.L("photos"));
        var (w, vm) = await _fx.OpenAsync();
        var entered = 0;
        var left = 0;
        vm.Commands.Register("nc_TestEnter", "test", () => entered++);
        vm.Commands.Register("nc_TestExit", "test", () => left++);
        vm.Settings.ColumnSets.Add(new ColumnSet
        {
            Name = "Fotoğraf testi",
            AutoPathMask = "*photos",
            Columns = [new ColumnSpec { Field = "comment.text" }],
            Background = "#102030",
            TabColor = "#FF8800",
            EnterCommand = "nc_TestEnter",
            ExitCommand = "nc_TestExit",
        });

        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("photos")));
        await SettleAsync();
        Assert.Equal("Fotoğraf testi", vm.Left.ActiveColumnSet?.Name);
        Assert.Equal(1, entered);
        Assert.Equal(Color.Parse("#FF8800"), ((ISolidColorBrush)vm.Left.TabBrush!).Color);
        var panel = w.GetVisualDescendants().OfType<FilePanelView>().First(p => ReferenceEquals(p.DataContext, vm.Left));
        Assert.Equal(Color.Parse("#102030"), ((ISolidColorBrush)panel.Resources["FmListBackgroundBrush"]!).Color);

        // also in the brief and thumbnail views, which draw themselves
        foreach (var mode in new[] { PanelViewMode.Brief, PanelViewMode.Thumbnails })
        {
            vm.Left.ViewMode = mode;
            await SettleAsync();
            var list = panel.GetVisualDescendants().OfType<Control>()
                .First(c => c.IsEffectivelyVisible && c is Controls.BriefListControl or Controls.ThumbnailListControl);
            var corner = list.TranslatePoint(new Avalonia.Point(list.Bounds.Width - 3, list.Bounds.Height - 3), w)!.Value;
            Assert.Equal(Color.Parse("#102030"), PixelAt(w, corner));
        }
        vm.Left.ViewMode = PanelViewMode.Full;
        await SettleAsync();

        await vm.Left.GoUpAsync();
        await SettleAsync();
        Assert.Null(vm.Left.ActiveColumnSet);
        Assert.Equal(1, left);
        Assert.Null(vm.Left.TabBrush);
        Assert.False(panel.Resources.ContainsKey("FmListBackgroundBrush"));
    }

    /// <summary>The color of one pixel of the rendered window.</summary>
    private static Color PixelAt(Window window, Point point)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var scale = frame.PixelSize.Width / window.Bounds.Width;
        var x = (int)(point.X * scale);
        var y = (int)(point.Y * scale);
        unsafe
        {
            var pixel = ((uint*)buffer.Address)[(y * buffer.RowBytes / 4) + x];
            return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888
                ? Color.FromArgb((byte)(pixel >> 24), (byte)pixel, (byte)(pixel >> 8), (byte)(pixel >> 16))
                : Color.FromArgb((byte)(pixel >> 24), (byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel);
        }
    }
}
