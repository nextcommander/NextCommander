using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NextCommander.App.Services;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Esc closes Lister and the editor, "/" goes to the root of the drive, settings export / import.</summary>
public sealed class EscRootAndTransferTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static VfsEntry Entry(string path) =>
        new LocalFileSystemProvider().GetEntryAsync(VfsPath.Local(path), CancellationToken.None).GetAwaiter().GetResult()!;

    /// <summary>Esc as a key press; the release only while the window is still open (Esc may have closed it).</summary>
    private static void Esc(Avalonia.Controls.Window w)
    {
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        if (w.IsVisible)
        {
            w.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        }
    }

    [AvaloniaFact]
    public async Task Esc_closes_the_editor_from_the_text_after_its_search_panel()
    {
        var (_, vm) = await _fx.OpenAsync();
        var editor = _fx.Track(new EditorWindow(Entry(_fx.L("a.txt")), vm.Tools));
        var closed = false;
        editor.Closed += (_, _) => closed = true;
        editor.Show();
        await WaitUntilAsync(() => editor.Loading is { IsCompleted: true });
        await SettleAsync(4);
        editor.Editor.TextArea.Focus(); // the focus in the text: the text area would keep Esc for itself
        await SettleAsync();
        Press(editor, Key.F, RawInputModifiers.Control); // find panel open: Esc closes it first
        await SettleAsync();
        Esc(editor);
        await SettleAsync();
        Assert.False(closed);

        editor.Editor.TextArea.Focus();
        Esc(editor);
        await SettleAsync();
        Assert.True(closed);
    }

    [AvaloniaFact]
    public async Task Esc_closes_lister_after_its_find_bar()
    {
        var (_, vm) = await _fx.OpenAsync();
        var lister = _fx.Track(new ListerWindow(new ListerRequest([Entry(_fx.L("a.txt"))], 0, vm.Tools)));
        var closed = false;
        lister.Closed += (_, _) => closed = true;
        lister.Show();
        await WaitUntilAsync(() => lister.View.Entry is not null && lister.View.Loading is { IsCompleted: true });
        await SettleAsync(4);

        lister.View.OpenFindBar();
        await SettleAsync();
        Esc(lister);
        await SettleAsync();
        Assert.False(lister.View.IsFindBarOpen);
        Assert.False(closed);

        lister.View.FocusContent();
        await SettleAsync();
        Esc(lister);
        await SettleAsync();
        Assert.True(closed);
    }

    [AvaloniaFact]
    public async Task Slash_goes_to_the_root_of_the_drive_unless_typing_a_command()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("sub")));
        await SettleAsync();

        // the drive holding the folder (a disk mounted under /media on Linux; C:\ ... on Windows)
        var drive = vm.Left.Drives.Where(d => d.RootPath.Length > 1 && _fx.L("sub").StartsWith(d.RootPath.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.RootPath.Length).Select(d => d.RootPath).FirstOrDefault() ?? "/";
        w.KeyTextInput("/");
        await WaitUntilAsync(() => vm.Left.CurrentPath!.Path != _fx.L("sub"));
        Assert.Equal(drive.TrimEnd('/', '\\'), vm.Left.CurrentPath!.Path.TrimEnd('/', '\\'));

        // typing a command line: "/" is a character of it
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("sub")));
        vm.CommandLineText = "cd ";
        w.KeyTextInput("/");
        await SettleAsync();
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);
        Assert.Equal("cd /", vm.CommandLineText);
    }

    [AvaloniaFact]
    public async Task Settings_are_exported_and_imported_with_a_restart()
    {
        var (_, vm) = await _fx.OpenAsync();
        var settingsFolder = Path.Combine(_fx.Root, "settings");
        vm.Settings.PanelFontSize = 19;
        vm.Settings.Connections.Add(new RemoteConnection { Name = "nas", Host = "nas.local" });
        vm.Settings.Hotlist.Add(new HotlistEntry { Name = "Work", Path = _fx.L() });
        Directory.CreateDirectory(Path.Combine(settingsFolder, "themes"));
        File.WriteAllText(Path.Combine(settingsFolder, "themes", "mine.json"), "{}");

        var zip = Path.Combine(_fx.Root, "export.zip");
        _fx.Dialogs.NextSaveFile = zip;
        await vm.ExecuteAsync("nc_ExportSettings");
        Assert.True(File.Exists(zip));
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains(zip, StringComparison.Ordinal));
        var summary = SettingsTransfer.Inspect(zip);
        Assert.Equal((1, 1, 1), (summary.Connections, summary.HotlistEntries, summary.UserFiles));

        // changed afterwards, then the export is imported again: the program starts anew with it
        vm.Settings.PanelFontSize = 12;
        vm.SaveSettings();
        var restarted = false;
        var exited = false;
        vm.Restart = () => restarted = true;
        vm.ExitRequested += (_, _) => exited = true;
        _fx.Dialogs.NextOpenFile = zip;
        await vm.ExecuteAsync("nc_ImportSettings");
        Assert.True(vm.SettingsImported);
        Assert.True(restarted);
        Assert.True(exited);
        double FontSize() => JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(settingsFolder, "settings.json")), SettingsStore.JsonOptions)!.PanelFontSize;
        Assert.Equal(19, FontSize());
        Assert.Single(Directory.GetFiles(Path.Combine(settingsFolder, "backups"), "settings-*.zip"));

        // the running program does not write over the imported settings any more (closing saves)
        vm.Settings.PanelFontSize = 30;
        vm.SaveSettings();
        Assert.Equal(19, FontSize());

        // not an export: refused with a message, nothing changes
        var notExport = Path.Combine(_fx.Root, "other.zip");
        File.WriteAllText(notExport, "x");
        _fx.Dialogs.NextOpenFile = notExport;
        await vm.ExecuteAsync("nc_ImportSettings");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("not a NextCommander settings export", StringComparison.Ordinal));
    }
}
