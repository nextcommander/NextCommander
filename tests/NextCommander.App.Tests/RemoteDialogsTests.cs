using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Connection editor: WebDAV and S3 with the fields of their protocol.</summary>
public sealed class RemoteDialogsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Connection_editor_offers_webdav_and_s3()
    {
        var dialog = _fx.Track(new ConnectionEditDialog(null, () => Task.FromResult<string?>(null)));
        dialog.Show();
        await SettleAsync();
        var combo = dialog.GetVisualDescendants().OfType<ComboBox>().First();
        var protocols = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        Assert.Contains(protocols, p => p.StartsWith("WebDAV", StringComparison.Ordinal));
        Assert.Contains(protocols, p => p.Contains("S3", StringComparison.Ordinal));

        combo.SelectedIndex = protocols.FindIndex(p => p.Contains("S3", StringComparison.Ordinal));
        await SettleAsync();
        Assert.Equal(RemoteProtocol.S3, dialog.SelectedProtocol);
        bool Shown(string text) => dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);
        Assert.True(Shown("Region:"));
        Assert.True(Shown("Access key:"));
        Assert.False(Shown("Key file:"));

        var boxes = dialog.GetVisualDescendants().OfType<TextBox>().ToList();
        TextBox Box(string watermark) => boxes.First(b => b.PlaceholderText == watermark);
        Box("s3.eu-central-1.amazonaws.com").Text = "127.0.0.1";
        Box("default").Text = "9000";
        Box("empty: from the server name or us-east-1").Text = "eu-west-1";
        boxes[boxes.IndexOf(Box("default")) + 1].Text = "AKIAEXAMPLE"; // the user follows the port
        dialog.GetVisualDescendants().OfType<CheckBox>().First(c => Equals(c.Content, "No TLS: plain HTTP (a local MinIO, for example)")).IsChecked = true;
        dialog.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var error = dialog.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Foreground == Avalonia.Media.Brushes.IndianRed)?.Text;
        Assert.True(dialog.Result is not null, error + " | " + string.Join(", ", boxes.Select(b => b.PlaceholderText + "=" + b.Text)));
        var result = dialog.Result!;
        Assert.Equal((RemoteProtocol.S3, "s3", "AKIAEXAMPLE", "eu-west-1", true, 9000), (result.Protocol, result.Scheme, result.User, result.Region, result.PlainHttp, result.Port));
        Assert.Equal("s3://AKIAEXAMPLE@127.0.0.1:9000", result.SecretId);
        Assert.Null(result.KeyFile);
    }

    [AvaloniaFact]
    public async Task Services_fill_in_the_server()
    {
        // S3 compatible: Cloudflare R2 needs the account id in the server name
        var dialog = _fx.Track(new ConnectionEditDialog(null, () => Task.FromResult<string?>(null), RemoteProtocol.S3));
        dialog.Show();
        await SettleAsync();
        var boxes = dialog.GetVisualDescendants().OfType<TextBox>().ToList();
        TextBox Box(string watermark) => boxes.First(b => b.PlaceholderText == watermark);
        var host = Box("s3.eu-central-1.amazonaws.com");
        dialog.SelectedService = "Cloudflare R2";
        await SettleAsync();
        Assert.Equal("ACCOUNT_ID.r2.cloudflarestorage.com", host.Text);
        Assert.Equal("auto", Box("empty: from the server name or us-east-1").Text);
        boxes[boxes.IndexOf(Box("default")) + 1].Text = "key";
        void Save() => dialog.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Save();
        Assert.Null(dialog.Result); // the part in capitals is still there
        host.Text = "0123abcd.r2.cloudflarestorage.com";
        Save();
        Assert.Equal(("0123abcd.r2.cloudflarestorage.com", "auto", "Cloudflare R2"), (dialog.Result!.Host, dialog.Result.Region, dialog.Result.Name));

        // WebDAV: Nextcloud's start folder is the user's files
        var dav = _fx.Track(new ConnectionEditDialog(null, () => Task.FromResult<string?>(null), RemoteProtocol.WebDavs));
        dav.Show();
        await SettleAsync();
        var davBoxes = dav.GetVisualDescendants().OfType<TextBox>().ToList();
        dav.SelectedService = "Nextcloud / ownCloud";
        await SettleAsync();
        davBoxes.First(b => b.PlaceholderText == "cloud.example.com").Text = "cloud.example.org";
        davBoxes[davBoxes.IndexOf(davBoxes.First(b => b.PlaceholderText == "default")) + 1].Text = "ayşe";
        dav.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("/remote.php/dav/files/ay%C5%9Fe", dav.Result!.InitialPath);

        // no services for SFTP: the row is hidden
        var sftp = _fx.Track(new ConnectionEditDialog(null, () => Task.FromResult<string?>(null), RemoteProtocol.Sftp));
        sftp.Show();
        await SettleAsync();
        Assert.DoesNotContain(sftp.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Service:" && t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Cloud_drives_ask_for_the_client_id_instead_of_a_server()
    {
        var dialog = _fx.Track(new ConnectionEditDialog(null, () => Task.FromResult<string?>(null)));
        dialog.Show();
        await SettleAsync();
        var combo = dialog.GetVisualDescendants().OfType<ComboBox>().First();
        var protocols = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        bool Shown(string text) => dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);
        TextBox Box(string watermark) => dialog.GetVisualDescendants().OfType<TextBox>().First(b => b.PlaceholderText == watermark);
        void Save() => dialog.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        combo.SelectedIndex = protocols.IndexOf("Google Drive");
        await SettleAsync();
        Assert.Equal(RemoteProtocol.GoogleDrive, dialog.SelectedProtocol);
        Assert.False(Shown("Server:"));
        Assert.True(Shown("Application id:"));
        Assert.True(Shown("Client secret:"));
        Assert.True(Shown("Account name:"));
        Save();
        Assert.Null(dialog.Result); // no client id yet

        Box("id of the registered application").Text = " 123.apps.googleusercontent.com ";
        Box("client secret of the Google desktop application").Text = "GOCSPX-x";
        Box("e.g. personal, work (tells several accounts apart)").Text = "kisisel";
        Save();
        var result = dialog.Result!;
        Assert.Equal(("gdrive", "drive.google.com", "kisisel", "123.apps.googleusercontent.com", "GOCSPX-x", true),
            (result.Scheme, result.Host, result.User, result.ClientId, result.ClientSecret, result.SavePassword));
        Assert.Equal("gdrive://kisisel@drive.google.com", result.SecretId);
        Assert.Equal("Google Drive (kisisel)", result.Name);

        // OneDrive: no client secret field
        var onedrive = _fx.Track(new ConnectionEditDialog(null, () => Task.FromResult<string?>(null)));
        onedrive.Show();
        await SettleAsync();
        onedrive.GetVisualDescendants().OfType<ComboBox>().First().SelectedIndex = protocols.IndexOf("OneDrive");
        await SettleAsync();
        Assert.DoesNotContain(onedrive.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Client secret:" && t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Connection_manager_lists_connections_with_their_marks()
    {
        var connections = new[]
        {
            (RemoteProtocol.Sftp, "server.example.com"), (RemoteProtocol.Ftp, "ftp.ornek.com"), (RemoteProtocol.WebDavs, "cloud.example.com"),
            (RemoteProtocol.Smb, "nas"), (RemoteProtocol.S3, "s3.amazonaws.com"), (RemoteProtocol.AzureBlob, "account.blob.core.windows.net"),
            (RemoteProtocol.GoogleDrive, "drive.google.com"), (RemoteProtocol.OneDrive, "onedrive.live.com"), (RemoteProtocol.Dropbox, "dropbox.com"),
            (RemoteProtocol.Box, "box.com"),
        }.Select(c => new RemoteConnection { Name = c.Item1.ToString(), Protocol = c.Item1, Host = c.Item2, User = "ben" }).ToList();
        var dialog = _fx.Track(new ConnectionManagerDialog(connections, _ => Task.FromResult<RemoteConnection?>(null), _ => Task.CompletedTask));
        dialog.Show();
        await SettleAsync();
        var list = dialog.GetVisualDescendants().OfType<ListBox>().First();
        Assert.Equal(10, list.GetVisualDescendants().OfType<ListBoxItem>().Count());
        Assert.Equal(6, list.GetVisualDescendants().OfType<Viewbox>().Count(v => v.Child is Canvas)); // product marks: S3, Azure, Drive, OneDrive, Dropbox, Box
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog)!.SavePng(Path.Combine(dir, "connection-manager.png"));
        }
    }
}
