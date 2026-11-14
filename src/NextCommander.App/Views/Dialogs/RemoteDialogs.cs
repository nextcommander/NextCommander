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

/// <summary>Edits one connection of the connection manager.</summary>
public sealed class ConnectionEditDialog : DialogWindow
{
    private static readonly (string Text, RemoteProtocol Protocol)[] s_protocols =
    [
        ("SFTP (SSH)", RemoteProtocol.Sftp), ("SCP (SSH without SFTP)", RemoteProtocol.Scp), ("FTP", RemoteProtocol.Ftp), ("FTPS (explicit TLS)", RemoteProtocol.FtpsExplicit), ("FTPS (implicit TLS, 990)", RemoteProtocol.FtpsImplicit),
        ("WebDAV (HTTPS)", RemoteProtocol.WebDavs), ("WebDAV (HTTP)", RemoteProtocol.WebDav),
        ("Amazon S3 / compatible (MinIO, R2, Wasabi ...)", RemoteProtocol.S3),
        ("Azure Blob Storage", RemoteProtocol.AzureBlob),
        ("Azure Files", RemoteProtocol.AzureFiles),
        ("SMB (Windows share)", RemoteProtocol.Smb),
        ("Google Drive", RemoteProtocol.GoogleDrive),
        ("OneDrive", RemoteProtocol.OneDrive),
        ("Dropbox", RemoteProtocol.Dropbox),
        ("Box", RemoteProtocol.Box),
    ];

    private readonly TextBox _name = new();
    private readonly ComboBox _protocol = new() { MinWidth = 200 };
    private readonly ComboBox _service = new() { MinWidth = 200, Classes = { "noloc" } };
    private Control? _serviceRow;
    private IReadOnlyList<ConnectionPreset> _presets = [];
    private ConnectionPreset? _preset;
    private readonly TextBox _host = new() { PlaceholderText = "server.example.com" };
    private readonly TextBox _port = new() { PlaceholderText = "default", Width = 100 };
    private readonly TextBox _user = new();
    private readonly CheckBox _savePassword = new() { Content = "Save password (asked on first connect)" };
    private readonly TextBox _keyFile = new() { PlaceholderText = "SFTP private key file (optional)" };
    private readonly TextBox _path = new();
    private readonly CheckBox _passive = new() { Content = "Passive mode (FTP)" };
    private readonly CheckBox _anyCertificate = new() { Content = "Accept any certificate (not recommended)" };
    private readonly CheckBox _sharePoint = new() { Content = "Show SharePoint sites too (work / school account; a \"SharePoint\" folder at the root)" };
    private readonly TextBox _region = new() { PlaceholderText = "empty: from the server name or us-east-1" };
    private readonly CheckBox _plainHttp = new() { Content = "No TLS: plain HTTP (a local MinIO, for example)" };
    private readonly CheckBox _virtualHost = new() { Content = "Virtual-hosted addresses (bucket.server/key instead of path style)" };
    private readonly TextBox _awsProfile = new() { PlaceholderText = "empty: access key and secret key" };
    private readonly TextBox _clientId = new() { PlaceholderText = "id of the registered application" };
    private readonly TextBox _clientSecret = new() { PlaceholderText = "client secret of the Google desktop application" };
    private Control? _hostRow;
    private Control? _clientIdRow;
    private Control? _clientSecretRow;
    private Control? _profileRow;
    private readonly TextBlock _userCaption = new() { Text = "User:", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { Opacity = 0.7, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private Control? _keyRow;
    private Control? _regionRow;
    private readonly TextBlock _error = new() { Foreground = Avalonia.Media.Brushes.IndianRed };
    private readonly RemoteConnection _connection;

    /// <summary>New FTP connections use passive mode (Configuration > FTP).</summary>
    public static bool DefaultPassive { get; set; } = true;

    public ConnectionEditDialog(RemoteConnection? existing, Func<Task<string?>> pickFile, RemoteProtocol? protocol = null)
        : base(existing is null ? "New connection" : "Edit connection", 520)
    {
        _connection = existing?.Clone() ?? new RemoteConnection { Protocol = protocol ?? RemoteProtocol.Sftp, Passive = DefaultPassive };
        _name.Text = _connection.Name;
        _protocol.ItemsSource = s_protocols.Select(p => p.Text).ToList();
        _protocol.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((text, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (text is not null && s_protocols.FirstOrDefault(p => p.Text == text) is { Text: not null } entry)
            {
                row.Children.Add(NextCommander.App.Services.Icons.BrandIcons.Create(entry.Protocol));
            }
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return row;
        });
        _protocol.SelectedIndex = Math.Max(0, Array.FindIndex(s_protocols, p => p.Protocol == _connection.Protocol));
        _host.Text = _connection.Host;
        _port.Text = _connection.Port > 0 ? _connection.Port.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _user.Text = _connection.User;
        _savePassword.IsChecked = _connection.SavePassword;
        _keyFile.Text = _connection.KeyFile;
        _path.Text = _connection.InitialPath;
        _passive.IsChecked = _connection.Passive;
        _anyCertificate.IsChecked = _connection.AcceptAnyCertificate;
        _region.Text = _connection.Region;
        _plainHttp.IsChecked = _connection.PlainHttp;
        _virtualHost.IsChecked = _connection.S3VirtualHost;
        _awsProfile.Text = _connection.AwsProfile;
        _clientId.Text = _connection.ClientId;
        _clientSecret.Text = _connection.ClientSecret;
        _sharePoint.IsChecked = _connection.IncludeSharePoint;
        if (existing is null)
        {
            _savePassword.IsChecked = true; // cloud drives: the sign-in is kept (other protocols: the box is read when saving)
        }
        if (NextCommander.Vfs.Remote.AwsProfiles.Names() is { Count: > 0 } profiles)
        {
            _awsProfile.PlaceholderText = "empty: keys typed in; profiles: " + string.Join(", ", profiles);
        }

        Grid Row(string caption, Control control, Control? extra = null) => RowWith(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center }, control, extra);
        Grid RowWith(Control label, Control control, Control? extra = null)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,Auto"), ColumnSpacing = 8 };
            grid.Children.Add(label);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            if (extra is not null)
            {
                Grid.SetColumn(extra, 2);
                grid.Children.Add(extra);
            }
            return grid;
        }
        var browse = new Button { Content = "..." };
        browse.Click += async (_, _) =>
        {
            if (await pickFile() is { } file)
            {
                _keyFile.Text = file;
            }
        };
        Body.Children.Add(Row("Name:", _name));
        Body.Children.Add(Row("Protocol:", _protocol));
        Body.Children.Add(_serviceRow = Row("Service:", _service));
        Body.Children.Add(_hostRow = Row("Server:", _host, _port));
        Body.Children.Add(RowWith(_userCaption, _user));
        Body.Children.Add(_clientIdRow = Row("Application id:", _clientId));
        Body.Children.Add(_clientSecretRow = Row("Client secret:", _clientSecret));
        Body.Children.Add(_keyRow = Row("Key file:", _keyFile, browse));
        Body.Children.Add(_regionRow = Row("Region:", _region));
        Body.Children.Add(_profileRow = Row("AWS profile:", _awsProfile));
        Body.Children.Add(Row("Start folder:", _path));
        Body.Children.Add(_hint);
        Body.Children.Add(_savePassword);
        Body.Children.Add(_passive);
        Body.Children.Add(_plainHttp);
        Body.Children.Add(_virtualHost);
        Body.Children.Add(_anyCertificate);
        Body.Children.Add(_sharePoint);
        Body.Children.Add(_error);
        AddButton("Save", Accept, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
        _protocol.SelectionChanged += (_, _) =>
        {
            FillServices();
            UpdateProtocolFields();
        };
        _service.SelectionChanged += (_, _) => ApplyService();
        FillServices();
        UpdateProtocolFields();
        Opened += (_, _) => _host.Focus();
    }

    public RemoteConnection? Result { get; private set; }

    /// <summary>The protocol chosen in the dialog.</summary>
    public RemoteProtocol SelectedProtocol => s_protocols[Math.Max(0, _protocol.SelectedIndex)].Protocol;

    /// <summary>The services of the chosen type (S3 compatible stores, WebDAV services); "Other" keeps the fields as typed.</summary>
    private void FillServices()
    {
        _presets = ConnectionPresets.For(SelectedProtocol);
        _preset = null;
        _service.ItemsSource = new[] { Core.Localization.Localizer.T("Other (enter the server)") }.Concat(_presets.Select(p => p.Name)).ToList();
        _service.SelectedIndex = 0;
        _serviceRow!.IsVisible = _presets.Count > 0;
    }

    /// <summary>The service chosen (tests choose one by its name).</summary>
    public string? SelectedService
    {
        get => _preset?.Name;
        set => _service.SelectedIndex = _presets.ToList().FindIndex(p => p.Name == value) + 1;
    }

    /// <summary>A service was chosen: its server, port, region, address style and start folder.</summary>
    private void ApplyService()
    {
        _preset = _service.SelectedIndex > 0 && _service.SelectedIndex <= _presets.Count ? _presets[_service.SelectedIndex - 1] : null;
        if (_preset is { } preset)
        {
            _host.Text = preset.Host;
            _port.Text = preset.Port > 0 ? preset.Port.ToString(CultureInfo.InvariantCulture) : string.Empty;
            _region.Text = preset.Region;
            _virtualHost.IsChecked = preset.VirtualHost;
            _plainHttp.IsChecked = preset.PlainHttp;
            _path.Text = ConnectionPresets.StartFolder(preset, _user.Text);
            if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == "New connection" || _presets.Any(p => p.Name == _name.Text))
            {
                _name.Text = preset.Name;
            }
        }
        UpdateProtocolFields();
    }

    /// <summary>Shows the fields that the chosen protocol uses.</summary>
    private void UpdateProtocolFields()
    {
        var protocol = SelectedProtocol;
        var s3 = protocol == RemoteProtocol.S3;
        _keyRow!.IsVisible = protocol is RemoteProtocol.Sftp or RemoteProtocol.Scp;
        _regionRow!.IsVisible = s3;
        var azure = protocol is RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles;
        _plainHttp.IsVisible = s3 || azure;
        _virtualHost.IsVisible = s3;
        _profileRow!.IsVisible = s3;
        _passive.IsVisible = protocol is RemoteProtocol.Ftp or RemoteProtocol.FtpsExplicit or RemoteProtocol.FtpsImplicit;
        var cloud = RemoteProtocols.Of(protocol).IsCloudDrive;
        _anyCertificate.IsVisible = !cloud && protocol is not (RemoteProtocol.Sftp or RemoteProtocol.Scp or RemoteProtocol.Ftp or RemoteProtocol.WebDav or RemoteProtocol.Smb);
        _hostRow!.IsVisible = !cloud;
        _clientIdRow!.IsVisible = cloud;
        _clientSecretRow!.IsVisible = protocol is RemoteProtocol.GoogleDrive or RemoteProtocol.Box;
        _sharePoint.IsVisible = protocol == RemoteProtocol.OneDrive;
        _userCaption.Text = s3 ? "Access key:" : azure ? "Storage account:" : cloud ? "Account name:" : "User:";
        _user.PlaceholderText = cloud ? "e.g. personal, work (tells several accounts apart)" : null;
        _savePassword.Content = cloud ? "Remember the sign-in (sign in once in the browser)"
            : s3 || azure ? "Save the secret key (asked at the first login)" : "Save password (asked on first connect)";
        _host.PlaceholderText = s3 ? "s3.eu-central-1.amazonaws.com" : protocol == RemoteProtocol.AzureFiles ? "account.file.core.windows.net" : azure ? "account.blob.core.windows.net"
            : protocol is RemoteProtocol.WebDav or RemoteProtocol.WebDavs ? "cloud.example.com" : "server.example.com";
        _hint.Text = protocol == RemoteProtocol.GoogleDrive
            ? "On first use you sign in with your Google account in the browser. For the application id: console.cloud.google.com → new project → enable the Google Drive API → OAuth consent screen → Credentials → OAuth client ID, type: Desktop app. Enter the client id and client secret it gives here."
            : protocol == RemoteProtocol.OneDrive
            ? "On first use you sign in with your Microsoft account (personal or work/school) in the browser. For the application id: entra.microsoft.com → App registrations → New registration, account types: any organizational directory and personal Microsoft accounts; Authentication → Add a platform → Mobile and desktop applications, redirect URI http://localhost. Enter the Application (client) ID here."
            : protocol == RemoteProtocol.Dropbox
            ? "On first use you sign in with your Dropbox account in the browser. For the application id: dropbox.com/developers/apps → Create app → Scoped access, Full Dropbox; Permissions: files.metadata.read/write, files.content.read/write; Settings → Redirect URIs: http://localhost:53682/. Enter the App key here."
            : protocol == RemoteProtocol.Box
            ? "On first use you sign in with your Box account in the browser. For the application id: app.box.com/developers/console → Create Platform App → User Authentication (OAuth 2.0); Configuration: Redirect URI http://localhost:53682/, application scopes: read and write all files and folders. Enter the Client ID and Client Secret here."
            : protocol == RemoteProtocol.Smb
            ? "SMB2/3 shares (Windows, Samba, NAS); they open without mounting on Linux and macOS too. User: DOMAIN\\user or user; empty for a guest login. The root lists the machine's shares; the start folder can be a share (e.g. /documents)."
            : protocol == RemoteProtocol.AzureFiles
            ? "Server: account.file.core.windows.net. The account key or a SAS token is asked as the password. The root lists the file shares; the start folder can be a share (e.g. /documents)."
            : azure
            ? "Server: account.blob.core.windows.net (at another address, such as Azurite, requests go as server/account/container). The account key or a SAS token is asked as the password. The root lists the containers."
            : s3
            ? "Server: the S3 endpoint (AWS, MinIO, R2 ...). User: the access key id; the secret key is asked as the password. With an AWS profile the keys (and the session token of temporary keys) are read from ~/.aws/credentials. The root lists the buckets; the start folder can be a bucket (e.g. /backups)."
            : protocol is RemoteProtocol.WebDav or RemoteProtocol.WebDavs
                ? "The start folder is the WebDAV path of the server, e.g. Nextcloud: /remote.php/dav/files/<user>. Without a user the connection is anonymous."
                : string.Empty;
        if (_preset is { } preset)
        {
            _hint.Text = Core.Localization.Localizer.T(preset.Hint) + "\n\n" + _hint.Text; // the service's own note first
        }
        _hint.IsVisible = _hint.Text.Length > 0;
    }

    private void Accept()
    {
        var cloud = RemoteProtocols.Of(SelectedProtocol).IsCloudDrive;
        var host = cloud ? RemoteConnection.CloudHost(SelectedProtocol) : (_host.Text ?? string.Empty).Trim();
        if (cloud && string.IsNullOrWhiteSpace(_clientId.Text))
        {
            _error.Text = "An application (client) id is needed; how to get one is written below.";
            return;
        }
        if (SelectedProtocol == RemoteProtocol.Box && string.IsNullOrWhiteSpace(_clientSecret.Text))
        {
            _error.Text = "Box needs the client secret of the application too.";
            return;
        }
        if (cloud && string.IsNullOrWhiteSpace(_user.Text))
        {
            _user.Text = "account";
        }
        if (host.Length == 0 || host.Contains('/', StringComparison.Ordinal) || host.Contains(' ', StringComparison.Ordinal))
        {
            _error.Text = "Enter a valid server name (e.g. server.example.com).";
            return;
        }
        if (host.Contains("ACCOUNT_ID", StringComparison.Ordinal) || host.Contains("REGION", StringComparison.Ordinal))
        {
            _error.Text = "Replace the part in capitals of the server name (as the note below says).";
            _host.Focus();
            return;
        }
        var port = 0;
        if (!string.IsNullOrWhiteSpace(_port.Text) && (!int.TryParse(_port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            _error.Text = "Port must be between 1 and 65535.";
            return;
        }
        if (SelectedProtocol == RemoteProtocol.S3 && string.IsNullOrWhiteSpace(_user.Text) && string.IsNullOrWhiteSpace(_awsProfile.Text))
        {
            _error.Text = "S3 needs an access key id or an AWS profile.";
            return;
        }
        if (SelectedProtocol is RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles && string.IsNullOrWhiteSpace(_user.Text))
        {
            _error.Text = "Azure needs the name of the storage account.";
            return;
        }
        _connection.Protocol = SelectedProtocol;
        _connection.Region = (_region.Text ?? string.Empty).Trim();
        _connection.PlainHttp = _plainHttp.IsChecked == true;
        _connection.S3VirtualHost = SelectedProtocol == RemoteProtocol.S3 && _virtualHost.IsChecked == true;
        _connection.AwsProfile = SelectedProtocol == RemoteProtocol.S3 && !string.IsNullOrWhiteSpace(_awsProfile.Text) ? _awsProfile.Text.Trim() : null;
        _connection.Host = host;
        _connection.Port = cloud ? 0 : port;
        _connection.ClientId = cloud ? _clientId.Text?.Trim() : null;
        _connection.IncludeSharePoint = SelectedProtocol == RemoteProtocol.OneDrive && _sharePoint.IsChecked == true;
        _connection.ClientSecret = SelectedProtocol is RemoteProtocol.GoogleDrive or RemoteProtocol.Box && !string.IsNullOrWhiteSpace(_clientSecret.Text) ? _clientSecret.Text.Trim() : null;
        _connection.User = (_user.Text ?? string.Empty).Trim();
        _connection.Name = string.IsNullOrWhiteSpace(_name.Text) || _name.Text == "New connection"
            ? cloud ? $"{s_protocols.First(p => p.Protocol == SelectedProtocol).Text} ({_connection.User})"
            : (_connection.User.Length > 0 && !_connection.IsObjectStorage ? $"{_connection.User}@{host}" : host)
            : _name.Text.Trim();
        _connection.SavePassword = _savePassword.IsChecked == true;
        _connection.KeyFile = string.IsNullOrWhiteSpace(_keyFile.Text) || _connection.Protocol is not (RemoteProtocol.Sftp or RemoteProtocol.Scp) ? null : _keyFile.Text.Trim();
        var path = (_path.Text ?? string.Empty).Replace("<user>", Uri.EscapeDataString(_connection.User), StringComparison.Ordinal);
        _connection.InitialPath = string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.Trim().Trim('/');
        _connection.Passive = _passive.IsChecked == true;
        _connection.AcceptAnyCertificate = _anyCertificate.IsChecked == true;
        Result = _connection;
        Close();
    }
}

/// <summary>Ctrl+F: saved connections; returns the one to connect to. The list is edited in place.</summary>
public sealed class ConnectionManagerDialog : DialogWindow
{
    private readonly List<RemoteConnection> _connections;
    private readonly ListBox _list = new() { Height = 260 };
    private readonly Func<RemoteConnection?, Task<RemoteConnection?>> _edit;

    public ConnectionManagerDialog(List<RemoteConnection> connections, Func<RemoteConnection?, Task<RemoteConnection?>> edit, Func<RemoteConnection, Task> forgetPassword)
        : base("Connection manager", 620)
    {
        _connections = connections;
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RemoteConnection>((c, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (c is null)
            {
                return row;
            }
            row.Children.Add(NextCommander.App.Services.Icons.BrandIcons.Create(c.Protocol, 18));
            row.Children.Add(new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
            row.Children.Add(new TextBlock
            {
                Text = $"{c.Scheme}://{c.Authority}{c.InitialPath}", Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" },
            });
            return row;
        });
        _edit = edit;
        Body.Children.Add(_list);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Button B(string text, Func<Task> action)
        {
            var b = new Button { Content = text };
            b.Click += async (_, _) => await action();
            tools.Children.Add(b);
            return b;
        }
        B("New...", async () =>
        {
            if (await _edit(null) is { } created)
            {
                _connections.Add(created);
                Refresh(created);
            }
        });
        B("Edit...", async () =>
        {
            if (Selected is { } c && await _edit(c) is { } edited)
            {
                _connections[_connections.IndexOf(c)] = edited;
                Refresh(edited);
            }
        });
        B("Copy", () =>
        {
            if (Selected is { } c)
            {
                var copy = c.Clone();
                copy.Name += " (kopya)";
                _connections.Add(copy);
                Refresh(copy);
            }
            return Task.CompletedTask;
        });
        B("Delete", async () =>
        {
            if (Selected is { } c)
            {
                _connections.Remove(c);
                await forgetPassword(c);
                Refresh(null);
            }
        });
        B("Forget saved password", async () =>
        {
            if (Selected is { } c)
            {
                await forgetPassword(c);
            }
        });
        Body.Children.Add(tools);
        _list.DoubleTapped += (_, _) => Connect();
        AddButton("Connect", Connect, isDefault: true);
        AddButton("Close", Close, isCancel: true);
        Refresh(connections.FirstOrDefault());
        Opened += (_, _) => _list.Focus();
    }

    public RemoteConnection? Result { get; private set; }

    private RemoteConnection? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _connections.Count ? _connections[_list.SelectedIndex] : null;

    private void Refresh(RemoteConnection? select)
    {
        _list.ItemsSource = _connections.ToList();
        _list.SelectedIndex = select is null ? (_connections.Count > 0 ? 0 : -1) : _connections.IndexOf(select);
    }

    private void Connect()
    {
        if (Selected is { } c)
        {
            Result = c;
            Close();
        }
    }
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
