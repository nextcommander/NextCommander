using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Lister preview (key 5) for HTML, Markdown and RTF.</summary>
[Collection("Appearance")]
public sealed class ListerPreviewTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        App.ApplyColorTheme(null);
        _fx.Dispose();
    }

    private async Task<ListerWindow> OpenAsync(MainWindowViewModel vm, string file)
    {
        var window = _fx.Track(new ListerWindow(new ListerRequest([(await new NextCommander.Vfs.Local.LocalFileSystemProvider().GetEntryAsync(VfsPath.Local(file), CancellationToken.None))!], 0, vm.Tools)));
        window.Show();
        await WaitUntilAsync(() => window.View.Entry is not null && window.View.Loading is { IsCompleted: true });
        await SettleAsync(4);
        return window;
    }

    [AvaloniaFact]
    public void Builds_pages_per_type()
    {
        var md = DocumentPreview.Build("md", "# Başlık\n\n- **kalın** madde\n\n| a | b |\n|---|---|\n| 1 | 2 |\n", dark: true);
        Assert.Contains("<h1", md, StringComparison.Ordinal);
        Assert.Contains("<strong>kalın</strong>", md, StringComparison.Ordinal);
        Assert.Contains("<table>", md, StringComparison.Ordinal);
        Assert.Contains("#1E1E1E", md, StringComparison.Ordinal);

        var html = DocumentPreview.Build("html", "<html><head><title>t</title></head><body>x<script>alert(1)</script></body></html>", dark: false);
        Assert.DoesNotContain("alert", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("<style>", StringComparison.Ordinal) < html.IndexOf("<title>", StringComparison.Ordinal));

        Assert.Contains("<b>kalın</b>", DocumentPreview.Build("rtf", @"{\rtf1 \b kal\u305?n\b0}", dark: false), StringComparison.Ordinal);
        Assert.True(DocumentPreview.Supports("MD"));
        Assert.False(DocumentPreview.Supports("txt"));
    }

    [AvaloniaFact]
    public async Task Markdown_and_rtf_open_in_preview_and_switch_with_keys()
    {
        var (_, vm) = await _fx.OpenAsync();
        var md = _fx.L("notes.md");
        File.WriteAllText(md, "# Notlar\n\nBu bir **Markdown** önizlemesi.\n\n- madde 1\n- madde 2\n\n```\nkod bloğu\n```\n\n> alıntı\n\n[bağlantı](https://example.com)\n");
        var lister = await OpenAsync(vm, md);
        Assert.Equal(ListerMode.Preview, lister.View.Mode);
        Assert.Contains("Preview", lister.View.StatusText, StringComparison.Ordinal);

        Press(lister, Key.D1);
        await SettleAsync();
        Assert.Equal(ListerMode.Text, lister.View.Mode);
        Press(lister, Key.D5);
        await SettleAsync();
        Assert.Equal(ListerMode.Preview, lister.View.Mode);

        var rtf = _fx.L("doc.rtf");
        File.WriteAllText(rtf, @"{\rtf1\ansi\ansicpg1254 {\fonttbl{\f0 Arial;}}\f0\fs24 {\b RTF belgesi}\par \i italik\i0  ve d\'fcz metin.\par}");
        var rtfLister = await OpenAsync(vm, rtf);
        Assert.Equal(ListerMode.Preview, rtfLister.View.Mode);

        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        Assert.NotNull(lister.CaptureRenderedFrame());
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        Directory.CreateDirectory(dir);
        lister.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "preview-md-light.png"));
        App.ApplyColorTheme("dark");
        await SettleAsync(6);
        lister.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "preview-md-dark.png"));
        rtfLister.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "preview-rtf-dark.png"));
    }

    /// <summary>Viewer plugin in the Lister: chosen by default for files it wants, key 6 switches to it.</summary>
    private sealed class UpperViewer : Core.Plugins.IViewerPlugin
    {
        public string Name => "Büyük harf";

        public bool CanView(string fileName, ReadOnlySpan<byte> header) => header.StartsWith("UPPER"u8);

        public async Task<Core.Plugins.ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken) =>
            new() { Text = (await new StreamReader(content).ReadToEndAsync(cancellationToken)).ToUpperInvariant(), Status = "ok" };
    }

    [AvaloniaFact]
    public async Task Viewer_plugins_show_in_the_lister()
    {
        var (_, vm) = await _fx.OpenAsync();
        var plugin = new Core.Plugins.LoadedPlugin { Folder = _fx.Root };
        plugin.Viewers.Add(new UpperViewer());
        Core.Plugins.PluginCatalog.Add(plugin);
        try
        {
            var file = _fx.L("x.dat");
            File.WriteAllText(file, "UPPER and lower");
            var lister = await OpenAsync(vm, file);
            Assert.Equal(ListerMode.Plugin, lister.View.Mode);
            Assert.Equal("UPPER AND LOWER", lister.View.Editor.Text);
            Assert.Contains("Plugin (Büyük harf: ok)", lister.View.StatusText, StringComparison.Ordinal);

            Press(lister, Key.D1);
            await SettleAsync();
            Assert.Equal("UPPER and lower", lister.View.Editor.Text);
            Press(lister, Key.D6);
            await SettleAsync();
            Assert.Equal(ListerMode.Plugin, lister.View.Mode);

            // A file no plugin wants: key 6 falls back to text
            var other = _fx.L("y.txt");
            File.WriteAllText(other, "plain");
            var second = await OpenAsync(vm, other);
            Assert.Equal(ListerMode.Text, second.View.Mode);
            Press(second, Key.D6);
            await SettleAsync();
            Assert.Equal(ListerMode.Text, second.View.Mode);
        }
        finally
        {
            Core.Plugins.PluginCatalog.Clear();
        }
    }

    /// <summary>A view next to the Lister's own (picture metadata): the picture opens first, key 6 shows the metadata.</summary>
    [AvaloniaFact]
    public async Task Picture_metadata_is_the_plugin_view_next_to_the_picture()
    {
        var (_, vm) = await _fx.OpenAsync();
        var plugin = new Core.Plugins.LoadedPlugin { Folder = _fx.Root };
        plugin.Viewers.Add(new NextCommander.Plugin.Viewers.ImageMetadataViewer());
        Core.Plugins.PluginCatalog.Add(plugin);
        try
        {
            var file = _fx.L("galata.jpg");
            File.WriteAllBytes(file, ViewersPluginTests.PhotoWithExif());
            var lister = await OpenAsync(vm, file);
            Assert.Equal(ListerMode.Image, lister.View.Mode);

            Press(lister, Key.D6);
            await WaitUntilAsync(() => lister.View.PreviewHtml is not null);
            Assert.Equal(ListerMode.Plugin, lister.View.Mode);
            Assert.Contains("Canon EOS R5", lister.View.PreviewHtml, StringComparison.Ordinal);
            Assert.Contains("openstreetmap.org", lister.View.PreviewHtml, StringComparison.Ordinal);
        }
        finally
        {
            Core.Plugins.PluginCatalog.Clear();
        }
    }

    [AvaloniaFact]
    public void Media_positions_are_remembered()
    {
        var file = Path.Combine(_fx.Root, "positions.json");
        var positions = new MediaPositions(file);
        positions.Set("a.mp4", 125, 600);
        positions.Set("b.mp4", 5, 600); // barely started: not kept
        positions.Set("c.mp4", 590, 600); // finished: not kept
        var reloaded = new MediaPositions(file);
        Assert.Equal(125, reloaded.Get("a.mp4"));
        Assert.Null(reloaded.Get("b.mp4"));
        Assert.Null(reloaded.Get("c.mp4"));
        reloaded.Set("a.mp4", 0, 600);
        Assert.Null(new MediaPositions(file).Get("a.mp4"));
    }

    [AvaloniaFact]
    public async Task Media_without_vlc_shows_details_and_the_reason()
    {
        if (MediaSupport.IsAvailable)
        {
            return; // VLC installed on this machine: playback path instead
        }
        var (_, vm) = await _fx.OpenAsync();
        var song = _fx.L("song.mp3");
        File.WriteAllBytes(song, [0x49, 0x44, 0x33, 3, 0, 0, 0, 0, 0, 0]);
        var lister = await OpenAsync(vm, song);
        Assert.Equal(ListerMode.Info, lister.View.Mode);
        Press(lister, Key.D7);
        await SettleAsync();
        Assert.Equal(ListerMode.Info, lister.View.Mode);
        Assert.NotNull(MediaSupport.Error);
    }
}
