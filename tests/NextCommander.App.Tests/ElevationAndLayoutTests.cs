using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The helper that runs operations with administrator rights (here without them), and the button bar editor of the Layout page.</summary>
public sealed class ElevationAndLayoutTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private string Serve(ElevatedRequest request)
    {
        var requestFile = _fx.L("request.json");
        var resultFile = _fx.L("result.json");
        File.WriteAllText(requestFile, JsonSerializer.Serialize(request));
        Elevation.Serve(requestFile, resultFile);
        return string.Join("; ", JsonSerializer.Deserialize<List<string>>(File.ReadAllText(resultFile))!);
    }

    [AvaloniaFact]
    public void The_elevated_helper_copies_moves_creates_renames_and_deletes()
    {
        Assert.Equal(string.Empty, Serve(new(ElevatedOperation.CreateFolder, [], _fx.R("made"))));
        Assert.True(Directory.Exists(_fx.R("made")));
        Assert.Equal(string.Empty, Serve(new(ElevatedOperation.Copy, [_fx.L("a.txt")], _fx.R("made"))));
        Assert.True(File.Exists(_fx.R("made", "a.txt")));
        Assert.Equal(string.Empty, Serve(new(ElevatedOperation.Rename, [_fx.R("made", "a.txt")], NewName: "b.txt")));
        Assert.True(File.Exists(_fx.R("made", "b.txt")));
        Assert.Equal(string.Empty, Serve(new(ElevatedOperation.Move, [_fx.R("made", "b.txt")], _fx.R())));
        Assert.True(File.Exists(_fx.R("b.txt")));
        Assert.Equal(string.Empty, Serve(new(ElevatedOperation.Delete, [_fx.R("made")])));
        Assert.False(Directory.Exists(_fx.R("made")));

        Assert.True(Elevation.IsAccessDenied(new UnauthorizedAccessException()));
        Assert.False(Elevation.IsAccessDenied(new FileNotFoundException()));
    }

    [AvaloniaFact]
    public void Explorer_context_menu_and_thumbnails_are_made_without_builtin_com()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // Avalonia turns the runtime's COM interop off: the shell interfaces are called through their vtables
        var items = NextCommander.Platform.Windows.ShellContextMenu.CountItems([_fx.L("a.txt"), _fx.L("b.log")]);
        Assert.True(items > 3, $"{items} items");
        Assert.True(NextCommander.Platform.Windows.ShellContextMenu.CountItems([_fx.L("sub")]) > 3);
        // the shell's own verbs without an icon get the application's (open, cut, copy, delete, properties ...)
        var (count, withIcon) = NextCommander.Platform.Windows.ShellContextMenu.Probe([_fx.L("a.txt")], ShellMenuIcons.ForScale(1.5));
        Assert.True(count > 3);
        foreach (var verb in new[] { "open", "copy", "cut", "delete", "properties" })
        {
            Assert.Contains(verb, withIcon);
        }
        using (var drawn = ShellMenuIcons.Draw("nc_CopyToClipboard", 2)!)
        {
            Assert.Equal(32, drawn.PixelSize.Width);
            var pixels = new byte[32 * 32 * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                drawn.CopyPixels(new Avalonia.PixelRect(0, 0, 32, 32), handle.AddrOfPinnedObject(), pixels.Length, 32 * 4);
            }
            finally
            {
                handle.Free();
            }
            var painted = Enumerable.Range(0, 32 * 32).Count(i => pixels[i * 4 + 3] > 0);
            Assert.InRange(painted, 40, 32 * 32 - 1); // drawn, with a transparent ground
            if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
            {
                drawn.SavePng(Path.Combine(dir, "shell-menu-icon.png"));
            }
        }
        var png = _fx.L("dot.png");
        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(64, 64), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        bitmap.SavePng(png);
        Assert.NotNull(ThumbnailSources.Shell(png, 64));
    }

    [AvaloniaFact]
    public async Task Button_bars_are_edited_with_the_editor_not_as_json()
    {
        var (_, vm) = await _fx.OpenAsync();
        var window = _fx.Track(new SettingsWindow(new AppSettings(), () => { }, []));
        window.Show();
        window.SelectPage("Layout");
        await SettleAsync(3);
        Assert.Contains("Button bar buttons", window.VisibleLabels);
        Assert.DoesNotContain(window.GetLogicalDescendants().OfType<TextBox>(), b => b.AcceptsReturn && (b.Text ?? string.Empty).Contains("nc_", StringComparison.Ordinal));
        Assert.Equal(2, window.GetLogicalDescendants().OfType<Button>().Count(b => Equals(b.Content, "Edit...")));
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.EndsWith(" buttons", StringComparison.Ordinal) == true);
        Assert.NotNull(vm);
    }
}
