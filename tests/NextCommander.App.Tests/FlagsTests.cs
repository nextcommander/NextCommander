using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Services.Icons;
using NextCommander.App.Views.Dialogs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Language: each language with its country's flag.</summary>
public sealed class FlagsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Languages_show_their_flags()
    {
        Assert.True(Flags.Has("tr"));
        Assert.True(Flags.Has("en-US"));
        Assert.True(Flags.Has("de_AT"));
        Assert.False(Flags.Has("xx"));
        Assert.Null(Flags.Create("xx"));

        var (_, vm) = await _fx.OpenAsync();
        var window = _fx.Track(new SettingsWindow(vm.Settings, () => { }, []));
        window.Show();
        window.SelectPage("Display/Language");
        await SettleAsync(4);
        var combo = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IEnumerable<string> items && items.Contains("Türkçe"));
        // every language entry is drawn with a flag (Viewbox) next to its name ("System": a monitor)
        var items = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        Assert.True(items.Count >= 3, string.Join(", ", items));
        foreach (var text in items.Where(t => t != "System"))
        {
            var row = combo.ItemTemplate!.Build(text)!;
            Assert.NotEmpty(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(row).OfType<Viewbox>());
        }

        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            var strip = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(8) };
            foreach (var code in new[] { "tr", "en", "de", "fr", "it", "es", "nl", "pt", "ru", "pl", "uk", "ja", "zh" })
            {
                strip.Children.Add(Flags.Create(code, 40)!);
            }
            var flags = _fx.Track(new Window { Width = 760, Height = 70, Content = strip });
            flags.Show();
            await SettleAsync(4);
            flags.CaptureRenderedFrame()!.Save(Path.Combine(dir, "flags.png"));
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "settings-language.png"));
        }
    }
}
