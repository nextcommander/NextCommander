using Avalonia.Headless.XUnit;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Exporting connections to a file in the connection manager and importing such a file.</summary>
public sealed class ConnectionExportTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task A_folder_is_exported_without_passwords_and_imported_again()
    {
        var settings = new AppSettings();
        settings.ConnectionFolders.Add(new ConnectionFolder { Path = "Work", Color = "#2E9E44" });
        settings.Connections.Add(new RemoteConnection { Name = "web", Folder = "Work", Protocol = RemoteProtocol.Sftp, Host = "web", User = "me", SavePassword = true });
        settings.Connections.Add(new RemoteConnection { Name = "nas", Protocol = RemoteProtocol.Smb, Host = "nas" });
        var target = Path.Combine(_fx.Root, "work.json");
        var saves = 0;
        string? suggested = null;
        var view = new ConnectionManagerView(new ConnectionManagerRequest(settings, () => saves++, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null))
        {
            PickSaveFile = (_, name) =>
            {
                suggested = name;
                return Task.FromResult<string?>(target);
            },
        });
        var window = new Avalonia.Controls.Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        await SettleAsync();

        await view.ExportAsync("Work");
        Assert.Equal("Work connections.json", suggested);
        var text = await File.ReadAllTextAsync(target);
        Assert.Contains("\"web\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"nas\"", text, StringComparison.Ordinal);

        // imported into another installation: the folder with its color, the password not saved
        var other = new AppSettings();
        var otherView = new ConnectionManagerView(new ConnectionManagerRequest(other, () => saves++, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null)));
        window.Content = otherView;
        await SettleAsync();
        await otherView.ImportFileAsync(target);
        var web = Assert.Single(other.Connections);
        Assert.Equal(("web", "Work", false), (web.Name, web.Folder, web.SavePassword));
        Assert.Contains(other.ConnectionFolders, f => f is { Path: "Work", Color: "#2E9E44" });
        Assert.Equal("web", otherView.Current!.Name);

        // the same file again: a free name
        await otherView.ImportFileAsync(target);
        Assert.Equal(["web", "web (2)"], other.Connections.Select(c => c.Name));
        Assert.True(saves >= 2);
        window.Close();
    }
}
