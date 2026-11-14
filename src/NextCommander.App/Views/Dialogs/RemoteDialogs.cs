using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Password / key passphrase for a server, with "remember".</summary>
public sealed class LoginDialog : DialogWindow
{
    private readonly TextBox _password = new() { PasswordChar = '●' };
    private readonly CheckBox _save = new() { Content = "Save password (encrypted)" };

    public LoginDialog(RemoteConnection connection, bool retry)
        : base("Login", 440)
    {
        var what = connection.Protocol switch
        {
            RemoteProtocol.S3 => "Secret access key",
            RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles => "Account key or SAS token (sv=...&sig=...)",
            _ => string.IsNullOrEmpty(connection.KeyFile) ? "Password" : "Key file passphrase",
        };
        Body.Children.Add(Label($"{connection.User}@{connection.Host}:{connection.EffectivePort}", bold: true));
        if (retry)
        {
            Body.Children.Add(new TextBlock { Text = "Login failed: the password may be wrong.", Foreground = Avalonia.Media.Brushes.IndianRed });
        }
        Body.Children.Add(Label(what + ":"));
        Body.Children.Add(_password);
        _save.IsChecked = connection.SavePassword;
        Body.Children.Add(_save);
        AddButton("Connect", () =>
        {
            Result = (_password.Text ?? string.Empty, _save.IsChecked == true);
            Close();
        }, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
        Opened += (_, _) => _password.Focus();
    }

    public (string Password, bool Save)? Result { get; private set; }
}

/// <summary>Unknown / changed server key or certificate.</summary>
public sealed class FingerprintDialog : DialogWindow
{
    public FingerprintDialog(string host, string fingerprint, string description, bool changed)
        : base(changed ? "WARNING: server key changed" : "Unknown server", 560)
    {
        Body.Children.Add(Label(changed
            ? $"The key/certificate of {host} is DIFFERENT from the one saved earlier. This may mean the server was reinstalled, but someone may also be intercepting the connection (MITM)."
            : $"Connecting to {host} for the first time. Verify the fingerprint with the server administrator.", bold: changed));
        Body.Children.Add(Label(description));
        Body.Children.Add(new SelectableTextBlock { Text = fingerprint, FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono, Consolas, monospace"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        AddButton("Trust and save", () =>
        {
            Result = true;
            Close();
        }, isDefault: !changed);
        AddButton("Cancel", Close, isCancel: true);
    }

    public bool Result { get; private set; }
}

/// <summary>Sets / changes / removes the master password.</summary>
public sealed class MasterPasswordDialog : DialogWindow
{
    private readonly TextBox _current = new() { PasswordChar = '●', PlaceholderText = "Current master password" };
    private readonly TextBox _new = new() { PasswordChar = '●', PlaceholderText = "New master password (empty = remove)" };
    private readonly TextBox _repeat = new() { PasswordChar = '●', PlaceholderText = "Repeat new master password" };
    private readonly TextBlock _error = new() { Foreground = Avalonia.Media.Brushes.IndianRed };

    public MasterPasswordDialog(bool hasMaster)
        : base("Master password", 460)
    {
        Body.Children.Add(Label("The master password encrypts stored FTP/SFTP passwords. If set, it is asked once per session. "
            + "If not set, passwords are protected with a key tied to the user account on this computer."));
        if (hasMaster)
        {
            Body.Children.Add(_current);
        }
        Body.Children.Add(_new);
        Body.Children.Add(_repeat);
        Body.Children.Add(_error);
        AddButton("Save", () =>
        {
            if (_new.Text != _repeat.Text)
            {
                _error.Text = "The new passwords do not match.";
                return;
            }
            Result = (hasMaster ? _current.Text ?? string.Empty : null, _new.Text);
            Close();
        }, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
    }

    public (string? Current, string? New)? Result { get; private set; }
}
