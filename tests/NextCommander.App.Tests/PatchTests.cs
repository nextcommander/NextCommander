using System.Text;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Patches and reports in the compare windows, Tools > Apply patch (the compare and merge plan, phase 5; 25 November 2026).</summary>
public sealed class PatchTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task A_patch_of_two_files_applies_to_the_first_one_and_the_report_is_written()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("app.cs"), "a\nb\nc\n");
        File.WriteAllText(_fx.R("app.cs"), "a\nB\nc\nd\n");
        var l = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("app.cs"))))!;
        var r = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.R("app.cs"))))!;
        var window = _fx.Track(new CompareWindow(l, r, vm.Tools));
        window.AskSaveOnClose = _ => Task.FromResult(SaveChoice.Discard);
        window.Show();
        await WaitUntilAsync(() => window.Comparing is { IsCompleted: true });

        var patch = Assert.Single(UnifiedDiff.Parse(window.PatchText()));
        Assert.Equal(("a/app.cs", "b/app.cs"), (patch.OldPath, patch.NewPath));
        Assert.Equal(["a", "B", "c", "d"], UnifiedDiff.Apply(["a", "b", "c"], patch).Lines);
        Assert.Contains("<ins>B</ins>", window.ReportHtml(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_folder_compare_writes_one_patch_and_Apply_patch_makes_the_old_folder_new()
    {
        var (_, vm) = await _fx.OpenAsync();
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        // old: the left folder (one file with CRLF), new: the right one
        Write(_fx.L("src", "main.c"), "int main()\r\n{\r\n    return 0;\r\n}\r\n");
        Write(_fx.R("src", "main.c"), "int main()\n{\n    return 1;\n}\n");
        Write(_fx.L("gone.txt"), "old\n");
        Write(_fx.R("docs", "new.md"), "# New\n");
        var bytes = new byte[] { 0, 1, 2 };
        File.WriteAllBytes(_fx.L("pic.bin"), bytes);
        File.WriteAllBytes(_fx.R("pic.bin"), [0, 1, 3]);

        var view = new SyncDirsView(VfsPath.Local(_fx.L()), VfsPath.Local(_fx.R()), vm.Tools);
        _fx.Track(new Avalonia.Controls.Window { Content = view, Width = 1100, Height = 700 }).Show();
        view.MethodSelection = 2; // by content
        await view.CompareAsync();
        var text = await view.PatchTextAsync();
        Assert.Contains("Binary files a/pic.bin and b/pic.bin differ", text, StringComparison.Ordinal);
        Assert.Contains("--- /dev/null\n+++ b/docs/new.md\n", text, StringComparison.Ordinal);
        Assert.Contains("--- a/gone.txt\n+++ /dev/null\n", text, StringComparison.Ordinal);

        // the patch applied to the left folder: the C file gets "return 1" and keeps its CRLF line ends
        var patchFile = Path.Combine(_fx.Root, "change.patch");
        File.WriteAllText(patchFile, text);
        vm.ActivateSide(vm.LeftSide);
        var lists = 0;
        _fx.Dialogs.QuickListHandler = items =>
        {
            lists++;
            Assert.Contains(items, i => i.Text == "docs/new.md" && i.Detail == "new file");
            Assert.Contains(items, i => i.Text == "gone.txt" && i.Detail == "deleted");
            // first a look at a file's result, then the patch applied
            return lists == 1 ? new QuickListResult(items.First(i => i.Text == "src/main.c"), -1) : new QuickListResult(items[0], 0);
        };
        await vm.ExecuteAsync($"nc_ApplyPatch \"{patchFile}\"");
        Assert.Equal(2, lists);
        Assert.NotNull(_fx.Dialogs.LastCompare); // the preview
        Assert.Equal("# New\n", File.ReadAllText(_fx.L("docs", "new.md")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.False(File.Exists(_fx.L("gone.txt")));
        Assert.Equal("int main()\r\n{\r\n    return 1;\r\n}\r\n", File.ReadAllText(_fx.L("src", "main.c")));
        Assert.Contains(_fx.Dialogs.Messages, m => m.StartsWith("The patch changed", StringComparison.Ordinal));
        Assert.Equal(bytes, File.ReadAllBytes(_fx.L("pic.bin"))); // binary files are not touched
    }
}
