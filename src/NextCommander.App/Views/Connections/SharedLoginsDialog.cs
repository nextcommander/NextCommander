using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;

namespace NextCommander.App.Views.Connections;

/// <summary>
/// The logins shared by several connections (Logins... in the connection editor): a
/// list on the left, the name, user, password and key file of the chosen one on the right, and the connections that use
/// it. Works on copies; OK writes the list back, stores the passwords typed and gives the connections their logins'
/// users and keys again.
/// </summary>
public sealed class SharedLoginsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly Func<string, bool>? _hasSecret;
    private readonly Func<string, string, Task>? _storeSecret;
    private readonly Func<string, Task<string?>> _pickFile;
    private readonly List<SharedCredential> _logins;
    private readonly Dictionary<string, string> _passwords = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forget = new(StringComparer.Ordinal);
    private readonly ListBox _list = new() { MinWidth = 200 };
    private readonly StackPanel _detail = new() { Spacing = 10, Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _problem = new() { Foreground = new SolidColorBrush(Color.Parse("#E5484D")), TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public SharedLoginsDialog(AppSettings settings, Func<string, bool>? hasSecret, Func<string, string, Task>? storeSecret, Func<string, Task<string?>> pickFile)
    {
        _settings = settings;
        _hasSecret = hasSecret;
        _storeSecret = storeSecret;
        _pickFile = pickFile;
        _logins = [.. settings.SharedCredentials.Select(l => l.Clone())];
        Title = "Shared logins";
        Width = 720;
        Height = 460;
        MinWidth = 560;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var add = new Button { Content = "＋ New login", Classes = { "accent" } };
        add.Click += (_, _) => Add();
        var delete = new Button { Content = "Delete" };
        delete.Click += (_, _) => Delete();
        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        listButtons.Children.AddRange([add, delete]);
        var left = new DockPanel();
        DockPanel.SetDock(listButtons, Dock.Bottom);
        left.Children.AddRange([listButtons, _list]);
        _list.SelectionChanged += (_, _) => ShowDetail();

        var intro = new TextBlock
        {
            Text = "A login is a user and a password (or an SSH key) kept once: the connections that choose it under Login take its user and key, and its password when it is saved or typed for any of them.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 12),
        };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88, Classes = { "accent" } };
        ok.Click += async (_, _) => await AcceptAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.AddRange([cancel, ok]);
        var bottom = new StackPanel();
        bottom.Children.AddRange([_problem, buttons]);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        body.Children.AddRange([left, new ScrollViewer { Content = _detail }]);
        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.AddRange([intro, bottom, body]);
        Content = root;
        RefreshList();
        _list.SelectedIndex = _logins.Count > 0 ? 0 : -1;
        ShowDetail();
    }

    /// <summary>OK wrote the logins back.</summary>
    public bool Saved { get; private set; }

    public IReadOnlyList<SharedCredential> Logins => _logins;

    private SharedCredential? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _logins.Count ? _logins[_list.SelectedIndex] : null;

    private void RefreshList()
    {
        var index = _list.SelectedIndex;
        _list.ItemsSource = _logins.Select(l => new TextBlock { Text = l.Name.Length > 0 ? l.Name : Localizer.T("(no name)"), Classes = { "noloc" } }).ToList();
        _list.SelectedIndex = Math.Min(index, _logins.Count - 1);
    }

    /// <summary>A new login, chosen (tests use it too).</summary>
    public SharedCredential Add()
    {
        var login = new SharedCredential { Name = Localizer.T("New login") };
        _logins.Add(login);
        RefreshList();
        _list.SelectedIndex = _logins.Count - 1;
        return login;
    }

    private void Delete()
    {
        if (Selected is not { } login)
        {
            return;
        }
        _logins.Remove(login);
        _passwords.Remove(login.Id);
        _forget.Add(login.Id);
        RefreshList();
        ShowDetail();
    }

    /// <summary>The password typed for a login (stored on OK; tests).</summary>
    public void SetPassword(SharedCredential login, string password)
    {
        _passwords[login.Id] = password;
        _forget.Remove(login.Id);
    }

    private void ShowDetail()
    {
        _detail.Children.Clear();
        if (Selected is not { } login)
        {
            _detail.Children.Add(new TextBlock { Text = "No logins yet: New login makes one.", Opacity = 0.7 });
            return;
        }
        Control Row(string caption, Control input, string? note = null)
        {
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = caption, FontWeight = FontWeight.SemiBold, FontSize = 12.5 });
            stack.Children.Add(input);
            if (note is not null)
            {
                stack.Children.Add(new TextBlock { Text = note, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            }
            return stack;
        }
        var name = new TextBox { Text = login.Name, Classes = { "noloc" } };
        name.TextChanged += (_, _) =>
        {
            login.Name = name.Text ?? string.Empty;
            if (_list.ItemsSource is List<TextBlock> items && _list.SelectedIndex >= 0)
            {
                items[_list.SelectedIndex].Text = login.Name;
            }
        };
        var user = new TextBox { Text = login.User, Classes = { "noloc" }, PlaceholderText = "user or DOMAIN\\user" };
        user.TextChanged += (_, _) => login.User = (user.Text ?? string.Empty).Trim();
        var saved = !_forget.Contains(login.Id) && (_passwords.ContainsKey(login.Id) || _hasSecret?.Invoke(login.SecretId) == true);
        var password = new TextBox
        {
            PasswordChar = '•', Classes = { "noloc" },
            PlaceholderText = Localizer.T(saved ? "saved (type to change)" : "asked at the first login when empty"),
        };
        password.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(password.Text))
            {
                SetPassword(login, password.Text);
            }
        };
        var forget = new Button { Content = "Forget the saved password", IsVisible = saved };
        forget.Click += (_, _) =>
        {
            _passwords.Remove(login.Id);
            _forget.Add(login.Id);
            ShowDetail();
        };
        var key = new TextBox { Text = login.KeyFile, Classes = { "noloc" }, PlaceholderText = Localizer.T("empty: the password logs in") };
        key.TextChanged += (_, _) => login.KeyFile = string.IsNullOrWhiteSpace(key.Text) ? null : key.Text.Trim();
        var browse = new Button { Content = "...", Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            if (await _pickFile("Private key file") is { } file)
            {
                key.Text = file;
            }
        };
        var keyRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        keyRow.Children.AddRange([browse, key]);
        var passwordRow = new StackPanel { Spacing = 6 };
        passwordRow.Children.AddRange([password, forget]);
        var users = SharedCredential.UsedBy(login, _settings).Select(c => c.Name).ToList();
        _detail.Children.AddRange([
            Row("Name", name),
            Row("User", user),
            Row("Password", passwordRow, Localizer.T("With a key file: its passphrase.")),
            Row("Private key (SSH)", keyRow, Localizer.T("Used by SFTP, SCP and SSH connections; the others log in with the password.")),
            new TextBlock
            {
                Text = users.Count == 0 ? Localizer.T("No connection uses this login yet.") : Localizer.T("Used by: {0}").Replace("{0}", string.Join(", ", users), StringComparison.Ordinal),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Classes = { "noloc" },
            },
        ]);
    }

    /// <summary>What keeps OK from closing (empty: nothing).</summary>
    public string? Problem()
    {
        foreach (var login in _logins)
        {
            if (string.IsNullOrWhiteSpace(login.Name))
            {
                return "Every login needs a name.";
            }
            if (string.IsNullOrWhiteSpace(login.User))
            {
                return Localizer.T("The login \"{0}\" needs a user.").Replace("{0}", login.Name, StringComparison.Ordinal);
            }
            if (_logins.Count(l => string.Equals(l.Name.Trim(), login.Name.Trim(), StringComparison.OrdinalIgnoreCase)) > 1)
            {
                return Localizer.T("Two logins are named \"{0}\".").Replace("{0}", login.Name, StringComparison.Ordinal);
            }
        }
        return null;
    }

    /// <summary>OK: the logins into the settings, the passwords into the store, the connections updated.</summary>
    public async Task AcceptAsync()
    {
        if (Problem() is { } problem)
        {
            _problem.Text = Localizer.T(problem);
            _problem.IsVisible = true;
            return;
        }
        foreach (var login in _logins)
        {
            login.Name = login.Name.Trim();
        }
        _settings.SharedCredentials = [.. _logins];
        if (_storeSecret is { } store)
        {
            foreach (var id in _forget)
            {
                await store(SharedCredential.SecretIdOf(id), string.Empty);
            }
            foreach (var (id, password) in _passwords)
            {
                await store(SharedCredential.SecretIdOf(id), password);
            }
        }
        SharedCredential.Apply(_settings);
        Saved = true;
        Close();
    }
}
