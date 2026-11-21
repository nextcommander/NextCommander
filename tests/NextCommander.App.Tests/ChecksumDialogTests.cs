using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Localization;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The create checksums dialog (the user's template): algorithm cards, outputs, preview, comparison.</summary>
[Collection("Appearance")]
public sealed class ChecksumDialogTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        Localizer.Current = null;
        App.ApplyColorTheme(null);
        _fx.Dispose();
    }

    private async Task<ChecksumDialog> OpenAsync(params string[] names)
    {
        var (_, vm) = await _fx.OpenAsync();
        var folder = VfsPath.Local(_fx.L());
        var sources = new List<VfsEntry>();
        foreach (var name in names)
        {
            sources.Add((await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L(name))))!);
        }
        var request = new ChecksumDialogRequest(sources, folder, names.Length == 1 ? Path.GetFileNameWithoutExtension(names[0]) : "left", ChecksumKind.Sha256, false,
            choice => new CreateChecksumsJob(vm.Tools.Registry, sources, choice.Kind, choice.Output == ChecksumOutput.OneFile ? choice.SingleFile : null)
            {
                Bsd = choice.Bsd,
                WriteFiles = choice.Output != ChecksumOutput.Clipboard,
            });
        var dialog = _fx.Track(new ChecksumDialog(request));
        dialog.Show();
        await SettleAsync();
        return dialog;
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    [AvaloniaFact]
    public async Task One_file_the_algorithm_its_name_and_the_comparison()
    {
        File.WriteAllText(_fx.L("a.txt"), "hello");
        var dialog = await OpenAsync("a.txt");

        // SHA-256 at first; the name of the file follows the algorithm; the preview has placeholders as long as it
        Assert.Equal(ChecksumKind.Sha256, dialog.Kind);
        Assert.Equal("a.sha256", dialog.Choice.SingleFile!.Name);
        Assert.Contains(new string('·', 64) + " *a.txt", dialog.PreviewText, StringComparison.Ordinal);
        dialog.SetKind(ChecksumKind.Md5);
        Assert.Equal("a.md5", dialog.Choice.SingleFile!.Name);

        // a pasted checksum of 40 digits: SHA-1, the only one of that length
        dialog.SetExpected(new string('a', 40));
        await SettleAsync();
        Assert.Equal(ChecksumKind.Sha1, dialog.Kind);
        Assert.Contains("Compared once", dialog.VerdictText, StringComparison.Ordinal);

        // SHA-256, BSD style: the file is written, the preview shows its line, the expected one matches
        dialog.SetKind(ChecksumKind.Sha256);
        dialog.SetFormat(bsd: true);
        dialog.SetExpected(Sha256("hello").ToUpperInvariant());
        await dialog.CreateOrCloseAsync();
        await SettleAsync();
        var line = $"SHA256 (a.txt) = {Sha256("hello")}";
        Assert.Equal(line + "\n", File.ReadAllText(_fx.L("a.sha256")));
        Assert.Equal(line, dialog.PreviewText);
        Assert.Contains("Match: a.txt", dialog.VerdictText, StringComparison.Ordinal);
        Assert.NotNull(dialog.Result);

        // another checksum: no match; one of another length names its algorithm
        dialog.SetExpected(new string('0', 64));
        Assert.Contains("No match", dialog.VerdictText, StringComparison.Ordinal);
        dialog.SetExpected(new string('0', 32));
        Assert.Contains("MD5", dialog.VerdictText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_clipboard_writes_no_file_and_each_file_gets_its_own()
    {
        File.WriteAllText(_fx.L("a.txt"), "hello");
        File.WriteAllText(_fx.L("b.txt"), "world");
        var dialog = await OpenAsync("a.txt", "b.txt");
        Assert.Equal("left.sha256", dialog.Choice.SingleFile!.Name);

        dialog.SetOutput(ChecksumOutput.Clipboard);
        await dialog.CreateOrCloseAsync();
        await SettleAsync();
        Assert.False(File.Exists(_fx.L("left.sha256")));
        Assert.Equal($"{Sha256("hello")} *a.txt{Environment.NewLine}{Sha256("world")} *b.txt", await dialog.Clipboard!.TryGetTextAsync());

        dialog.SetOutput(ChecksumOutput.EachFile);
        await dialog.CreateOrCloseAsync();
        Assert.Equal($"{Sha256("world")} *b.txt\n", File.ReadAllText(_fx.L("b.txt.sha256")));
        Assert.True(File.Exists(_fx.L("a.txt.sha256")));
    }

    [AvaloniaFact]
    public async Task It_is_translated_and_drawn_in_both_themes()
    {
        File.WriteAllText(_fx.L("a.txt"), "hello");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        UiLocalization.Install();
        var turkish = UiLocalization.Read("tr")!;
        var shots = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        foreach (var (theme, language) in new[] { ((string?)null, "en"), ("dark", "tr") })
        {
            App.ApplyColorTheme(theme);
            Localizer.Current = language == "tr" ? turkish : null;
            UiLocalization.Untranslated = language == "tr" ? t => missing.Add(t) : null;
            var dialog = await OpenAsync("a.txt");
            dialog.SetExpected(Sha256("hello"));
            await dialog.CreateOrCloseAsync();
            await SettleAsync(4);
            if (shots is { Length: > 0 })
            {
                Directory.CreateDirectory(shots);
                Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog)!.SavePng(Path.Combine(shots, $"checksums-{theme ?? "light"}-{language}.png"));
            }
            UiLocalization.Untranslated = null;
            dialog.Close();
        }
        Localizer.Current = null;
        var english = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Languages", "en.json")))!;
        var kept = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Languages", "tr.json")))!
            .Where(p => english.TryGetValue(p.Key, out var en) && en == p.Value).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
        var words = missing.Where(t => t.Count(char.IsLetter) > 1 && !t.Contains("a.txt", StringComparison.Ordinal) && !t.Contains(_fx.Root, StringComparison.Ordinal)
            && !turkish.IsTranslation(t) && !kept.Contains(t)).ToList();
        Assert.True(words.Count == 0, "Not translated:\n" + string.Join("\n", words));
    }
}
