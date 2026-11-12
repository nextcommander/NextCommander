using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using NextCommander.Core.Operations;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Change attributes: tri-state flags (unchanged when mixed), modification time, recursion.</summary>
public sealed class AttributesDialog : DialogWindow
{
    private static readonly (string Text, EntryAttributes Flag)[] s_flags =
    [
        ("Read-only", EntryAttributes.ReadOnly), ("Hidden", EntryAttributes.Hidden), ("System", EntryAttributes.System), ("Archive", EntryAttributes.Archive),
    ];

    private readonly List<(CheckBox Box, EntryAttributes Flag, bool? Initial)> _boxes = [];
    private readonly CheckBox _changeTime = new() { Content = "Set modification time:" };
    private readonly TextBox _time = new();
    private readonly CheckBox _recursive = new() { Content = "Also apply to everything in subfolders" };
    private readonly TextBlock _error = new() { Foreground = Avalonia.Media.Brushes.IndianRed };

    public AttributesDialog(IReadOnlyList<VfsEntry> entries)
        : base("Change attributes", 460)
    {
        Body.Children.Add(Label(entries.Count == 1 ? entries[0].Name : $"{entries.Count} items", bold: true));
        foreach (var (text, flag) in s_flags)
        {
            var set = entries.Count(e => e.Attributes.HasFlag(flag));
            bool? initial = set == 0 ? false : set == entries.Count ? true : null;
            var box = new CheckBox { Content = text, IsThreeState = initial is null, IsChecked = initial };
            _boxes.Add((box, flag, initial));
            Body.Children.Add(box);
        }
        _time.Text = (entries.Count > 0 ? entries[0].LastWriteTime : DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        _time.IsEnabled = false;
        _changeTime.IsCheckedChanged += (_, _) => _time.IsEnabled = _changeTime.IsChecked == true;
        Body.Children.Add(_changeTime);
        Body.Children.Add(_time);
        if (entries.Any(e => e.IsDirectory))
        {
            Body.Children.Add(_recursive);
        }
        Body.Children.Add(_error);
        AddButton("OK", Accept, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
    }

    public AttributeChange? Result { get; private set; }

    private void Accept()
    {
        DateTime? time = null;
        if (_changeTime.IsChecked == true)
        {
            if (!DateTime.TryParse(_time.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
                && !DateTime.TryParse(_time.Text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out t))
            {
                _error.Text = "Could not parse date (e.g. 2024-05-01 14:30:00).";
                return;
            }
            time = t;
        }
        var set = EntryAttributes.None;
        var clear = EntryAttributes.None;
        foreach (var (box, flag, initial) in _boxes)
        {
            if (box.IsChecked == initial)
            {
                continue; // untouched
            }
            if (box.IsChecked == true)
            {
                set |= flag;
            }
            else if (box.IsChecked == false)
            {
                clear |= flag;
            }
        }
        Result = new AttributeChange(set, clear, time, _recursive.IsChecked == true);
        Close();
    }
}

/// <summary>Create checksums: algorithm, one file for all or one per file.</summary>
public sealed class ChecksumDialog : DialogWindow
{
    private readonly ComboBox _kind = new() { MinWidth = 160 };
    private readonly RadioButton _single = new() { Content = "In one file:", IsChecked = true, GroupName = "mode" };
    private readonly RadioButton _perFile = new() { Content = "Separate file for each file", GroupName = "mode" };
    private readonly TextBox _name = new();
    private string _baseName;

    public ChecksumDialog(string baseName, ChecksumKind kind)
        : base("Create checksums", 460)
    {
        _baseName = baseName;
        _kind.ItemsSource = Checksums.Available.Select(Checksums.DisplayName).ToList();
        _kind.SelectedIndex = Math.Max(0, Checksums.Available.ToList().IndexOf(kind));
        _kind.SelectionChanged += (_, _) => UpdateName();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "Algorithm:", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_kind);
        Body.Children.Add(row);
        Body.Children.Add(_single);
        Body.Children.Add(_name);
        Body.Children.Add(_perFile);
        _single.IsCheckedChanged += (_, _) => _name.IsEnabled = _single.IsChecked == true;
        UpdateName();
        AddButton("Create", () =>
        {
            Result = (SelectedKind, _single.IsChecked == true ? (_name.Text ?? string.Empty).Trim() : null);
            Close();
        }, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
    }

    public (ChecksumKind Kind, string? SingleFileName)? Result { get; private set; }

    private ChecksumKind SelectedKind => Checksums.Available[Math.Max(0, _kind.SelectedIndex)];

    private void UpdateName()
    {
        var text = _name.Text;
        if (!string.IsNullOrEmpty(text) && Checksums.FromFileName(text) is not null)
        {
            _baseName = Path.GetFileNameWithoutExtension(text);
        }
        _name.Text = _baseName + Checksums.Extension(SelectedKind);
    }
}
