using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The application's own right-click menu of the file list shows the command icons.</summary>
public sealed class ContextMenuIconTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_file_list_menu_has_icons()
    {
        var (w, vm) = await _fx.OpenAsync();
        var list = w.GetVisualDescendants().OfType<Control>().First(c => c.ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Tag as string == "nc_Copy") == true);
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "a.txt"));
        list.ContextMenu!.Open(list); // as the panel opens it (no Explorer menu in tests)
        await SettleAsync(4);
        var items = list.ContextMenu.Items.OfType<MenuItem>().ToList();
        Assert.All(items.Where(i => i.Tag as string is "nc_Copy" or "nc_Delete" or "nc_PackFiles" or "nc_List"), i => Assert.NotNull(i.Icon));
        var copy = items.First(i => i.Tag as string == "nc_Copy");
        Assert.Contains(copy.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("menuicon") && b.IsEffectivelyVisible && b.Bounds.Width > 0);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "context-menu-icons.png"));
        }
        list.ContextMenu.Close();
        await SettleAsync();

        // nothing selected ([..] under the cursor): the commands for the selection are disabled in this menu too
        vm.Left.MoveCursorTo(0);
        list.ContextMenu.Open(list);
        await SettleAsync(4);
        Assert.False(items.First(i => i.Tag as string == "nc_Copy").IsEnabled);
        Assert.True(items.First(i => i.Tag as string == "nc_PasteFromClipboard").IsEnabled);
        list.ContextMenu.Close();
    }

    [AvaloniaFact]
    public async Task A_disc_image_has_mount_and_unmount_in_the_menu()
    {
        File.WriteAllBytes(_fx.L("disc.iso"), new byte[2048]);
        var (w, vm) = await _fx.OpenAsync();
        var list = w.GetVisualDescendants().OfType<Control>().First(c => c.ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Tag as string == "nc_Copy") == true);
        var items = list.ContextMenu!.Items.OfType<MenuItem>().ToList();
        bool Shown(string command) => items.First(i => i.Tag as string == command).IsVisible;

        // on the image: mount and unmount, with their icons
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "disc.iso"));
        list.ContextMenu.Open(list);
        await SettleAsync(4);
        Assert.True(Shown("nc_MountImage"));
        Assert.True(Shown("nc_UnmountImage"));
        Assert.NotNull(items.First(i => i.Tag as string == "nc_MountImage").Icon);
        list.ContextMenu.Close();
        await SettleAsync();

        // on another file: not there
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "a.txt"));
        list.ContextMenu.Open(list);
        await SettleAsync(4);
        Assert.False(Shown("nc_MountImage"));
        Assert.False(Shown("nc_UnmountImage"));
        list.ContextMenu.Close();
    }

    /// <summary>A plugin's file system with an action for one of its entries (like starting a Docker container).</summary>
    private sealed class ActionsProvider : NextCommander.Core.Vfs.IFileSystemProvider, NextCommander.Core.Plugins.IPluginEntryActions
    {
        public int Runs { get; private set; }

        public int Lists { get; private set; }

        public string Scheme => "acts";

        public NextCommander.Core.Vfs.ProviderCapabilities Capabilities => NextCommander.Core.Vfs.ProviderCapabilities.Read;

        private static NextCommander.Core.Vfs.VfsEntry Entry(string name) =>
            new() { Name = name, Path = NextCommander.Core.Vfs.VfsPath.Create("acts", "box", "/" + name), IsDirectory = true };

        public Task<IReadOnlyList<NextCommander.Core.Vfs.VfsEntry>> ListAsync(NextCommander.Core.Vfs.VfsPath directory, CancellationToken cancellationToken)
        {
            Lists++;
            return Task.FromResult<IReadOnlyList<NextCommander.Core.Vfs.VfsEntry>>([Entry("one"), Entry("two")]);
        }

        public Task<NextCommander.Core.Vfs.VfsEntry?> GetEntryAsync(NextCommander.Core.Vfs.VfsPath path, CancellationToken cancellationToken) =>
            Task.FromResult<NextCommander.Core.Vfs.VfsEntry?>(path.Path == "/" ? new() { Name = "box", Path = path, IsDirectory = true } : Entry(path.Name));

        public IReadOnlyList<NextCommander.Core.Plugins.PluginAction> ActionsFor(NextCommander.Core.Vfs.VfsEntry entry) =>
            entry.Name == "one"
                ? [new("Start the box", NextCommander.Core.Plugins.PluginActionIcon.Start, _ =>
                {
                    Runs++;
                    return Task.FromResult<string?>(null);
                })]
                : [];

        public Task<Stream> OpenReadAsync(NextCommander.Core.Vfs.VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenWriteAsync(NextCommander.Core.Vfs.VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(NextCommander.Core.Vfs.VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(NextCommander.Core.Vfs.VfsPath path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MoveAsync(NextCommander.Core.Vfs.VfsPath source, NextCommander.Core.Vfs.VfsPath destination, bool overwrite, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetTimesAsync(NextCommander.Core.Vfs.VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task A_plugin_puts_its_actions_for_an_entry_into_the_menu()
    {
        var (w, vm) = await _fx.OpenAsync();
        var provider = new ActionsProvider();
        vm.Tools.Registry.Register(provider);
        await vm.Left.NavigateAsync(NextCommander.Core.Vfs.VfsPath.Create("acts", "box", "/"));
        await SettleAsync(3);
        var list = w.GetVisualDescendants().OfType<Control>().First(c => c.ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Tag as string == "nc_Copy") == true);

        // on "one": its action at the top, with its icon; a click runs it and reads the folder again
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "one"));
        list.ContextMenu!.Open(list);
        await SettleAsync(4);
        var first = list.ContextMenu.Items.OfType<MenuItem>().First();
        Assert.Equal("Start the box", first.Header);
        Assert.NotNull(first.Icon);
        Assert.IsType<Separator>(list.ContextMenu.Items[1]);
        var lists = provider.Lists;
        first.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        list.ContextMenu.Close();
        await SettleAsync(4);
        Assert.Equal(1, provider.Runs);
        Assert.True(provider.Lists > lists);

        // on "two": none (and the earlier ones are gone)
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "two"));
        list.ContextMenu.Open(list);
        await SettleAsync(4);
        Assert.DoesNotContain(list.ContextMenu.Items.OfType<MenuItem>(), i => i.Tag is NextCommander.Core.Plugins.PluginAction);
        Assert.Equal("nc_Open", list.ContextMenu.Items.OfType<MenuItem>().First().Tag);
        list.ContextMenu.Close();
    }
}
