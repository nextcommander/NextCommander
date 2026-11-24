using Avalonia.Headless.XUnit;

namespace NextCommander.App.Tests;

/// <summary>Edit > Delete securely (user's request, 23 November 2026).</summary>
public sealed class SecureDeleteUiTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_warning_names_ssds_and_no_means_nothing_happens()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == "a.txt");
        vm.Settings.SecureDeletePasses = 3;
        _fx.Dialogs.ConfirmAnswer = false;
        await vm.ExecuteAsync("nc_SecureDelete");
        var warning = Assert.Single(_fx.Dialogs.ConfirmMessages);
        Assert.Contains("3×", warning, StringComparison.Ordinal);
        Assert.Contains("SSD", warning, StringComparison.Ordinal);
        Assert.True(File.Exists(_fx.L("a.txt")));

        _fx.Dialogs.ConfirmAnswer = true;
        vm.Tags.Toggle([NextCommander.Core.Vfs.VfsPath.Local(_fx.L("a.txt"))], "Red");
        await vm.ExecuteAsync("nc_SecureDelete");
        Assert.False(File.Exists(_fx.L("a.txt")));
        Assert.True(vm.Tags.IsEmpty); // its tags went with it
        Assert.DoesNotContain(vm.Left.Items, i => i.FullName == "a.txt");
    }
}
