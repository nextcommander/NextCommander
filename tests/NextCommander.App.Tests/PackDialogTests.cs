using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Vfs.Archive;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Pack files (Alt+F5), and "Pack..." in the right-click menus.</summary>
public sealed class PackDialogTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_dialog_has_the_options_of_total_commander()
    {
        await _fx.OpenAsync();
        var dialog = _fx.Track(new PackDialog("Pack 1 file(s) to the archive", @"C:\Users\me\Downloads\Debug.zip", PackFormat.Zip));
        dialog.Show();
        await SettleAsync(4);
        Assert.Equal("ZIP", dialog.SelectedPacker);
        Assert.Equal(6, (int)dialog.Rate.Value!);
        Assert.True(dialog.Option("Also pack path names (only recursed)").IsChecked);
        Assert.True(dialog.Option("Recursively pack subdirectories").IsChecked);
        var leaveOut = dialog.Option("Leave out base directory when packing folders");
        Assert.False(leaveOut.IsEnabled); // only with separate archives
        dialog.Option("Create separate archives, one per selected file/dir").IsChecked = true;
        Assert.True(leaveOut.IsEnabled);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            dialog.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "pack-dialog.png"));
        }

        // another packer swaps the extension; TGZ has no encryption
        dialog.SelectedPacker = "TGZ";
        Assert.EndsWith("Debug.tar.gz", dialog.TargetText, StringComparison.Ordinal);
        Assert.False(dialog.Option("Encrypt").IsEnabled);
        dialog.SelectedPacker = "ZIP";
        Assert.EndsWith("Debug.zip", dialog.TargetText, StringComparison.Ordinal);

        // encrypt: the password twice
        dialog.Option("Encrypt").IsChecked = true;
        dialog.PasswordBox.Text = "pw";
        dialog.RepeatBox.Text = "other";
        dialog.Accept(queue: false);
        Assert.Null(dialog.Result);
        Assert.Contains("do not match", dialog.ErrorText, StringComparison.Ordinal);
        dialog.RepeatBox.Text = "pw";
        leaveOut.IsChecked = true;
        dialog.Option("Recursively pack subdirectories").IsChecked = false;
        dialog.Rate.Value = 9;
        dialog.Accept(queue: false);
        var result = dialog.Result!;
        Assert.Equal((PackFormat.Zip, "pw", true, true, false, 9), (result.Format, result.Password, result.Separate, result.LeaveOutBaseFolder, result.Recursive, result.Rate!.Value));
        Assert.Equal(System.IO.Compression.CompressionLevel.SmallestSize, result.Level);
    }

    [AvaloniaFact]
    public async Task Pack_is_in_the_right_click_menus()
    {
        var (w, vm) = await _fx.OpenAsync();
        // the application's own menu (Shift+right click, Linux, macOS)
        var list = w.GetVisualDescendants().OfType<Control>().First(c => c.ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Tag as string == "nc_Copy") == true);
        Assert.Contains(list.ContextMenu!.Items.OfType<MenuItem>(), m => m.Tag as string == "nc_PackFiles");

        // Explorer's menu (Windows): the application's item comes first, then a separator
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var ran = false;
        var (probe, texts) = NextCommander.Platform.Windows.ShellContextMenu.Probe([_fx.L("a.txt")], ShellMenuIcons.ForScale(1),
            [new NextCommander.Platform.SystemMenuItem("Pack...", "nc_PackFiles", () => ran = true)]);
        Assert.True(probe.Items > 4);
        Assert.Equal("Pack...", texts[0]);
        Assert.Equal(string.Empty, texts[1]); // the separator
        Assert.Contains("nc_PackFiles", probe.VerbsWithIcon);
        Assert.False(ran); // only when chosen
    }
}
