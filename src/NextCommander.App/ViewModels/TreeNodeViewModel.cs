using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>Lazily loaded directory tree node (tree pane, Alt+F10 tree).</summary>
public sealed partial class TreeNodeViewModel : ObservableObject
{
    private readonly Func<VfsPath, Task<IReadOnlyList<VfsEntry>>> _listDirectories;
    private bool _loaded;
    private Task? _loading;

    [ObservableProperty]
    private bool _isExpanded;

    private TreeNodeViewModel(string name, VfsPath path, Func<VfsPath, Task<IReadOnlyList<VfsEntry>>> listDirectories, bool placeholder = false)
    {
        Name = name;
        Path = path;
        _listDirectories = listDirectories;
        IsPlaceholder = placeholder;
        if (!placeholder)
        {
            Children.Add(new TreeNodeViewModel("…", path, listDirectories, placeholder: true));
        }
    }

    public string Name { get; }

    public VfsPath Path { get; }

    public bool IsPlaceholder { get; }

    /// <summary>The icon the system gives a user folder (documents, downloads ...); null for other folders.</summary>
    public Avalonia.Media.Imaging.Bitmap? SystemIcon => !IsPlaceholder && Path.IsLocal ? Services.Icons.SystemFolderIcons.ForPath(Path.Path) : null;

    public bool HasSystemIcon => SystemIcon is not null;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public static TreeNodeViewModel CreateRoot(VfsPath path, Func<VfsPath, Task<IReadOnlyList<VfsEntry>>> listDirectories)
    {
        var root = path;
        while (root.Parent is not null)
        {
            root = root.Parent;
        }
        return new TreeNodeViewModel(root.ToDisplayString(), root, listDirectories);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = LoadAsync();
        }
    }

    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    /// <summary>Forgets the children so they are re-read on the next expansion (after create/rename/delete).</summary>
    public void Invalidate()
    {
        _loaded = false;
        _loading = null;
        Children.Clear();
        Children.Add(new TreeNodeViewModel("…", Path, _listDirectories, placeholder: true));
        if (IsExpanded)
        {
            _ = LoadAsync();
        }
    }

    private async Task LoadCoreAsync()
    {
        if (_loaded || IsPlaceholder)
        {
            return;
        }
        var dirs = await _listDirectories(Path);
        Children.Clear();
        foreach (var dir in dirs)
        {
            Children.Add(new TreeNodeViewModel(dir.Name, dir.Path, _listDirectories));
        }
        _loaded = true;
    }

    /// <summary>
    /// Reads the whole tree below this node without expanding it (Display > Always load complete directory tree):
    /// branches without subfolders lose their expander at once. Stops after <paramref name="maxNodes"/> folders.
    /// </summary>
    /// <returns>Number of folders read.</returns>
    public async Task<int> LoadAllAsync(int maxNodes = 50_000, CancellationToken cancellationToken = default)
    {
        var count = 0;
        var pending = new Queue<TreeNodeViewModel>();
        pending.Enqueue(this);
        while (pending.Count > 0 && count < maxNodes && !cancellationToken.IsCancellationRequested)
        {
            var node = pending.Dequeue();
            try
            {
                await node.LoadAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // unreadable branch stays as it is
            }
            foreach (var child in node.Children.Where(c => !c.IsPlaceholder))
            {
                count++;
                pending.Enqueue(child);
            }
        }
        return count;
    }

    /// <summary>Expands the nodes down to <paramref name="target"/> and returns its node (or the deepest existing one).</summary>
    public async Task<TreeNodeViewModel> RevealAsync(VfsPath target)
    {
        var node = this;
        while (node.Path != target)
        {
            if (!target.IsSameOrUnder(node.Path))
            {
                return node;
            }
            await node.LoadAsync();
            var next = node.Children.FirstOrDefault(c => !c.IsPlaceholder && target.IsSameOrUnder(c.Path));
            if (next is null && target.GetRelativeTo(node.Path) is { Length: > 0 } relative)
            {
                // Hidden (or unlistable) folder on the way to the target: show it anyway.
                var childPath = node.Path.Combine(relative.Split('/')[0]);
                next = new TreeNodeViewModel(childPath.Name, childPath, _listDirectories);
                node.Children.Add(next);
            }
            if (next is null)
            {
                return node;
            }
            node.IsExpanded = true;
            node = next;
        }
        return node;
    }
}
