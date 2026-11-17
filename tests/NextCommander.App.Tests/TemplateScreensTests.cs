using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
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

    [AvaloniaFact]
    public async Task The_windows_take_the_colors_of_the_color_theme()
    {
        await _fx.OpenAsync();
        var app = Avalonia.Application.Current!;
        static Avalonia.Media.Color Resolved(string key) =>
            Avalonia.Application.Current!.TryGetResource(key, Avalonia.Application.Current.ActualThemeVariant, out var value) && value is Avalonia.Media.ISolidColorBrush brush
                ? brush.Color : default;
        try
        {
            foreach (var theme in new[] { "dark", "macos", "macos-dark", "ubuntu", "ubuntu-dark" })
            {
                ColorThemes.Apply(app, theme);
                var current = ColorThemes.Current!;
                await SettleAsync(2);
                // the window ground is the theme's Window.Background, the text its Window.Text
                if (current.Colors.TryGetValue("Window.Background", out var background))
                {
                    Assert.Equal(Avalonia.Media.Color.Parse(background).ToUInt32() | 0xFF000000, Resolved("NcSurfaceBrush").ToUInt32() | 0xFF000000);
                }
                if (current.Colors.TryGetValue("Window.Text", out var text))
                {
                    Assert.Equal(Avalonia.Media.Color.Parse(text).ToUInt32() | 0xFF000000, Resolved("NcTextBrush").ToUInt32() | 0xFF000000);
                }
                // the soft accent is the theme's accent, made transparent
                var accent = Resolved("NcAccentBrush");
                var soft = Resolved("NcAccentSoftBrush");
                Assert.Equal((accent.R, accent.G, accent.B), (soft.R, soft.G, soft.B));
                Assert.True(soft.A < 0x40);

                var dialog = _fx.Track(new CopyDialog(false, "Copy \"Movie\" to", _fx.R(), false, [], new CopyDialogOptions { Settings = new AppSettings(), Summary = new CopySummary(1000, 3, 1) }));
                dialog.Show();
                await SettleAsync(4);
                if (Environment.GetEnvironmentVariable("FM_DESIGN_SHOTS") is { Length: > 0 } dir)
                {
                    Directory.CreateDirectory(dir);
                    Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog)!.SavePng(Path.Combine(dir, $"copy-theme-{theme}.png"));
                }
                dialog.Close();
            }
        }
        finally
        {
            ColorThemes.Apply(app, null);
        }
    }

    [AvaloniaFact]
    public async Task Change_attributes_follows_the_template()
    {
        var (_, vm) = await _fx.OpenAsync();
        var folder = vm.Left.Items.Single(i => i.FullName == "sub").Entry!;
        var dialog = _fx.Track(new AttributesDialog([folder]));
        dialog.Show();
        await SettleAsync();
        Assert.False(dialog.CanApply);
        Assert.Equal("No changes yet", dialog.SummaryText);

        var hidden = dialog.SwitchOf(Core.Vfs.EntryAttributes.Hidden);
        hidden.IsChecked = hidden.IsChecked != true;
        Assert.True(dialog.CanApply);
        Assert.Equal("1 change", dialog.SummaryText);
        var recursive = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<Avalonia.Controls.CheckBox>()
            .Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Also apply to everything in subfolders");
        recursive.IsChecked = true;
        Assert.Equal("1 change, also inside the folders", dialog.SummaryText);
        await SettleAsync();
        await DesignShots.CaptureAsync(dialog, "attributes");

        var apply = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<Avalonia.Controls.Button>()
            .Single(b => NextCommander.App.Services.Design.Nc.TextOf(b) == "Apply");
        apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        var result = dialog.Result!;
        Assert.True(result.Recursive);
        Assert.Equal(hidden.IsChecked == true ? Core.Vfs.EntryAttributes.Hidden : Core.Vfs.EntryAttributes.None, result.Set);
    }

    [AvaloniaFact]
    public async Task The_transfers_button_shows_the_transfers_under_the_panels()
    {
        var (w, vm) = await _fx.OpenAsync();
        var button = w.FindControl<Avalonia.Controls.Button>("TransfersButton")!;
        Assert.False(button.IsVisible); // no background jobs
        vm.BackgroundJobCount = 2;
        await SettleAsync(2);
        Assert.True(button.IsVisible);

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await SettleAsync(2);
        Assert.NotNull(w.TransfersView);
        Assert.Equal("No running jobs.", w.TransfersView!.StatusText);
        await DesignShots.CaptureAsync(w, "transfers-docked");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Null(w.TransfersView);
    }

    [AvaloniaFact]
    public async Task Find_files_follows_the_template()
    {
        var (_, vm) = await _fx.OpenAsync();
        var find = _fx.Track(new Views.Search.FindFilesWindow(new FindFilesRequest(vm.Tools, _fx.L(), null, false, vm.FeedSearchResultsAsync, vm.GoToFileAsync)));
        find.Show();
        find.SetFields("file* *.log", _fx.L());
        await find.StartAsync();
        Assert.Equal(["b.log", "file10.txt", "file2.txt"], find.ResultEntries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.False(find.IsSearching);

        // a click on a column head sorts by it, a second click the other way
        find.SortBy("size");
        Assert.Equal("file2.txt", find.ResultEntries[0].Name); // 1 byte; the others have 2
        find.SortBy("name");
        Assert.Equal(["b.log", "file10.txt", "file2.txt"], find.ResultEntries.Select(e => e.Name));
        find.SortBy("name");
        Assert.Equal(["file2.txt", "file10.txt", "b.log"], find.ResultEntries.Select(e => e.Name));
        await SettleAsync();
        await DesignShots.CaptureAsync(find, "find-files");
    }

    [AvaloniaFact]
    public async Task Multi_rename_follows_the_template()
    {
        var (_, vm) = await _fx.OpenAsync();
        var dir = Core.Vfs.VfsPath.Local(_fx.L());
        var entries = (await vm.Tools.Registry.Resolve(dir).ListAsync(dir, CancellationToken.None))
            .Where(e => !e.IsDirectory).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var window = _fx.Track(new Views.Tools.MultiRenameWindow(entries, vm.Tools, () => vm.Left.RefreshAsync()));
        window.Show();
        await SettleAsync(4);
        window.Apply(new Core.Rename.MultiRenameOptions { NameMask = "[N]", Search = "file", Replace = "report_", Case = Core.Rename.CaseMode.Title });
        Assert.Equal(Core.Rename.CaseMode.Title, window.Options.Case);
        Assert.Contains(window.Rows, r => r.NewName == "Report_10.txt" && r.State == "ok");
        Assert.Contains(window.Rows, r => r.NewName == "A.txt" && r.State == "ok");
        await SettleAsync();
        await DesignShots.CaptureAsync(window, "multi-rename");

        window.Apply(new Core.Rename.MultiRenameOptions { NameMask = "same" });
        Assert.Contains(window.Rows, r => r.State == "warn" && r.Note == "same name twice");
    }
}
