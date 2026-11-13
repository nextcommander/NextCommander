using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NextCommander.App.Services;
using NextCommander.Core.Plugins;
using NextCommander.Vfs.Archive;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// Pack files (Alt+F5): the archive name, what goes in (path names, subfolders,
/// move, separate archives, leave out the base folder, encryption) on the left; the compression rate, the packer and
/// its configuration on the right.
/// </summary>
public sealed class PackDialog : Window
{
    /// <summary>The built-in packers with their names.</summary>
    private static readonly (string Text, PackFormat Format)[] s_builtIn =
    [
        ("ZIP", PackFormat.Zip), ("7-ZIP", PackFormat.SevenZip), ("TAR", PackFormat.Tar), ("TGZ", PackFormat.TarGz), ("TBZ", PackFormat.TarBz2),
        ("TZST", PackFormat.TarZst),
    ];

    /// <summary>Extensions the name may end in (swapped when the packer changes).</summary>
    private static readonly string[] s_extensions = ["zip", "7z", "tar", "tar.gz", "tgz", "tar.bz2", "tbz", "tar.zst", "tzst", "exe", "sfx"];

    /// <summary>Configuration > Packer: compression level chosen first (index into Store / Fast / Normal / Smallest).</summary>
    public static int DefaultLevelIndex { get; set; } = 2;

    /// <summary>Opens Configuration on the Packer page over this dialog ("Configure..."); set by the main window.</summary>
    public static Func<Window, Task>? OpenConfiguration { get; set; }

    private readonly List<(string Text, PackFormat Format, IPackerPlugin? Plugin, RadioButton Radio)> _packers = [];
    private readonly TextBox _target;
    private readonly NumericUpDown _rate = new() { Minimum = 0, Maximum = 9, Increment = 1, FormatString = "0", Width = 110 };
    private readonly CheckBox _paths = new() { Content = "Also pack path names (only recursed)", IsChecked = true };
    private readonly CheckBox _recursive = new() { Content = "Recursively pack subdirectories", IsChecked = true };
    private readonly CheckBox _move = new() { Content = "Move to archive" };
    private readonly CheckBox _separate = new() { Content = "Create separate archives, one per selected file/dir" };
    private readonly CheckBox _leaveOutBase = new() { Content = "Leave out base directory when packing folders", Margin = new Thickness(26, 0, 0, 0), IsEnabled = false };
    private readonly CheckBox _split = new() { Content = "Split into volumes of", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _volume = new() { Text = "100M", Width = 110, IsEnabled = false, Classes = { "noloc" } };
    private readonly CheckBox _sfx = new() { Content = "Create a self-extracting archive" };
    private readonly CheckBox _encrypt = new() { Content = "Encrypt" };
    private readonly TextBox _password = new() { PasswordChar = '●', PlaceholderText = "Password" };
    private readonly TextBox _repeat = new() { PasswordChar = '●', PlaceholderText = "Repeat password" };
    private readonly TextBlock _encryptNote = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _passwords = new() { Spacing = 6, Margin = new Thickness(26, 0, 0, 0), IsVisible = false };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

    public PackDialog(string description, string target, PackFormat format)
    {
        Title = "Pack files";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _target = new TextBox { Text = target, Classes = { "noloc" } };
        _rate.Value = new[] { 0, 1, 6, 9 }[Math.Clamp(DefaultLevelIndex, 0, 3)];

        // ---- left: name and options
        _separate.IsCheckedChanged += (_, _) => _leaveOutBase.IsEnabled = _separate.IsChecked == true;
        _encrypt.IsCheckedChanged += (_, _) =>
        {
            _passwords.IsVisible = _encrypt.IsChecked == true;
            if (_passwords.IsVisible)
            {
                _password.Focus();
            }
        };
        _passwords.Children.AddRange([_password, _repeat, _encryptNote]);

        // 7-Zip: volumes (name.7z.001 ...) or a self-extracting archive; 7-Zip cannot split a self-extracting one
        ToolTip.SetTip(_volume, "Size of each volume: 100M, 650M (CD), 4092M (FAT32), 1.5G ... A plain number is megabytes.");
        _split.IsCheckedChanged += (_, _) =>
        {
            _volume.IsEnabled = _split.IsChecked == true;
            UpdateSevenZipOptions();
            if (_volume.IsEnabled)
            {
                _volume.Focus();
                _volume.SelectAll();
            }
        };
        _sfx.IsCheckedChanged += (_, _) =>
        {
            UpdateSevenZipOptions();
            OnPackerChanged(); // the name ends in .exe
        };
        var splitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        splitRow.Children.AddRange([_split, _volume]);

        var left = new StackPanel { Spacing = 8 };
        left.Children.AddRange([
            new TextBlock { Text = description, Classes = { "noloc" } }, _target,
            _paths, _recursive, _move, _separate, _leaveOutBase, splitRow, _sfx, _encrypt, _passwords, _error,
        ]);

        // ---- right: rate, packer, configure
        var rateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        rateRow.Children.AddRange([new TextBlock { Text = "Compression rate:", VerticalAlignment = VerticalAlignment.Center }, _rate]);
        ToolTip.SetTip(_rate, "0 = store only, 1 = fastest ... 9 = smallest");

        var grid = new UniformGrid { Columns = 3 };
        foreach (var (text, packFormat) in s_builtIn)
        {
            var available = packFormat != PackFormat.SevenZip || SevenZipCli.IsAvailable;
            AddPacker(grid, available ? text : text + "*", packFormat, null, available);
        }
        foreach (var plugin in PluginCatalog.Packers)
        {
            AddPacker(grid, plugin.FormatName.ToUpperInvariant(), PackFormat.Zip, plugin, true);
        }
        var packerPanel = new StackPanel { Spacing = 6 };
        packerPanel.Children.Add(grid);
        if (!SevenZipCli.IsAvailable)
        {
            packerPanel.Children.Add(new TextBlock { Text = "* needs the 7-Zip program", FontSize = 12, Opacity = 0.7 });
        }
        var configure = new Button { Content = "Configure...", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        configure.IsEnabled = OpenConfiguration is not null;
        configure.Click += async (_, _) =>
        {
            if (OpenConfiguration is { } open)
            {
                await open(this);
            }
        };
        packerPanel.Children.Add(configure);
        var packerFrame = new Border
        {
            Child = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "Packer", FontWeight = FontWeight.SemiBold }, packerPanel } },
            Padding = new Thickness(10, 8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            [!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmFunctionKeyBorderBrush"),
        };
        var right = new StackPanel { Spacing = 10, Width = 250 };
        right.Children.AddRange([rateRow, packerFrame]);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, Margin = new Thickness(16, 14, 16, 8) };
        Grid.SetColumn(right, 1);
        columns.Children.AddRange([left, right]);

        // ---- buttons
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "dialog" } };
        ok.Click += (_, _) => Accept(queue: false);
        var folder = new Button { Content = "Folder...", Classes = { "dialog" } };
        ToolTip.SetTip(folder, "Choose the folder of the archive");
        folder.Click += async (_, _) => await ChooseFolderAsync();
        var queue = new Button { Content = "Add to queue", Classes = { "dialog" } };
        queue.Click += (_, _) => Accept(queue: true);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Classes = { "buttonrow" }, Margin = new Thickness(16, 4, 16, 14) };
        buttons.Children.AddRange([ok, folder, queue, cancel]);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.AddRange([buttons, columns]);
        Content = root;

        var start = _packers.FindIndex(p => p.Format == format && p.Plugin is null && p.Radio.IsEnabled);
        _packers[Math.Max(0, start)].Radio.IsChecked = true;
        OnPackerChanged();
        Opened += (_, _) =>
        {
            _target.Focus();
            var text = _target.Text ?? string.Empty;
            var name = Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/')) + 1;
            _target.SelectionStart = name;
            _target.SelectionEnd = text.Length;
        };
    }

    private void AddPacker(UniformGrid grid, string text, PackFormat format, IPackerPlugin? plugin, bool available)
    {
        var radio = new RadioButton { Content = text, GroupName = "packer", IsEnabled = available, Classes = { "noloc" }, MinWidth = 70 };
        if (!available)
        {
            ToolTip.SetTip(radio, "Install 7-Zip (or set its path in Configuration > Packer > 7-Zip) to write 7z archives.");
        }
        var index = _packers.Count;
        radio.IsCheckedChanged += (_, _) =>
        {
            if (radio.IsChecked == true)
            {
                _selected = index;
                OnPackerChanged();
            }
        };
        _packers.Add((text, format, plugin, radio));
        grid.Children.Add(radio);
    }

    public PackDialogResult? Result { get; private set; }

    /// <summary>The packer chosen (tests choose one by its name: "ZIP", "TGZ" ...).</summary>
    public string SelectedPacker
    {
        get => Selected.Text;
        set => _packers.First(p => string.Equals(p.Text.TrimEnd('*'), value, StringComparison.OrdinalIgnoreCase)).Radio.IsChecked = true;
    }

    /// <summary>The archive name as typed (tests).</summary>
    public string TargetText
    {
        get => _target.Text ?? string.Empty;
        set => _target.Text = value;
    }

    /// <summary>The check boxes by their text and the rate box (tests).</summary>
    public CheckBox Option(string text) =>
        new[] { _paths, _recursive, _move, _separate, _leaveOutBase, _split, _sfx, _encrypt }.First(c => string.Equals(c.Content as string, text, StringComparison.Ordinal));

    /// <summary>The volume size box (tests).</summary>
    public TextBox VolumeBox => _volume;

    public NumericUpDown Rate => _rate;

    public TextBox PasswordBox => _password;

    public TextBox RepeatBox => _repeat;

    public string ErrorText => _error.Text ?? string.Empty;

    /// <summary>The packer last checked (kept here: while one radio button is checked the old one is still checked for a moment).</summary>
    private int _selected;

    private (string Text, PackFormat Format, IPackerPlugin? Plugin, RadioButton Radio) Selected => _packers[_selected];

    private string SelectedExtension => SelfExtracting ? SevenZipCli.SfxExtension
        : Selected.Plugin is { } p ? "." + p.Extension : PackJob.Extension(Selected.Format);

    /// <summary>The built-in 7-Zip packer is chosen: volumes and self-extracting archives are its own.</summary>
    private bool SevenZipChosen => Selected.Format == PackFormat.SevenZip && Selected.Plugin is null;

    private bool SelfExtracting => SevenZipChosen && _sfx.IsChecked == true;

    /// <summary>Volumes and self-extracting archives: only with 7-Zip (the latter when it has an SFX module), not both at once.</summary>
    private void UpdateSevenZipOptions()
    {
        var sfxModule = SevenZipCli.SfxModule is not null;
        _split.IsEnabled = SevenZipChosen && _sfx.IsChecked != true;
        _sfx.IsEnabled = SevenZipChosen && sfxModule && _split.IsChecked != true;
        _volume.IsEnabled = _split.IsEnabled && _split.IsChecked == true;
        ToolTip.SetTip(_sfx, !sfxModule ? "This 7-Zip has no module for self-extracting archives (7z.sfx)."
            : _split.IsChecked == true ? "7-Zip cannot split a self-extracting archive." : null);
        ToolTip.SetTip(_split, _sfx.IsChecked == true ? "7-Zip cannot split a self-extracting archive." : null);
    }

    private bool SelectedEncrypts => Selected.Plugin is { } p ? p.SupportsPassword : Selected.Format is PackFormat.Zip or PackFormat.SevenZip;

    /// <summary>Swaps the archive extension when the packer changes; encryption only where the packer has it.</summary>
    private void OnPackerChanged()
    {
        UpdateSevenZipOptions();
        _encrypt.IsEnabled = SelectedEncrypts;
        if (!SelectedEncrypts)
        {
            _encrypt.IsChecked = false;
        }
        _encryptNote.Text = Selected.Format == PackFormat.SevenZip && Selected.Plugin is null
            ? "AES-256; the file names are encrypted too (Configuration > Packer > 7-Zip)."
            : "The method is chosen in Configuration > Packer > Zip.";
        var text = _target.Text ?? string.Empty;
        var extensions = s_extensions.Concat(PluginCatalog.Packers.Select(p => p.Extension));
        foreach (var ext in extensions.OrderByDescending(e => e.Length))
        {
            if (text.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^(ext.Length + 1)];
                break;
            }
        }
        _target.Text = text + SelectedExtension;
    }

    /// <summary>"Folder...": another folder for the archive, the name kept.</summary>
    private async Task ChooseFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder of the archive" });
        if (folders.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault() is { } folder)
        {
            var text = _target.Text ?? string.Empty;
            var name = text[(Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/')) + 1)..];
            _target.Text = Path.Combine(folder, name.Length > 0 ? name : "archive" + SelectedExtension);
        }
    }

    /// <summary>Rate 0..9 → the level of the built-in packers (7-Zip gets the number itself).</summary>
    public static CompressionLevel LevelOf(int rate) => rate switch
    {
        <= 0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        <= 6 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize,
    };

    public void Accept(bool queue)
    {
        string? password = null;
        if (_encrypt.IsChecked == true && SelectedEncrypts)
        {
            password = _password.Text ?? string.Empty;
            if (password.Length == 0)
            {
                _error.Text = "Enter a password, or turn Encrypt off.";
                return;
            }
            if (password != _repeat.Text)
            {
                _error.Text = "Passwords do not match.";
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(_target.Text))
        {
            _error.Text = "Enter an archive name.";
            return;
        }
        long volumeSize = 0;
        if (SevenZipChosen && _split.IsChecked == true && !SelfExtracting)
        {
            volumeSize = SevenZipCli.ParseVolumeSize(_volume.Text);
            if (volumeSize == 0)
            {
                _error.Text = "Enter a volume size such as 100M, 700M or 4G.";
                _volume.Focus();
                return;
            }
        }
        var rate = (int)(_rate.Value ?? 6);
        Result = new PackDialogResult(_target.Text.Trim(), Selected.Format, LevelOf(rate), password, _move.IsChecked == true, _separate.IsChecked == true,
            queue, Selected.Plugin, KeepPaths: _paths.IsChecked == true, Recursive: _recursive.IsChecked == true,
            LeaveOutBaseFolder: _separate.IsChecked == true && _leaveOutBase.IsChecked == true, Rate: rate,
            VolumeSize: volumeSize, SelfExtracting: SelfExtracting);
        Close();
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}

/// <summary>Password entry (archives).</summary>
public sealed class PasswordDialog : DialogWindow
{
    private readonly TextBox _input = new() { PasswordChar = '●' };

    public PasswordDialog(string title, string message)
        : base(title, 420)
    {
        Body.Children.Add(Label(message));
        Body.Children.Add(_input);
        AddButton("OK", () =>
        {
            Result = _input.Text ?? string.Empty;
            Close();
        }, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
        Opened += (_, _) => _input.Focus();
    }

    public string? Result { get; private set; }
}
