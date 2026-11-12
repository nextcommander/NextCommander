using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// Change button bar > icon file: the icons of an exe, dll or icl file to choose from (Windows); the result is the
/// icon's index ("file,index" in the button).
/// </summary>
public sealed class IconPickerDialog : DialogWindow
{
    private readonly ListBox _list = new() { Height = 300 };
    private readonly List<int> _indexes = [];

    public IconPickerDialog(string file)
        : base("Choose icon", 520)
    {
        Body.Children.Add(new TextBlock { Text = file, Opacity = 0.75, Classes = { "noloc" }, TextTrimming = Avalonia.Media.TextTrimming.PathSegmentEllipsis });
        _list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });
        var items = new List<Control>();
        if (OperatingSystem.IsWindows())
        {
            var count = Math.Min(Services.Icons.WindowsShellIconService.IconCount(file), 1000);
            for (var i = 0; i < count; i++)
            {
                if (Services.Icons.WindowsShellIconService.LoadIndex(file, i) is Bitmap bitmap)
                {
                    _indexes.Add(i);
                    var image = new Image { Source = bitmap, Width = 32, Height = 32, Margin = new Avalonia.Thickness(4) };
                    ToolTip.SetTip(image, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    items.Add(image);
                }
            }
        }
        _list.ItemsSource = items;
        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
        _list.DoubleTapped += (_, _) => Accept();
        Body.Children.Add(_list);
        AddButton("OK", Accept, isDefault: true);
        AddButton("Cancel", Close, isCancel: true);
    }

    /// <summary>The chosen icon's index in the file; null when cancelled.</summary>
    public int? Result { get; private set; }

    /// <summary>Indexes of the icons shown.</summary>
    public IReadOnlyList<int> Indexes => _indexes;

    public void Select(int position) => _list.SelectedIndex = position;

    public void Accept()
    {
        if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _indexes.Count)
        {
            Result = _indexes[_list.SelectedIndex];
            Close();
        }
    }
}
