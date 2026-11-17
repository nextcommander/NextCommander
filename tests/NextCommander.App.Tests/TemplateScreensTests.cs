using Avalonia.Headless.XUnit;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The windows built after the templates (copy dialog, find files, multi-rename), light and dark.</summary>
[Collection("Appearance")]
public sealed class TemplateScreensTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_copy_dialog_follows_the_template()
    {
        await _fx.OpenAsync();
        var dialog = _fx.Track(new CopyDialog(false, "Copy \"Movie\" to", _fx.R(), false, [_fx.L()], new CopyDialogOptions
        {
            Settings = new AppSettings(),
            Summary = new CopySummary(35_300_000_000, 35, 2),
            PickFolder = (_, _) => Task.FromResult<string?>(null),
            FindFiles = _ => Task.FromResult<IReadOnlyList<Core.Vfs.VfsEntry>?>(null),
        }));
        dialog.Show();
        await SettleAsync();
        await DesignShots.CaptureAsync(dialog, "copy");
        dialog.ShowAdvanced(true);
        Assert.True(dialog.AdvancedShown);
        await DesignShots.CaptureAsync(dialog, "copy-advanced");

        // a quick filter puts its patterns into the filter, and takes them out again
        dialog.FilterBox.Text = "| sample\\";
        var video = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .Single(b => b.Content as string == "Video");
        video.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal("*.mkv *.mp4 *.avi *.mov | sample\\", dialog.FilterBox.Text);
        Assert.True(video.IsChecked);
        video.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(" | sample\\", dialog.FilterBox.Text);
        Assert.False(video.IsChecked);
    }
}
