using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Views.Editor;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The menus of the built-in editor show icons.</summary>
public sealed class EditorMenuIconTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_editor_menus_have_icons()
    {
        var (_, vm) = await _fx.OpenAsync();
        var entry = (await new LocalFileSystemProvider().GetEntryAsync(VfsPath.Local(_fx.L("a.txt")), CancellationToken.None))!;
        var editor = _fx.Track(new EditorWindow(entry, vm.Tools));
        editor.Show();
        await WaitUntilAsync(() => editor.Loading is { IsCompleted: true });
        await SettleAsync(4);
        var items = editor.GetLogicalDescendants().OfType<MenuItem>().Where(m => m.Header is string).ToDictionary(m => (string)m.Header!, m => m);
        foreach (var header in new[] { "Save", "Reload from disk", "Close", "Undo", "Redo", "Find", "Replace", "Find next", "Go to line...",
                     "Word wrap", "Line numbers", "Font...", "Increase font size", "Decrease font size" })
        {
            Assert.True(items[header].Icon is Control, $"no icon: {header}");
        }
        Assert.Null(items["UTF-8"].Icon); // the encodings are plain names
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            items["_Edit"].IsSubMenuOpen = true;
            await SettleAsync(4);
            editor.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "editor-menu-icons.png"));
            items["_Edit"].IsSubMenuOpen = false;
        }
    }
}
