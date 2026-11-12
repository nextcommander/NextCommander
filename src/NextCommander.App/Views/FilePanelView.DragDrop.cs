using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.Core.Vfs;
using Serilog;

namespace NextCommander.App.Views;

/// <summary>Drag &amp; drop between the panels, onto subfolders and breadcrumbs, and to/from the OS.</summary>
public partial class FilePanelView
{
    /// <summary>Marks a drag that started in this process; the entries themselves travel in <see cref="s_activeDrag"/>
    /// (they may be archive/remote entries without an OS path).</summary>
    private static readonly DataFormat<string> s_entriesFormat = DataFormat.CreateStringApplicationFormat("nextcommander-entries");

    private static DragPayload? s_activeDrag;

    private (FileItemViewModel Item, Point Origin)? _dragCandidate;

    private sealed record DragPayload(IReadOnlyList<VfsEntry> Entries, PanelViewModel Source);

    private void InitializeDragDrop()
    {
        foreach (var target in new Control[] { Grid, Brief, Thumbs, PathBar })
        {
            target.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            target.AddHandler(DragDrop.DropEvent, OnDrop);
            target.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearCrumbHighlight());
        }
    }

    private void BeginDragCandidate(FileItemViewModel item, Point origin)
    {
        _dragCandidate = PanelViewModel.CanMark(item) ? (item, origin) : null;
    }

    private void CancelDragCandidate() => _dragCandidate = null;

    private async void TryStartDrag(PointerEventArgs e)
    {
        if (_dragCandidate is not { } candidate || _vm is null)
        {
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }
        var delta = e.GetPosition(this) - candidate.Origin;
        if (Math.Abs(delta.X) < 8 && Math.Abs(delta.Y) < 8)
        {
            return;
        }
        _dragCandidate = null;

        var vm = _vm;
        IReadOnlyList<VfsEntry> entries = candidate.Item.IsMarked ? vm.GetSelectedEntries() : [candidate.Item.Entry!];
        var payload = new DragPayload(entries, vm);
        var data = await FileClipboard.CreateTransferAsync(TopLevel.GetTopLevel(this)?.StorageProvider, entries);
        var marker = new DataTransferItem();
        marker.Set(s_entriesFormat, vm.Name);
        data.Add(marker);

        s_activeDrag = payload;
        try
        {
            var result = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (result == DragDropEffects.Move)
            {
                // Another application moved the files: show the new state.
                await vm.RefreshAsync();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Warning(ex, "Drag and drop failed");
        }
        finally
        {
            s_activeDrag = null;
        }
    }

    private static bool HasPayload(DragEventArgs e) =>
        s_activeDrag is not null || e.DataTransfer.Contains(DataFormat.File);

    /// <summary>Entries of a drag started by this application (null for drags from other applications).</summary>
    private static DragPayload? InternalPayload(DragEventArgs e) => s_activeDrag;

    /// <summary>The entries of a drag that started in a panel of this window (null otherwise): the F8 button takes them.</summary>
    public static (IReadOnlyList<VfsEntry> Entries, PanelViewModel Source)? DraggedEntries =>
        s_activeDrag is { } drag ? (drag.Entries, drag.Source) : null;

    /// <summary>Folder the drop goes to: folder row/crumb under the pointer, else the panel's directory.</summary>
    private VfsPath? ResolveDropTarget(DragEventArgs e, out Button? crumb)
    {
        crumb = null;
        if (_vm?.CurrentPath is not { } current)
        {
            return null;
        }
        var visual = e.Source as Visual;
        if (visual?.FindAncestorOfType<Button>(includeSelf: true) is { Tag: BreadcrumbSegment segment } button)
        {
            crumb = button;
            return segment.Path;
        }
        if (TreePane.NodeAt(e.Source) is { } treeNode)
        {
            return treeNode;
        }
        FileItemViewModel? item = null;
        if (visual?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: FileItemViewModel rowItem })
        {
            item = rowItem;
        }
        else if (visual is not null && (visual == Brief || visual.FindAncestorOfType<Controls.BriefListControl>() is not null))
        {
            item = Brief.HitTest(e.GetPosition(Brief));
        }
        else if (visual is not null && (visual == Thumbs || visual.FindAncestorOfType<Controls.ThumbnailListControl>() is not null))
        {
            item = Thumbs.HitTest(e.GetPosition(Thumbs));
        }
        if (item is { IsParent: true })
        {
            return current.Parent ?? current;
        }
        if (item?.Entry is { IsDirectory: true } dir)
        {
            return dir.Path;
        }
        return current;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        ClearCrumbHighlight();
        if (!HasPayload(e))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        var target = ResolveDropTarget(e, out var crumb);
        var payload = InternalPayload(e);
        // Dropping onto the folder the items already are in does nothing.
        if (target is null || (payload is not null && payload.Entries.All(x => x.Path.Parent == target || x.Path == target)))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        crumb?.Classes.Add("droptarget");
        var wanted = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DragDropEffects.Move : DragDropEffects.Copy;
        e.DragEffects = (e.DragEffects & wanted) != 0 ? wanted : e.DragEffects & (DragDropEffects.Copy | DragDropEffects.Move);
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ClearCrumbHighlight();
        var vm = _vm;
        if (vm is null)
        {
            return;
        }
        var target = ResolveDropTarget(e, out _);
        if (target is null)
        {
            return;
        }
        var move = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.DragEffects == DragDropEffects.Move;
        var payload = InternalPayload(e);
        IReadOnlyList<VfsEntry> sources;
        PanelViewModel? sourcePanel = null;
        if (payload is not null)
        {
            sources = payload.Entries;
            sourcePanel = payload.Source;
        }
        else if (e.DataTransfer.TryGetFiles() is { } files)
        {
            var list = new List<VfsEntry>();
            foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
            {
                if (await vm.GetEntryAsync(VfsPath.Local(path)) is { } entry)
                {
                    list.Add(entry);
                }
            }
            sources = list;
        }
        else
        {
            return;
        }
        // We copy/move ourselves; never tell the source application to delete anything.
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        if (sources.Count > 0)
        {
            Activated?.Invoke(this, EventArgs.Empty);
            DropRequested?.Invoke(this, new DropRequest(sources, target, move, sourcePanel));
        }
    }

    private void ClearCrumbHighlight()
    {
        foreach (var button in PathBar.GetVisualDescendants().OfType<Button>())
        {
            button.Classes.Remove("droptarget");
        }
    }
}
