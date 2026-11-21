using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Services;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Logins shared by several connections.</summary>
public sealed class SharedCredentialTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static AppSettings Settings(out SharedCredential admin)
    {
        var settings = new AppSettings();
        admin = new SharedCredential { Name = "Domain admin", User = "CORP\\admin" };
        settings.SharedCredentials.Add(admin);
        settings.Connections.Add(new RemoteConnection { Name = "web1", Protocol = RemoteProtocol.Sftp, Host = "web1", User = "old" });
        settings.Connections.Add(new RemoteConnection { Name = "web2", Protocol = RemoteProtocol.Smb, Host = "web2" });
        settings.Connections.Add(new RemoteConnection { Name = "drive", Protocol = RemoteProtocol.GoogleDrive });
        return settings;
    }

    [AvaloniaFact]
    public async Task Connections_with_one_login_share_its_user_key_and_password()
    {
        await _fx.OpenAsync();
        var settings = Settings(out var admin);
        var (web1, web2) = (settings.Connections[0], settings.Connections[1]);
        Assert.False(SharedCredential.CanUse(settings.Connections[2])); // a cloud drive signs in in the browser
        admin.KeyFile = @"C:\keys\admin.ppk";
        SharedCredential.Use(web1, admin);
        SharedCredential.Use(web2, admin);
        Assert.Equal(("CORP\\admin", @"C:\keys\admin.ppk", true), (web1.User, web1.KeyFile, web1.SavePassword));
        Assert.Null(web2.KeyFile); // SMB logs in with the password
        Assert.Equal("credential:" + admin.Id, web1.PasswordSecretId);
        Assert.Equal(web1.PasswordSecretId, web2.PasswordSecretId);

        // the password typed for one is saved once and used by the other without asking
        var secrets = new SecretStore(Path.Combine(_fx.Root, "secrets"));
        var session = new RemoteSession(settings, secrets, _fx.Dialogs);
        _fx.Dialogs.Logins.Enqueue(("pa55", true));
        Assert.Equal("pa55", await session.Hooks.GetPassword!(web1.Clone(), false));
        await session.Hooks.LoginSucceeded!(web1.Clone(), "pa55");
        Assert.True(secrets.Contains(admin.SecretId));
        Assert.False(secrets.Contains(web1.SecretId));
        Assert.Equal("pa55", await session.Hooks.GetPassword!(web2.Clone(), false));

        // the login changes its user: the connections follow; deleted: they keep the user and log in on their own
        admin.User = "CORP\\root";
        SharedCredential.Apply(settings);
        Assert.Equal("CORP\\root", web2.User);
        settings.SharedCredentials.Clear();
        SharedCredential.Apply(settings);
        Assert.Equal((null, "CORP\\root"), (web2.CredentialId, web2.User));
        Assert.Equal(web2.SecretId, web2.PasswordSecretId);

        // an exported file has the user, not the login of this computer
        SharedCredential.Use(web1, admin);
        Assert.DoesNotContain(admin.Id, ConnectionImport.Export(settings), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_logins_dialog_and_the_editor_choice()
    {
        var settings = Settings(out var admin);
        var stored = new Dictionary<string, string>();
        var saves = 0;
        var view = new ConnectionManagerView(new ConnectionManagerRequest(settings, () => saves++, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null))
        {
            HasSecret = stored.ContainsKey,
            StoreSecret = (id, value) =>
            {
                if (value.Length == 0)
                {
                    stored.Remove(id);
                }
                else
                {
                    stored[id] = value;
                }
                return Task.CompletedTask;
            },
        });
        var window = new Window { Content = view, Width = 1100, Height = 760 };
        window.Show();
        view.Reveal(settings.Connections[1]);
        await SettleAsync();

        // the editor's Login choice: own, or a shared login; a shared one replaces the user field
        var login = window.GetLogicalDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Login");
        Assert.Equal(["Own user and password", "Domain admin"], login.ItemsSource!.Cast<string>());
        login.SelectedIndex = 1;
        await SettleAsync();
        Assert.Equal((admin.Id, "CORP\\admin"), (settings.Connections[1].CredentialId, settings.Connections[1].User));
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("of the login \"Domain admin\"", StringComparison.Ordinal) == true);

        // the dialog: problems keep it open; OK stores the password and updates the connections
        var dialog = new SharedLoginsDialog(settings, stored.ContainsKey, (id, value) =>
        {
            stored[id] = value;
            return Task.CompletedTask;
        }, _ => Task.FromResult<string?>(null));
        dialog.Show();
        await SettleAsync();
        var added = dialog.Add();
        await SettleAsync();
        Assert.Contains("needs a user", dialog.Problem(), StringComparison.Ordinal);
        added.Name = "Domain admin";
        added.User = "x";
        Assert.Contains("Two logins", dialog.Problem(), StringComparison.Ordinal);
        added.Name = "Web deploy";
        dialog.Logins[0].User = "CORP\\ops";
        dialog.SetPassword(dialog.Logins[0], "s3cret");
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog)!.SavePng(Path.Combine(shots, "shared-logins.png"));
        }
        await dialog.AcceptAsync();
        Assert.True(dialog.Saved);
        Assert.Equal(["Domain admin", "Web deploy"], settings.SharedCredentials.Select(l => l.Name));
        Assert.Equal("s3cret", stored["credential:" + admin.Id]);
        Assert.Equal("CORP\\ops", settings.Connections[1].User);
        window.Close();
    }
    [AvaloniaFact]
    public async Task The_new_connection_wizard_offers_the_shared_logins()
    {
        var settings = Settings(out var admin);
        admin.KeyFile = "admin.ppk";
        var wizard = new ConnectionWizard(new ConnectionWizardRequest(settings, (_, _) => Task.FromResult(string.Empty), _ => Task.FromResult<string?>(null)));
        wizard.Show();
        await SettleAsync();
        wizard.ChooseType(RemoteProtocol.Sftp);
        await SettleAsync();
        ComboBox Login() => wizard.GetLogicalDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Login");
        bool HasBox(string name) => wizard.GetLogicalDescendants().OfType<TextBox>().Any(t => t.PlaceholderText == name);
        Assert.Equal(["Own user and password", "Domain admin"], Login().ItemsSource!.Cast<string>());
        Assert.True(HasBox("user name"));

        // a shared login: its user and key, no user or key fields; back to its own: they come again, empty
        Login().SelectedIndex = 1;
        await SettleAsync();
        Assert.Equal((admin.Id, "CORP\\admin", "admin.ppk"), (wizard.Connection!.CredentialId, wizard.Connection.User, wizard.Connection.KeyFile));
        Assert.False(HasBox("user name"));
        Assert.Contains(wizard.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("User CORP\\admin and the password", StringComparison.Ordinal) == true);
        Login().SelectedIndex = 0;
        await SettleAsync();
        Assert.Equal((null, string.Empty), (wizard.Connection!.CredentialId, wizard.Connection.User));
        Assert.True(HasBox("user name"));

        // without shared logins the choice is not offered
        Login().SelectedIndex = 1;
        await SettleAsync();
        wizard.Connection!.Host = "web1";
        wizard.Connection.Name = "web new";
        Assert.True(wizard.Finish());
        Assert.Equal(admin.Id, wizard.Result!.CredentialId);
        settings.SharedCredentials.Clear();
        wizard.ChooseType(RemoteProtocol.Ftp);
        await SettleAsync();
        Assert.DoesNotContain(wizard.GetLogicalDescendants().OfType<ComboBox>(), c => Avalonia.Automation.AutomationProperties.GetName(c) == "Login");
        wizard.Close();
    }
}
