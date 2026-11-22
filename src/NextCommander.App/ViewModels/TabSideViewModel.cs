using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>The tabs of one side (left or right). The visible panel is <see cref="ActiveTab"/>.</summary>
public sealed partial class TabSideViewModel : ObservableObject, IDisposable
{
    private const int MaxClosed = 20;
    private readonly Func<PanelSettings, PanelViewModel> _createPanel;
    private readonly AppSettings _settings;
    private readonly List<PanelSettings> _closed = [];

    /// <summary>Tabs in the order they were last visible (most recent first), for Ctrl+Tab "last used".</summary>
    private readonly List<PanelViewModel> _recent = [];

    [ObservableProperty]
    private PanelViewModel _activeTab;

    [ObservableProperty]
    private bool _isActiveSide;

    public TabSideViewModel(string name, AppSettings settings, IReadOnlyList<PanelSettings> tabs, int activeIndex, Func<PanelSettings, PanelViewModel> createPanel)
    {
        Name = name;
        _settings = settings;
        _createPanel = createPanel;
        foreach (var state in tabs)
        {
            Tabs.Add(Create(state));
        }
        _activeTab = Tabs[Math.Clamp(activeIndex, 0, Tabs.Count - 1)];
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, _activeTab)))
        {
            tab.Suspend();
            tab.IsCurrentTab = false;
        }
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowTabStrip));
            OnPropertyChanged(nameof(CanCloseTabs));
        };
    }

    public string Name { get; }

    public ObservableCollection<PanelViewModel> Tabs { get; } = [];

    public bool ShowTabStrip => _settings.ShowFolderTabs && (Tabs.Count > 1 || _settings.AlwaysShowTabs);

    /// <summary>A tab can be closed (the last one stays): the close button of the current tab shows then.</summary>
    public bool CanCloseTabs => Tabs.Count > 1;

    public AppSettings Settings => _settings;

    public bool TabsMultiline => _settings.TabsMultiline;

    public bool TabsSameWidth => _settings.TabsSameWidth;

    /// <summary>Settings changed (always show tabs, folder tab options).</summary>
    public void RefreshTabStrip()
    {
        OnPropertyChanged(nameof(ShowTabStrip));
        OnPropertyChanged(nameof(TabsMultiline));
        OnPropertyChanged(nameof(TabsSameWidth));
    }

    /// <summary>Where a new tab goes: after <paramref name="afterIndex"/> (default the current tab) or at the end.</summary>
    private int InsertIndex(int? afterIndex) =>
        _settings.NewTabNextToCurrent || afterIndex is not null ? Math.Min(Tabs.Count, (afterIndex ?? ActiveIndex) + 1) : Tabs.Count;

    /// <summary>Ctrl+Tab with "last used": the tab that was visible before the current one.</summary>
    public void ActivateRecent()
    {
        _recent.RemoveAll(t => !Tabs.Contains(t));
        if (_recent.FirstOrDefault(t => !ReferenceEquals(t, ActiveTab)) is { } previous)
        {
            ActiveTab = previous;
        }
        else
        {
            ActivateRelative(1);
        }
    }

    /// <summary>Tabs, most recently visible first (tabs never shown yet follow in strip order).</summary>
    public IReadOnlyList<PanelViewModel> RecentTabs()
    {
        _recent.RemoveAll(t => !Tabs.Contains(t));
        return [.. _recent, .. Tabs.Where(t => !_recent.Contains(t))];
    }

    public int ActiveIndex => Tabs.IndexOf(ActiveTab);

    public IReadOnlyList<PanelSettings> ClosedTabs => _closed;

    /// <summary>Raised when a tab is added (the owner subscribes to its events).</summary>
    public event EventHandler<PanelViewModel>? TabCreated;

    private PanelViewModel Create(PanelSettings state)
    {
        var panel = _createPanel(state);
        panel.LockedNavigation = (path, focus) => OpenTabAsync(path, focus, activate: true, afterIndex: Tabs.IndexOf(panel));
        panel.CloseWhenUnreachable = () =>
        {
            if (Tabs.Count <= 1 || !Tabs.Contains(panel))
            {
                return false; // the last tab stays (it goes to an existing parent)
            }
            Serilog.Log.Information("Tab of {Path} closed: it cannot be reached", panel.CurrentPath);
            CloseTab(panel);
            return true;
        };
        TabCreated?.Invoke(this, panel);
        return panel;
    }

    partial void OnActiveTabChanged(PanelViewModel? oldValue, PanelViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.IsCurrentTab = false;
            oldValue.Suspend();
        }
        newValue.IsCurrentTab = true;
        newValue.IsActive = IsActiveSide;
        _recent.Remove(newValue);
        _recent.Insert(0, newValue);
        _ = newValue.ResumeAsync();
        OnPropertyChanged(nameof(ActiveIndex));
    }

    partial void OnIsActiveSideChanged(bool value) => ActiveTab.IsActive = value;

    /// <summary>Ctrl+T: new tab (copy of the current one by default) next to the active tab.</summary>
    public async Task<PanelViewModel> OpenTabAsync(VfsPath? path = null, string? focusName = null, bool activate = true, int? afterIndex = null)
    {
        var state = ActiveTab.State.Clone();
        state.LockMode = TabLockMode.None;
        state.LockedPath = null;
        state.Title = null;
        state.Filter = null;
        var target = path ?? ActiveTab.CurrentPath;
        state.Path = target?.ToString();

        var panel = Create(state);
        panel.Drives = ActiveTab.Drives;
        Tabs.Insert(InsertIndex(afterIndex), panel);
        if (target is not null)
        {
            await panel.NavigateAsync(target, focusName);
        }
        if (activate)
        {
            ActiveTab = panel;
        }
        else
        {
            panel.Suspend();
            panel.IsCurrentTab = false;
        }
        return panel;
    }

    /// <summary>Adds a tab for a saved state (loading .tab files, restoring closed tabs; at <paramref name="at"/>: a tab dragged from the other side).</summary>
    public async Task<PanelViewModel> AddTabAsync(PanelSettings state, bool activate, int? at = null)
    {
        var panel = Create(state.Clone());
        panel.Drives = ActiveTab.Drives;
        Tabs.Insert(at is { } index ? Math.Clamp(index, 0, Tabs.Count) : InsertIndex(null), panel);
        var path = state.Path is not null && VfsPath.TryParse(state.Path, out var p) ? p : ActiveTab.CurrentPath;
        if (path is not null)
        {
            await panel.NavigateAsync(path!);
        }
        if (activate)
        {
            ActiveTab = panel;
        }
        else
        {
            panel.Suspend();
            panel.IsCurrentTab = false;
        }
        return panel;
    }

    /// <summary>Ctrl+W. The last tab is never closed.</summary>
    public void CloseTab(PanelViewModel? tab = null)
    {
        tab ??= ActiveTab;
        if (Tabs.Count <= 1)
        {
            return;
        }
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }
        _closed.Insert(0, tab.State.Clone());
        if (_closed.Count > MaxClosed)
        {
            _closed.RemoveAt(_closed.Count - 1);
        }
        if (ReferenceEquals(tab, ActiveTab))
        {
            ActiveTab = Tabs[index + 1 < Tabs.Count ? index + 1 : index - 1];
        }
        Tabs.RemoveAt(index);
        _recent.Remove(tab);
        tab.Dispose();
    }

    /// <summary>Takes a tab out of the side without keeping it among the closed ones (it went to the other side); the last tab stays.</summary>
    public void RemoveTab(PanelViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0 || Tabs.Count <= 1)
        {
            return;
        }
        if (ReferenceEquals(tab, ActiveTab))
        {
            ActiveTab = Tabs[index + 1 < Tabs.Count ? index + 1 : index - 1];
        }
        Tabs.RemoveAt(index);
        _recent.Remove(tab);
        tab.Dispose();
    }

    public void CloseOtherTabs()
    {
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, ActiveTab)).ToList())
        {
            CloseTab(tab);
        }
    }

    /// <summary>Closes tabs showing the same directory as an earlier tab (locked tabs are kept).</summary>
    public void CloseDuplicateTabs()
    {
        var seen = new HashSet<VfsPath>();
        foreach (var tab in Tabs.ToList())
        {
            if (tab.CurrentPath is not { } path)
            {
                continue;
            }
            if (!seen.Add(path) && tab.LockMode == TabLockMode.None && !tab.IsVirtual)
            {
                CloseTab(tab);
            }
        }
    }

    /// <summary>Restores the most recently closed tab (or the one at <paramref name="index"/> of <see cref="ClosedTabs"/>).</summary>
    public async Task ReopenClosedTabAsync(int index = 0)
    {
        if (index < 0 || index >= _closed.Count)
        {
            return;
        }
        var state = _closed[index];
        _closed.RemoveAt(index);
        await AddTabAsync(state, activate: true);
    }

    public void ActivateRelative(int delta)
    {
        if (Tabs.Count > 1)
        {
            ActiveTab = Tabs[((ActiveIndex + delta) % Tabs.Count + Tabs.Count) % Tabs.Count];
        }
    }

    public void ActivateIndex(int index)
    {
        if (index >= 0 && index < Tabs.Count)
        {
            ActiveTab = Tabs[index];
        }
    }

    public void MoveActiveTab(int delta)
    {
        var from = ActiveIndex;
        var to = Math.Clamp(from + delta, 0, Tabs.Count - 1);
        if (from != to)
        {
            Tabs.Move(from, to);
            OnPropertyChanged(nameof(ActiveIndex));
        }
    }

    /// <summary>Puts a tab at another place in the strip (dragging a tab with the mouse).</summary>
    public void MoveTab(PanelViewModel tab, int index)
    {
        var from = Tabs.IndexOf(tab);
        var to = Math.Clamp(index, 0, Tabs.Count - 1);
        if (from >= 0 && from != to)
        {
            Tabs.Move(from, to);
            OnPropertyChanged(nameof(ActiveIndex));
        }
    }

    /// <summary>Replaces all tabs (loading a .tab file).</summary>
    public async Task ReplaceTabsAsync(IReadOnlyList<PanelSettings> states, int activeIndex)
    {
        if (states.Count == 0)
        {
            return;
        }
        var old = Tabs.ToList();
        var created = new List<PanelViewModel>();
        foreach (var state in states)
        {
            var panel = Create(state.Clone());
            panel.Drives = ActiveTab.Drives;
            panel.Suspend();
            panel.IsCurrentTab = false;
            Tabs.Add(panel);
            created.Add(panel);
            var path = state.Path is not null && VfsPath.TryParse(state.Path, out var p) ? p : ActiveTab.CurrentPath;
            if (path is not null)
            {
                await panel.NavigateAsync(path!);
            }
        }
        ActiveTab = created[Math.Clamp(activeIndex, 0, created.Count - 1)];
        foreach (var tab in old)
        {
            Tabs.Remove(tab);
            tab.Dispose();
        }
    }

    /// <summary>Tab states for saving.</summary>
    public List<PanelSettings> SnapshotStates() => Tabs.Select(t =>
    {
        var state = t.State.Clone();
        if (!_settings.SaveTabFilters)
        {
            state.Filter = null;
        }
        return state;
    }).ToList();

    public void Dispose()
    {
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }
    }
}
