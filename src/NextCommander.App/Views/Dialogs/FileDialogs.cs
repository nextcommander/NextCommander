using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using NextCommander.Core.Formatting;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Occupied space of a selection (Ctrl+L, folder sizes in "Properties").</summary>
public sealed record SpaceInfo(long Bytes, int Files, int Folders);

/// <summary>File properties where the operating system has no dialog (archives, servers, Linux, macOS).</summary>
public sealed class PropertiesDialog : DialogWindow
{
    private readonly TextBlock _size = new();

    public PropertiesDialog(VfsEntry entry, Func<Task<SpaceInfo>>? measure)
        : base("Properties — " + entry.Name, 480)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), RowSpacing = 6 };
        var row = 0;
        void Add(string label, Control value)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var caption = new TextBlock { Text = label, Opacity = 0.7 };
            Grid.SetRow(caption, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(caption);
            grid.Children.Add(value);
            row++;
        }
        TextBlock Text(string? text) => new SelectableTextBlock { Text = text ?? "—", TextWrapping = TextWrapping.Wrap };
        string Date(DateTime? d) => d is { Year: > 1980 } v ? v.ToString("dddd, d MMMM yyyy HH:mm:ss", CultureInfo.CurrentCulture) : "—";

        Add("Name", Text(entry.Name));
        Add("Location", Text(entry.Path.Parent?.ToDisplayString()));
        Add("Type", Text(entry.IsDirectory ? "Folder" : entry.Extension.Length == 0 ? "File" : entry.Extension.ToUpperInvariant() + " file"));
        _size.Text = entry.IsDirectory ? "calculating..." : $"{SizeFormatter.Human(entry.Size)} ({entry.Size:N0} bytes)";
        Add("Size", _size);
        if (entry.IsLink)
        {
            Add("Link target", Text(entry.LinkTarget));
        }
        Add("Modified", Text(Date(entry.LastWriteTime)));
        Add("Created", Text(Date(entry.CreationTime)));
        Add("Last accessed", Text(Date(entry.LastAccessTime)));
        var flags = new[] { (EntryAttributes.ReadOnly, "read-only"), (EntryAttributes.Hidden, "hidden"), (EntryAttributes.System, "system"),
            (EntryAttributes.Archive, "archive"), (EntryAttributes.Compressed, "compressed"), (EntryAttributes.Encrypted, "encrypted") }
            .Where(f => entry.Attributes.HasFlag(f.Item1)).Select(f => f.Item2).ToList();
        Add("Attributes", Text(flags.Count == 0 ? "none" : string.Join(", ", flags)));
        if (entry.UnixMode is { } mode)
        {
            Add("Permissions", Text(UnixPermissions(mode)));
        }
        Body.Children.Add(grid);
        AddButton("Close", Close, isDefault: true, isCancel: true);
        if (entry.IsDirectory && measure is not null)
        {
            Opened += async (_, _) =>
            {
                var info = await measure();
                _size.Text = $"{SizeFormatter.Human(info.Bytes)} ({info.Bytes:N0} bytes) — {info.Files:N0} files, {info.Folders:N0} folders";
            };
        }
    }

    public string SizeText => _size.Text ?? string.Empty;

    /// <summary>"rwxr-xr-x (755)".</summary>
    public static string UnixPermissions(UnixFileMode mode)
    {
        var bits = (int)mode;
        var chars = new char[9];
        var letters = "rwxrwxrwx";
        for (var i = 0; i < 9; i++)
        {
            chars[i] = (bits & (1 << (8 - i))) != 0 ? letters[i] : '-';
        }
        return $"{new string(chars)} ({Convert.ToString(bits & 0x1FF, 8)})";
    }
}

/// <summary>Ctrl+Z: comment of a file (descript.ion), several lines allowed.</summary>
public sealed class CommentDialog : DialogWindow
{
    private readonly TextBox _text;

    public CommentDialog(string name, string? current)
        : base("Comment — " + name, 520)
    {
        Body.Children.Add(Label("Comment (descript.ion; deleted if left empty):"));
        _text = new TextBox { Text = current, AcceptsReturn = true, Height = 110, TextWrapping = TextWrapping.Wrap };
        Body.Children.Add(_text);
        AddButton("Save", () =>
        {
            Result = _text.Text ?? string.Empty;
            Close();
        }, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
        Opened += (_, _) =>
        {
            _text.Focus();
            _text.SelectAll();
        };
    }

    public string? Result { get; private set; }
}
