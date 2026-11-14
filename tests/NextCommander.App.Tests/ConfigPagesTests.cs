using Avalonia.Headless.XUnit;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration pages Refresh, Ignore list, History, Log file, Misc.</summary>
public sealed class ConfigPagesTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Ignore_list_hides_names_masks_and_paths()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.IgnoreList = ["a.txt", "*.log", _fx.L("sub")];
        File.WriteAllText(_fx.L("x.log"), "x");
        await vm.Left.RefreshAsync();
        Assert.Contains(vm.Left.Items, i => i.FullName == "a.txt");

        await vm.ExecuteAsync("nc_SwitchIgnoreList");
        Assert.True(vm.Settings.IgnoreListEnabled);
        Assert.DoesNotContain(vm.Left.Items, i => i.FullName is "a.txt" or "x.log" or "sub");
        Assert.Contains(vm.Left.Items, i => i.FullName == "file2.txt");
        await vm.ExecuteAsync("nc_SwitchIgnoreList");
        Assert.Contains(vm.Left.Items, i => i.FullName == "sub");
    }

    [AvaloniaFact]
    public async Task Automatic_refresh_can_be_switched_off()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.AutoRefresh = false;
        vm.ApplySettings();
        File.WriteAllText(_fx.L("new1.txt"), "x");
        await SettleAsync(40);
        Assert.DoesNotContain(vm.Left.Items, i => i.FullName == "new1.txt");

        vm.Settings.AutoRefresh = true;
        vm.Settings.AutoRefreshExcluded = "*" + Path.GetFileName(_fx.Root) + "*";
        vm.ApplySettings();
        File.WriteAllText(_fx.L("new2.txt"), "x");
        await SettleAsync(40);
        Assert.DoesNotContain(vm.Left.Items, i => i.FullName == "new2.txt"); // excluded folder

        vm.Settings.AutoRefreshExcluded = null;
        vm.ApplySettings();
        File.WriteAllText(_fx.L("new3.txt"), "x");
        await WaitUntilAsync(() => vm.Left.Items.Any(i => i.FullName == "new3.txt"));
    }

    [AvaloniaFact]
    public async Task History_lengths()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.FolderHistoryLength = 2;
        vm.Settings.CommandLineHistoryLength = 2;
        foreach (var folder in new[] { _fx.L("sub"), _fx.R(), _fx.L() })
        {
            vm.Left.MarkWorkedHere(); // folders only passed through are thinned out of the history
            await vm.Left.NavigateAsync(VfsPath.Local(folder));
        }
        Assert.Equal(2, vm.Left.History.Count);

        foreach (var command in new[] { "nc_SrcFull", "nc_SrcBrief", "nc_SrcFull" + " " })
        {
            vm.CommandLineText = command;
            await vm.ExecuteCommandLineAsync(keepTerminalOpen: false);
        }
        Assert.Equal(2, vm.Settings.CommandLineHistory.Count);
    }

    [AvaloniaFact]
    public async Task Log_file_records_operations()
    {
        var (_, vm) = await _fx.OpenAsync();
        var log = Path.Combine(_fx.Root, "logs", "ops.log");
        vm.Settings.LogFile = log;
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "a.txt");
        await vm.ExecuteAsync("nc_Copy");
        _fx.Dialogs.NextPrompt = "b.txt";
        vm.BeginInlineRename = null; // the name from the dialog, not typed in the list
        await vm.ExecuteAsync("nc_RenameOnly");
        var text = File.ReadAllText(log);
        Assert.Contains(_fx.L("a.txt") + " -> " + _fx.R(), text, StringComparison.Ordinal);
        Assert.Contains("-> b.txt", text, StringComparison.Ordinal);

        vm.Settings.LogErrorsOnly = true;
        await vm.Left.RefreshAsync();
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "file2.txt");
        await vm.ExecuteAsync("nc_Copy");
        Assert.DoesNotContain("file2.txt", File.ReadAllText(log), StringComparison.Ordinal);

        // servers and archives are separate kinds (Log file > FTP operations / unpacking)
        var registry = new NextCommander.Core.Vfs.ProviderRegistry([]);
        var local = Core.Vfs.VfsPath.Local(_fx.L());
        var server = Core.Vfs.VfsPath.Parse("sftp://me@host/home");
        var inArchive = Core.Vfs.VfsPath.Parse("archive://" + Uri.EscapeDataString(_fx.L("x.zip")) + "!/a.txt");
        var copy = new NextCommander.Core.Operations.CopyMoveJob(registry, [], local, move: false);
        var delete = new NextCommander.Core.Operations.DeleteJob(registry, [], null);
        Assert.Equal(Services.OperationLog.Kind.Remote, Services.OperationLog.KindOf(copy, [local], server));
        Assert.Equal(Services.OperationLog.Kind.Remote, Services.OperationLog.KindOf(delete, [server], null));
        Assert.Equal(Services.OperationLog.Kind.Unpack, Services.OperationLog.KindOf(copy, [inArchive], local));
        Assert.Equal(Services.OperationLog.Kind.CopyMove, Services.OperationLog.KindOf(copy, [local], local));
        vm.Settings.LogRemote = false;
        Assert.False(Services.OperationLog.Wants(vm.Settings, Services.OperationLog.Kind.Remote));
        Assert.True(Services.OperationLog.Wants(vm.Settings, Services.OperationLog.Kind.Unpack));
    }

    [AvaloniaFact]
    public async Task Confirm_before_exit()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.ConfirmExit = true;
        _fx.Dialogs.ConfirmAnswer = false;
        await vm.ExecuteAsync("nc_Exit");
        await SettleAsync(4);
        Assert.Equal(1, _fx.Dialogs.Confirmations);
        Assert.True(w.IsVisible);
        _fx.Dialogs.ConfirmAnswer = true;
        await vm.ExecuteAsync("nc_Exit");
        await SettleAsync(4);
        Assert.False(w.IsVisible);
    }
}
