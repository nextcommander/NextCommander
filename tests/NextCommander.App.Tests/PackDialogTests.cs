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

    private static readonly NextCommander.Core.Vfs.VfsEntry[] s_items =
    [
        new() { Name = "Debug.log", Path = NextCommander.Core.Vfs.VfsPath.Local(@"C:\Users\me\Downloads\Debug.log"), Size = 2_000_000 },
        new() { Name = "photo.jpg", Path = NextCommander.Core.Vfs.VfsPath.Local(@"C:\Users\me\Downloads\photo.jpg"), Size = 1_000_000 },
    ];

    [AvaloniaFact]
    public async Task The_dialog_has_formats_a_level_with_an_estimate_and_switches()
    {
        await _fx.OpenAsync();
        var dialog = _fx.Track(new PackDialog("Pack 2 items:", @"C:\Users\me\Downloads\Debug.zip", PackFormat.Zip, new PackDialogInput(s_items)));
        dialog.Show();
        await SettleAsync(4);
        Assert.Equal("ZIP", dialog.SelectedPacker);
        Assert.Equal(6, (int)dialog.LevelSlider.Value);
        Assert.True(dialog.Option("Keep folder paths inside the archive").IsChecked);
        Assert.True(dialog.Option("Include subfolders").IsChecked);
        Assert.True(dialog.Option("One archive per selected item").IsEnabled); // two items
        dialog.Option("One archive per selected item").IsChecked = true;
        var leaveOut = dialog.Option("Leave out the base folder when packing folders");
        Assert.True(leaveOut.IsEffectivelyVisible); // its setting under it, while it is on

        // the estimate: the log shrinks, the photo keeps its size
        Assert.StartsWith("≈ ", dialog.EstimateText, StringComparison.Ordinal);
        Assert.Contains("Debug.zip", dialog.SummaryText, StringComparison.Ordinal);
        dialog.LevelSlider.Value = 0;
        await SettleAsync();
        Assert.Equal("≈ " + NextCommander.Core.Formatting.SizeFormatter.Human(3_000_000), dialog.EstimateText);
        dialog.LevelSlider.Value = 6;
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            dialog.Option("Encrypt with a password").IsChecked = true;
            dialog.PasswordBox.Text = "Secret-pass1";
            await SettleAsync(3);
            Directory.CreateDirectory(dir);
            dialog.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "pack-dialog.png"));
            dialog.Option("Encrypt with a password").IsChecked = false;
            dialog.PasswordBox.Text = string.Empty;
        }

        // another packer swaps the extension; TGZ has no encryption, TAR no level
        dialog.SelectedPacker = "TGZ";
        Assert.EndsWith("Debug.tar.gz", dialog.TargetText, StringComparison.Ordinal);
        Assert.False(dialog.Option("Encrypt with a password").IsEnabled);
        dialog.SelectedPacker = "TAR";
        Assert.False(dialog.LevelSlider.IsEnabled);
        dialog.SelectedPacker = "ZIP";
        Assert.EndsWith("Debug.zip", dialog.TargetText, StringComparison.Ordinal);

        // encrypt: the password twice, the zip method of the dialog
        dialog.Option("Encrypt with a password").IsChecked = true;
        dialog.PasswordBox.Text = "pw";
        dialog.RepeatBox.Text = "other";
        dialog.Accept(queue: false);
        Assert.Null(dialog.Result);
        Assert.Contains("do not match", dialog.ErrorText, StringComparison.Ordinal);
        dialog.RepeatBox.Text = "pw";
        dialog.ZipMethod.SelectedIndex = 2;
        leaveOut.IsChecked = true;
        dialog.Option("Include subfolders").IsChecked = false;
        dialog.LevelSlider.Value = 9;
        dialog.Accept(queue: false);
        var result = dialog.Result!;
        Assert.Equal((PackFormat.Zip, "pw", true, true, false, 9), (result.Format, result.Password, result.Separate, result.LeaveOutBaseFolder, result.Recursive, result.Rate!.Value));
        Assert.Equal(System.IO.Compression.CompressionLevel.SmallestSize, result.Level);
        Assert.Equal(NextCommander.Core.Config.ZipEncryptionMethod.ZipCrypto, result.ZipEncryption);
    }

    [AvaloniaFact]
    public async Task Volumes_and_self_extracting_archives_with_7zip()
    {
        await _fx.OpenAsync();
        var dialog = _fx.Track(new PackDialog("Pack 1 file(s) to the archive", @"C:\Users\me\Downloads\Debug.zip", PackFormat.Zip));
        dialog.Show();
        await SettleAsync(4);
        var split = dialog.Option("Split into volumes");
        var sfx = dialog.Option("Self-extracting archive");
        // ZIP: 7-Zip's own options are off
        Assert.False(split.IsEnabled);
        Assert.False(sfx.IsEnabled);
        if (!SevenZipCli.IsAvailable)
        {
            return; // the 7-ZIP packer needs the program
        }
        dialog.SelectedPacker = "7-ZIP";
        Assert.True(split.IsEnabled);
        Assert.Equal(SevenZipCli.SfxModule is not null, sfx.IsEnabled);

        // volumes: the size is checked, a plain number is megabytes
        split.IsChecked = true;
        Assert.True(dialog.VolumeBox.IsEnabled);
        Assert.False(sfx.IsEnabled); // 7-Zip cannot split a self-extracting archive
        dialog.VolumeBox.Text = "big";
        dialog.Accept(queue: false);
        Assert.Null(dialog.Result);
        Assert.Contains("volume size", dialog.ErrorText, StringComparison.Ordinal);
        dialog.VolumeBox.Text = "700";
        dialog.Accept(queue: false);
        Assert.Equal((700L * 1024 * 1024, false), (dialog.Result!.VolumeSize, dialog.Result.SelfExtracting));
        Assert.EndsWith("Debug.7z", dialog.TargetText, StringComparison.Ordinal);

        if (SevenZipCli.SfxModule is null)
        {
            return;
        }
        // self-extracting: the name ends in .exe, no volumes
        var again = _fx.Track(new PackDialog("Pack 1 file(s) to the archive", @"C:\Users\me\Downloads\Debug.7z", PackFormat.SevenZip));
        again.Show();
        await SettleAsync(4);
        again.Option("Self-extracting archive").IsChecked = true;
        Assert.EndsWith("Debug" + SevenZipCli.SfxExtension, again.TargetText, StringComparison.Ordinal);
        Assert.False(again.Option("Split into volumes").IsEnabled);
        again.Accept(queue: false);
        Assert.Equal((0L, true), (again.Result!.VolumeSize, again.Result.SelfExtracting));
        Assert.Equal(SevenZipCli.SfxExtension, again.Result.Extension);
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
