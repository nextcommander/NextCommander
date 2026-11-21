using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// The connection manager's pages in Turkish: every text shown is translated. With FM_SCREENSHOT_DIR the pages are
/// saved in the light and the dark theme (connection-look-*.png) to compare them with the design.
/// </summary>
[Collection("Appearance")]
public sealed class ConnectionManagerLookTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        Localizer.Current = null;
        App.ApplyColorTheme(null);
        _fx.Dispose();
    }

    private static AppSettings Sample()
    {
        var settings = new AppSettings();
        settings.Connections.Add(new RemoteConnection { Name = "web-prod", Protocol = RemoteProtocol.Sftp, Host = "web.example.com", User = "deploy", Folder = "Work", LastUsed = DateTime.Now.AddHours(-2), Favorite = true });
        settings.Connections.Add(new RemoteConnection { Name = "nas", Protocol = RemoteProtocol.Smb, Host = "nas.local", Folder = "Home", LastUsed = DateTime.Now.AddDays(-1) });
        settings.Connections.Add(new RemoteConnection { Name = "backups", Protocol = RemoteProtocol.S3, Host = "s3.amazonaws.com", Region = "eu-central-1" });
        settings.Connections.Add(new RemoteConnection { Name = "old-ftp", Protocol = RemoteProtocol.Ftp, Host = "ftp.example.com" });
        return settings;
    }

    private (ConnectionManagerView View, Window Window) Open(AppSettings settings)
    {
        var request = new ConnectionManagerRequest(settings, () => { }, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null));
        var view = new ConnectionManagerView(request) { ConfirmDeletes = false };
        var window = _fx.Track(new Window { Content = view, Width = 1280, Height = 820 });
        window.Show();
        return (view, window);
    }

    /// <summary>The pages one after the other: (name, how to get there).</summary>
    private static async IAsyncEnumerable<string> Pages(ConnectionManagerView view, AppSettings settings, bool firstStart)
    {
        if (firstStart)
        {
            yield return "first-start";
            yield break;
        }
        yield return "home";
        view.Reveal(settings.Connections[0]);
        await SettleAsync();
        yield return "connection";
        view.Reveal(null, "Work");
        await SettleAsync();
        yield return "folder";
        var adding = view.NewConnectionAsync();
        await SettleAsync();
        yield return "types";
        view.Wizard!.ChooseType(RemoteProtocol.Sftp);
        await SettleAsync();
        yield return "details";
        foreach (var expander in view.Wizard.GetVisualDescendants().OfType<Expander>())
        {
            expander.IsExpanded = true;
            expander.BringIntoView();
            await SettleAsync();
            foreach (var scroller in view.Wizard.GetVisualDescendants().OfType<ScrollViewer>())
            {
                scroller.ScrollToEnd();
            }
        }
        await SettleAsync();
        yield return "details-more";
        view.Wizard.Close();
        await adding;
        await SettleAsync();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_page_is_translated_and_drawn_in_both_themes(bool firstStart)
    {
        var shots = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        UiLocalization.Install();
        var turkish = UiLocalization.Read("tr")!;
        foreach (var (theme, language) in new[] { ((string?)null, "en"), ("dark", "tr") })
        {
            App.ApplyColorTheme(theme);
            Localizer.Current = language == "tr" ? turkish : null;
            UiLocalization.Untranslated = language == "tr" ? t => missing.Add(t) : null;
            var settings = firstStart ? new AppSettings() : Sample();
            var (view, window) = Open(settings);
            await SettleAsync();
            await foreach (var page in Pages(view, settings, firstStart))
            {
                await SettleAsync();
                if (shots is { Length: > 0 })
                {
                    Directory.CreateDirectory(shots);
                    Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(shots, $"connection-look-{page}-{theme ?? "light"}-{language}.png"));
                }
            }
            UiLocalization.Untranslated = null;
            window.Close();
        }
        Localizer.Current = null;

        // names, addresses, sample values and numbers are not translated; a text the language file keeps as it is ("Port") is
        string[] data = ["web-prod", "nas", "backups", "old-ftp", "Work", "Home", "deploy", "web.example.com", "nas.local", "s3.amazonaws.com", "ftp.example.com", "eu-central-1", "FileZilla", "WinSCP", "PuTTY", "MobaXterm", "SHA256:", "bastion.example.com"];
        var languages = Path.Combine(AppContext.BaseDirectory, "Languages");
        var english = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(languages, "en.json")))!;
        var kept = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(languages, "tr.json")))!
            .Where(p => english.TryGetValue(p.Key, out var en) && en == p.Value).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
        var words = missing.Where(t => t.Count(char.IsLetter) > 1 && !data.Any(d => t.Contains(d, StringComparison.Ordinal))
            && !t.Contains("://", StringComparison.Ordinal) && !turkish.IsTranslation(t) && !kept.Contains(t)).ToList();
        Assert.True(words.Count == 0, "Not translated:\n" + string.Join("\n", words));
    }
}
