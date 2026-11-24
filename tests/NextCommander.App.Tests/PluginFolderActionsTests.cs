using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.Core.Plugins;
using NextCommander.Core.Scripting;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Tests;

/// <summary>
/// Plugins' actions for a folder and its selection (Git's stage, commit ...), the dialogs lent to them, and the example
/// of a JavaScript file system (user's request, 24 November 2026).
/// </summary>
public sealed class PluginFolderActionsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>A plugin file system that only offers an action, which asks a question and shows a text.</summary>
    private sealed class Asking : IFileSystemProvider, IPluginFolderActions
    {
        public List<(VfsPath Folder, IReadOnlyList<VfsEntry> Selected)> Asked { get; } = [];

        public string Scheme => "asking";

        public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

        public IReadOnlyList<PluginAction> ActionsFor(VfsPath folder, IReadOnlyList<VfsEntry> selected, IPluginDialogs dialogs)
        {
            Asked.Add((folder, selected));
            return
            [
                new PluginAction("Ask", PluginActionIcon.Info, async _ =>
                {
                    var name = await dialogs.PromptAsync("Ask", "Name?", "x");
                    var pick = await dialogs.ChooseAsync("Pick", ["one", "two"]);
                    await dialogs.ShowTextAsync("result.diff", $"+{name} {pick}");
                    return "asked " + name;
                }),
            ];
        }

        public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VfsEntry>>([]);

        public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) => Task.FromResult<VfsEntry?>(null);

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Folder_actions_reach_the_menus_and_use_the_programs_dialogs()
    {
        var (_, vm) = await _fx.OpenAsync();
        var plugin = new LoadedPlugin { Folder = "asking" };
        var asking = new Asking();
        plugin.FileSystems.Add(asking);
        vm.Plugins.Add(plugin);

        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == "a.txt");
        var actions = vm.Left.PluginActionsForCursor(); // the file list's menu
        var ask = Assert.Single(actions);
        Assert.Equal(VfsPath.Local(_fx.L()), asking.Asked[^1].Folder);
        Assert.Equal("a.txt", Assert.Single(asking.Asked[^1].Selected).Name);
        Assert.Contains(PluginMenus.Items(vm).OfType<Avalonia.Controls.MenuItem>(), m => m.Header as string == "Ask"); // the Plugins menu

        _fx.Dialogs.NextPrompt = "Ada";
        _fx.Dialogs.QuickListHandler = items => new QuickListResult(items.Single(i => i.Text == "two"), -1);
        await vm.Left.RunPluginActionAsync(ask);
        var shown = Assert.Single(_fx.Dialogs.LastLister!.Files);
        Assert.Equal("result.diff", shown.Name);
        Assert.Equal("+Ada two", File.ReadAllText(shown.Path.Path));
    }

    [AvaloniaFact]
    public async Task The_example_file_system_works_as_written()
    {
        var fs = ScriptFileSystemProvider.Load("scratch.js", ScriptTemplates.FileSystem);
        Assert.Equal("Scratch (in memory)", fs.EntryName);
        var root = fs.EntryRoot;
        Assert.Equal(["readme.txt"], (await fs.ListAsync(root, CancellationToken.None)).Select(e => e.Name));
        await fs.CreateDirectoryAsync(root.Combine("d"), CancellationToken.None);
        await using (var write = await fs.OpenWriteAsync(root.Combine("d").Combine("n.txt"), true, CancellationToken.None))
        {
            await write.WriteAsync("note"u8.ToArray());
        }
        await fs.MoveAsync(root.Combine("d"), root.Combine("e"), false, CancellationToken.None);
        var moved = Assert.Single(await fs.ListAsync(root.Combine("e"), CancellationToken.None));
        Assert.Equal(("n.txt", 4L), (moved.Name, moved.Size));
        await fs.DeleteAsync(root.Combine("e"), true, CancellationToken.None);
        Assert.Equal(["readme.txt"], (await fs.ListAsync(root, CancellationToken.None)).Select(e => e.Name));
    }
}
