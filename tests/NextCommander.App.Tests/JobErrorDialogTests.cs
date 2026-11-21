using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Localization;
using NextCommander.Core.Operations;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The error question of an operation (the user's template): each kind explained, its default and choices.</summary>
[Collection("Appearance")]
public sealed class JobErrorDialogTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        Localizer.Current = null;
        App.ApplyColorTheme(null);
        _fx.Dispose();
    }

    private JobErrorRequest Request(ErrorKind kind, string item, int code = 0) => new(new JobError(item, "The process cannot access the file.")
    {
        Kind = kind, Code = code, Operation = "Delete", DoneFiles = 5, TotalFiles = 14,
    })
    {
        LockedBy = _ => ["Microsoft Excel"],
        OwnLogFolder = _fx.L("logs"),
        OpenLogSetting = () => Task.CompletedTask,
        ShowProperties = _ => Task.CompletedTask,
    };

    private async Task<JobErrorDialog> ShowAsync(JobErrorRequest request)
    {
        var dialog = _fx.Track(new JobErrorDialog(request));
        dialog.Show();
        await SettleAsync(4);
        return dialog;
    }

    [AvaloniaFact]
    public async Task Each_kind_is_explained_with_its_default()
    {
        Directory.CreateDirectory(_fx.L("logs"));
        File.WriteAllText(_fx.L("logs", "nextcommander-20261121.log"), "log");

        // the program's own log: in use by the program itself; Skip, and how to stop the log
        var own = await ShowAsync(Request(ErrorKind.InUse, _fx.L("logs", "nextcommander-20261121.log"), 32));
        Assert.Equal(ErrorAction.Skip, own.Primary);
        Assert.Contains("today's program log", own.Text, StringComparison.Ordinal);
        Assert.Contains("File 6 of 14 · 5 done", own.Text, StringComparison.Ordinal);
        own.SameForNext = true;
        own.Choose(ErrorAction.Skip);
        Assert.Equal(ErrorAction.SkipAllOfKind, own.Result);

        // another program: named, Retry first
        var other = await ShowAsync(Request(ErrorKind.InUse, _fx.L("a.txt"), 32));
        Assert.Equal(ErrorAction.Retry, other.Primary);
        Assert.Contains("Open in Microsoft Excel", other.Text, StringComparison.Ordinal);
        Assert.Contains("Close Microsoft Excel and choose Retry.", other.Text, StringComparison.Ordinal);
        other.Close();
        Assert.Equal(ErrorAction.Cancel, other.Result);

        // no permission: Retry as administrator first
        var denied = await ShowAsync(Request(ErrorKind.AccessDenied, _fx.L("a.txt"), 5));
        Assert.Equal(ErrorAction.Elevate, denied.Primary);
        Assert.Contains("0x05 · ERROR_ACCESS_DENIED", denied.Text, StringComparison.Ordinal);
        denied.Close();

        // a lost connection: it retries by itself after the countdown
        var network = await ShowAsync(Request(ErrorKind.NetworkLost, @"\\nas\media\video.mp4", 64) with { AutoRetrySeconds = 1 });
        await WaitUntilAsync(() => !network.IsVisible, 8000);
        Assert.Equal(ErrorAction.Retry, network.Result);
    }

    [AvaloniaFact]
    public async Task It_is_translated_and_drawn_in_both_themes()
    {
        File.WriteAllText(_fx.L("Budget 2027.xlsx"), "x");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        UiLocalization.Install();
        var turkish = UiLocalization.Read("tr")!;
        var shots = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        foreach (var (theme, language) in new[] { ((string?)null, "en"), ("dark", "tr") })
        {
            App.ApplyColorTheme(theme);
            Localizer.Current = language == "tr" ? turkish : null;
            UiLocalization.Untranslated = language == "tr" ? t => missing.Add(t) : null;
            foreach (var (kind, code, name) in new[] { (ErrorKind.InUse, 32, "inuse"), (ErrorKind.AccessDenied, 5, "denied"), (ErrorKind.DiskFull, 112, "full"), (ErrorKind.NetworkLost, 64, "network"), (ErrorKind.NotFound, 2, "notfound"), (ErrorKind.Other, 1117, "other") })
            {
                var dialog = await ShowAsync(Request(kind, _fx.L("Budget 2027.xlsx"), code) with { AutoRetrySeconds = kind == ErrorKind.NetworkLost ? 30 : 0 });
                if (shots is { Length: > 0 })
                {
                    Directory.CreateDirectory(shots);
                    Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog)!.SavePng(Path.Combine(shots, $"error-{name}-{theme ?? "light"}-{language}.png"));
                }
                dialog.Close();
            }
            UiLocalization.Untranslated = null;
        }
        Localizer.Current = null;
        var english = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Languages", "en.json")))!;
        var kept = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Languages", "tr.json")))!
            .Where(p => english.TryGetValue(p.Key, out var en) && en == p.Value).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
        string[] data = ["Budget 2027", _fx.Root, "Microsoft Excel", "ERROR_", "The process cannot access the file."];
        var words = missing.Where(t => t.Count(char.IsLetter) > 1 && !data.Any(d => t.Contains(d, StringComparison.Ordinal))
            && !turkish.IsTranslation(t) && !kept.Contains(t)).ToList();
        Assert.True(words.Count == 0, "Not translated:\n" + string.Join("\n", words));
    }
}

/// <summary>The program holding a file, through Windows' Restart Manager.</summary>
public sealed class FileLocksTests
{
    [Fact]
    public void The_program_that_has_a_file_open_is_named()
    {
        var path = Path.Combine(Path.GetTempPath(), "nc-lock-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        try
        {
            using (var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var names = NextCommander.Platform.Windows.FileLocks.WhoLocks(path);
                if (OperatingSystem.IsWindows())
                {
                    Assert.NotEmpty(names); // this test's own process
                }
                else
                {
                    Assert.Empty(names);
                }
            }
            Assert.Empty(NextCommander.Platform.Windows.FileLocks.WhoLocks(path)); // closed: nobody
        }
        finally
        {
            File.Delete(path);
        }
    }
}
