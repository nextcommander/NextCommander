using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Display options.</summary>
public sealed class ConfigDisplayTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static List<string> Names(PanelViewModel panel) => panel.Items.Where(i => !i.IsParent).Select(i => i.FullName).ToList();

    [AvaloniaFact]
    public async Task Parent_in_root_and_folder_decoration()
    {
        var (_, vm) = await _fx.OpenAsync();
        var root = VfsPath.Local(Path.GetPathRoot(_fx.Root)!);
        await vm.Left.NavigateAsync(root);
        Assert.DoesNotContain(vm.Left.Items, i => i.IsParent);
        vm.Settings.ShowParentInRoot = true;
        vm.Left.Reapply();
        Assert.True(vm.Left.Items[0].IsParent);
        await vm.Left.GoUpAsync(); // does nothing in the root
        Assert.Equal(root, vm.Left.CurrentPath);

        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L()));
        vm.Settings.AppendDirectorySeparator = true;
        vm.Left.Reapply();
        var sub = vm.Left.Items.First(i => i.FullName == "sub");
        var separator = OperatingSystem.IsWindows() ? "\\" : "/";
        Assert.Equal($"[sub{separator}]", sub.ShownName);
        Assert.Equal("[..]", vm.Left.Items[0].ShownName);
        vm.Settings.DirectoryBrackets = false;
        vm.Left.Reapply();
        Assert.Equal("sub" + separator, vm.Left.Items.First(i => i.FullName == "sub").ShownName);
        Assert.Equal("a", vm.Left.Items.First(i => i.FullName == "a.txt").ShownName);
    }

    [AvaloniaFact]
    public void A_link_that_cannot_be_listed_opens_its_target()
    {
        Assert.Null(PanelViewModel.ReadableLinkTarget(VfsPath.Local(_fx.L()))); // not a link
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // as Windows' "Application Data" junction in ProgramData (user's report, 23 November 2026)
        var target = Directory.CreateDirectory(_fx.L("real")).FullName;
        var junction = _fx.L("Application Data");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", junction, target]) { CreateNoWindow = true, UseShellExecute = false }))
        {
            mklink!.WaitForExit();
        }
        try
        {
            Assert.Equal(target, PanelViewModel.ReadableLinkTarget(VfsPath.Local(junction))?.Path);
        }
        finally
        {
            Directory.Delete(junction); // the link only (the folder's clean-up would go into it)
        }
    }

    [AvaloniaFact]
    public async Task Sort_methods_and_folder_sorting()
    {
        File.WriteAllText(_fx.L("_x.txt"), "x");
        var (_, vm) = await _fx.OpenAsync();
        var left = vm.Left;

        Assert.True(Names(left).IndexOf("file2.txt") < Names(left).IndexOf("file10.txt")); // natural (default)
        vm.Settings.SortMethod = SortMethod.Alphabetical;
        left.Reapply();
        Assert.True(Names(left).IndexOf("file10.txt") < Names(left).IndexOf("file2.txt"));
        vm.Settings.SortMethod = SortMethod.Ordinal;
        left.Reapply();
        Assert.Equal("_x.txt", Names(left).Last()); // '_' (0x5F) after letters folded to upper case (A-Z 0x41-0x5A)

        Assert.True(PanelViewModel.NameComparer(SortMethod.Natural).Compare("a2", "a10") < 0);
        Assert.True(PanelViewModel.NameComparer(SortMethod.Alphabetical).Compare("a2", "a10") > 0);
        // files: the name before the extension first (user's report, 23 November 2026)
        var natural = PanelViewModel.NameComparer(SortMethod.Natural);
        Assert.True(PanelViewModel.CompareFileNames(natural, "deneme1.txt", "deneme1 - Copy.txt") < 0);
        Assert.True(PanelViewModel.CompareFileNames(natural, "a.txt", "a.zip") < 0);
        Assert.True(PanelViewModel.CompareFileNames(natural, ".gitignore", "a.txt") < 0);
        File.WriteAllText(_fx.L("deneme1 - Copy.txt"), "x");
        File.WriteAllText(_fx.L("deneme1.txt"), "x");
        vm.Settings.SortMethod = SortMethod.Natural;
        await left.RefreshAsync();
        Assert.Equal(Names(left).IndexOf("deneme1.txt") + 1, Names(left).IndexOf("deneme1 - Copy.txt"));

        // Folders: by date like files, or always by name
        Directory.CreateDirectory(_fx.L("zz_old"));
        Directory.SetLastWriteTime(_fx.L("zz_old"), new DateTime(2001, 1, 1));
        Directory.SetLastWriteTime(_fx.L("sub"), new DateTime(2020, 1, 1));
        await left.RefreshAsync();
        left.SetSort(SortColumn.Date); // newest first
        var dirs = left.Items.Where(i => i.IsDirectory && !i.IsParent).Select(i => i.FullName).ToList();
        Assert.Equal(["sub", "zz_old"], dirs);
        left.SetSort(SortColumn.Date); // oldest first
        Assert.Equal(["zz_old", "sub"], left.Items.Where(i => i.IsDirectory && !i.IsParent).Select(i => i.FullName));
        vm.Settings.SortDirectoriesByNameOnly = true;
        left.Reapply();
        Assert.Equal(["sub", "zz_old"], left.Items.Where(i => i.IsDirectory && !i.IsParent).Select(i => i.FullName));
    }

    [AvaloniaFact]
    public async Task Tree_loads_completely_on_request()
    {
        Directory.CreateDirectory(_fx.L("sub", "deep", "deeper"));
        var (_, vm) = await _fx.OpenAsync();
        // The tree starts at the drive root: above the test folder list only the folder on the way down.
        var top = VfsPath.Local(_fx.L());
        async Task<IReadOnlyList<VfsEntry>> List(VfsPath p)
        {
            if (top.IsSameOrUnder(p) && p != top)
            {
                var next = p.Combine(top.GetRelativeTo(p)!.Split('/')[0]);
                return [new VfsEntry { Name = next.Name, Path = next, IsDirectory = true }];
            }
            return await vm.Left.ListSubdirectoriesAsync(p);
        }
        var root = TreeNodeViewModel.CreateRoot(top, List);
        var count = await root.LoadAllAsync();
        Assert.True(count > 3, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var node = await root.RevealAsync(VfsPath.Local(_fx.L("sub", "deep", "deeper")));
        Assert.Equal("deeper", node.Name);
        Assert.Empty(node.Children); // loaded: no "…" placeholder on a leaf
    }

    [AvaloniaFact]
    public async Task Tool_tips_drive_labels_and_comments()
    {
        Descriptions.Write(_fx.L(), "a.txt", "ilk dosya");
        var (w, vm) = await _fx.OpenAsync();
        Assert.True(ToolTip.GetServiceEnabled(w));
        vm.Settings.ShowToolTips = false;
        vm.ApplySettings();
        await SettleAsync();
        Assert.False(ToolTip.GetServiceEnabled(w));

        var item = vm.Left.Items.First(i => i.FullName == "a.txt");
        Assert.Equal("ilk dosya", vm.Left.CommentOf(item));
        var cell = new TextBlock { Text = item.ShownName, Classes = { "cell" } };
        Assert.Null(w.LeftPanel.TipFor(cell, item)); // tips off by default
        vm.Settings.ShowCommentTips = true;
        Assert.Equal("a.txt\nilk dosya", w.LeftPanel.TipFor(cell, item));

        Assert.True(vm.Left.ShowDriveLabels);
        vm.Settings.ShowDriveLabels = false;
        vm.ApplySettings();
        Assert.False(vm.Left.ShowDriveLabels);
    }

    [AvaloniaFact]
    public async Task Layout_drive_bars_and_command_line_focus()
    {
        var (w, vm) = await _fx.OpenAsync();
        var shared = w.FindControl<ItemsControl>("SharedDriveBar")!;
        Assert.True(vm.Left.ShowDriveButtons);
        Assert.False(shared.IsEffectivelyVisible);

        vm.Settings.TwoDriveBars = false;
        vm.Settings.FlatDriveButtons = false;
        vm.ApplySettings();
        await SettleAsync();
        Assert.False(vm.Left.ShowDriveButtons);
        Assert.True(shared.IsEffectivelyVisible);
        Assert.Contains("raised", shared.Classes);
        Assert.True(vm.Left.Drives.Count > 0);

        // Typing in the panel: by default the command line takes the focus; with the option the panel keeps it
        w.LeftPanel.FocusList();
        await SettleAsync();
        w.KeyTextInput("a");
        await SettleAsync();
        Assert.Equal("a", vm.CommandLineText);
        Assert.True(w.FindControl<TextBox>("CommandLine")!.IsFocused);

        vm.CommandLineText = string.Empty;
        vm.Settings.CommandLineOnlyWhenFocused = true;
        w.LeftPanel.FocusList();
        await SettleAsync();
        w.KeyTextInput("b");
        await SettleAsync();
        Assert.Equal("b", vm.CommandLineText);
        Assert.False(w.FindControl<TextBox>("CommandLine")!.IsFocused);
    }

    [AvaloniaFact]
    public async Task Connection_menu_check_marks_follow_the_settings()
    {
        var (w, vm) = await _fx.OpenAsync();
        var item = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(w).OfType<MenuItem>()
            .First(m => m.CommandParameter as string == "nc_SwitchAdminShares");
        var before = vm.Settings.ShowAdminShares;
        Assert.Equal(before, item.IsChecked);
        await vm.ExecuteAsync("nc_SwitchAdminShares");
        await SettleAsync();
        Assert.Equal(!before, vm.Settings.ShowAdminShares);
        Assert.Equal(!before, item.IsChecked);
        // hidden FTP files are a setting of each FTP connection now, not of this menu
        Assert.DoesNotContain(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(w).OfType<MenuItem>(), m => m.CommandParameter as string == "nc_FtpHiddenFiles");
    }
}

