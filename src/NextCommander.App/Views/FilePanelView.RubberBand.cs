using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;

namespace NextCommander.App.Views;

/// <summary>Rubber band selection: drag on empty space in the full view marks the rows it touches.</summary>
public partial class FilePanelView
{
    private Point? _bandOrigin;
    private Dictionary<FileItemViewModel, bool>? _bandSnapshot;

    private static bool IsEmptyListArea(object? source)
    {
        var visual = source as Visual;
        return visual is not null
            && visual.FindAncestorOfType<DataGridRow>(includeSelf: true) is null
            && visual.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is null
            && visual.FindAncestorOfType<ScrollBar>(includeSelf: true) is null;
    }

    private void BeginRubberBand(PointerPressedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        Grid.Focus();
        _bandOrigin = e.GetPosition(ContentHost);
        _bandSnapshot = _vm.Items.ToDictionary(i => i, i => i.IsMarked);
        e.Pointer.Capture(Grid);
        e.Handled = true;
    }

    /// <summary>Returns true while a rubber band is active.</summary>
    private bool UpdateRubberBand(PointerEventArgs e)
    {
        if (_bandOrigin is not { } origin || _vm is null || _bandSnapshot is null)
        {
            return false;
        }
        var current = e.GetPosition(ContentHost);
        var rect = new Rect(origin, current).Normalize();
        Canvas.SetLeft(Band, rect.X);
        Canvas.SetTop(Band, rect.Y);
        Band.Width = rect.Width;
        Band.Height = rect.Height;
        Band.IsVisible = rect.Width > 2 || rect.Height > 2;

        foreach (var row in Grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (!row.IsVisible || row.DataContext is not FileItemViewModel item || !_bandSnapshot.TryGetValue(item, out var before))
            {
                continue;
            }
            var top = row.TranslatePoint(default, ContentHost)?.Y ?? double.NaN;
            if (double.IsNaN(top))
            {
                continue;
            }
            var hit = top + row.Bounds.Height > rect.Top && top < rect.Bottom;
            _vm.SetMark(item, hit || before);
        }
        _vm.NotifyMarksChanged();
        e.Handled = true;
        return true;
    }

    private void EndRubberBand(PointerReleasedEventArgs e)
    {
        if (_bandOrigin is null)
        {
            return;
        }
        _bandOrigin = null;
        _bandSnapshot = null;
        Band.IsVisible = false;
        e.Pointer.Capture(null);
    }
}
