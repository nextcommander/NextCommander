using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Localization;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The verify checksums window (the user's template): the verdict, the rows, the filters, the report.</summary>
[Collection("Appearance")]
public sealed class VerifyChecksumsWindowTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        Localizer.Current = null;
        App.ApplyColorTheme(null);
        _fx.Dispose();
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private readonly List<VfsPath> _selected = [];

    /// <summary>a.txt and d.txt match, b.txt changed, missing.txt is not there (with <paramref name="problems"/>).</summary>
    private async Task<VerifyChecksumsWindow> OpenAsync(bool problems = true)
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("a.txt"), "hello");
        File.WriteAllText(_fx.L("b.txt"), "changed");
        File.WriteAllText(_fx.L("d.txt"), "world");
        var lines = problems
            ? $"{Sha256("hello")} *a.txt\n{Sha256("before")} *b.txt\n{Sha256("x")} *missing.txt\n{Sha256("world")} *d.txt\n"
            : $"{Sha256("hello")} *a.txt\n";
        File.WriteAllText(_fx.L("SHA256SUMS.sha256"), lines);
        var sums = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("SHA256SUMS.sha256"))))!;
        var request = new VerifyChecksumsRequest([sums], () => new VerifyChecksumsJob(vm.Tools.Registry, [sums]), vm.Tools.Registry,
            files =>
            {
                _selected.AddRange(files);
                return Task.CompletedTask;
            }, (_, _) => Task.FromResult<string?>(null));
        var window = _fx.Track(new VerifyChecksumsWindow(request));
        window.Show();
        await WaitUntilAsync(() => window.Results.Count > 0);
        await SettleAsync();
        return window;
    }

    [AvaloniaFact]
    public async Task Problems_come_first_with_the_filters_and_the_report()
    {
        var window = await OpenAsync();
        Assert.Equal("2 of 4 files have a problem", window.Verdict);
        Assert.Equal(["b.txt", "missing.txt", "a.txt", "d.txt"], window.ShownNames);
        Assert.Equal("Select 2 with problems in panel", window.ActionText);

        window.SetFilter("problems");
        Assert.Equal(["b.txt", "missing.txt"], window.ShownNames);
        window.SetFilter("ok");
        Assert.Equal(["a.txt", "d.txt"], window.ShownNames);
        window.SetFilter("all");
        window.SetSearch("d.t");
        await SettleAsync();
        Assert.Equal(["d.txt"], window.ShownNames);

        // the report: every file with its state, both checksums of the damaged one
        var report = window.Report();
        Assert.Contains("Damaged", report, StringComparison.Ordinal);
        Assert.Contains("Expected: " + Sha256("before"), report, StringComparison.Ordinal);
        Assert.Contains("Actual: " + Sha256("changed"), report, StringComparison.Ordinal);
        Assert.Contains("Missing", report, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task One_intact_file()
    {
        var window = await OpenAsync(problems: false);
        Assert.Equal("The file is intact", window.Verdict);
        Assert.Equal("Show in panel", window.ActionText);
    }

    [AvaloniaFact]
    public async Task It_is_translated_and_drawn_in_both_themes()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        UiLocalization.Install();
        var turkish = UiLocalization.Read("tr")!;
        var shots = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        foreach (var (theme, language) in new[] { ((string?)null, "en"), ("dark", "tr") })
        {
            App.ApplyColorTheme(theme);
            Localizer.Current = language == "tr" ? turkish : null;
            UiLocalization.Untranslated = language == "tr" ? t => missing.Add(t) : null;
            foreach (var problems in new[] { true, false })
            {
                var window = await OpenAsync(problems);
                await SettleAsync(4);
                if (shots is { Length: > 0 })
                {
                    Directory.CreateDirectory(shots);
                    Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(shots, $"verify-{(problems ? "problems" : "one")}-{theme ?? "light"}-{language}.png"));
                }
                window.Close();
            }
            UiLocalization.Untranslated = null;
        }
        Localizer.Current = null;
        var english = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Languages", "en.json")))!;
        var kept = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Languages", "tr.json")))!
            .Where(p => english.TryGetValue(p.Key, out var en) && en == p.Value).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
        string[] data = ["a.txt", "b.txt", "d.txt", "missing.txt", "SHA256SUMS", _fx.Root];
        var words = missing.Where(t => t.Count(char.IsLetter) > 1 && !data.Any(d => t.Contains(d, StringComparison.Ordinal))
            && !turkish.IsTranslation(t) && !kept.Contains(t)).ToList();
        Assert.True(words.Count == 0, "Not translated:\n" + string.Join("\n", words));
    }
}
