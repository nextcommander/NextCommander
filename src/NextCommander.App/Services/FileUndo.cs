using NextCommander.Core.Operations;
using NextCommander.Core.Rename;
using NextCommander.Core.Vfs;
using NextCommander.Platform.Trash;

namespace NextCommander.App.Services;

/// <summary>What an undo did: how many entries were put back and what could not be.</summary>
public sealed record UndoResult(int Done, IReadOnlyList<string> Problems);

/// <summary>A file operation that can be undone: what it was (shown before undoing it) and how it is undone.</summary>
public sealed record UndoStep(string Title, Func<CancellationToken, Task<UndoResult>> RunAsync)
{
    /// <summary>
    /// What undoing will do, one line each (shown and asked before it runs; user's request, 24 November 2026), already
    /// in the user's language.
    /// </summary>
    public IReadOnlyList<string> Plan { get; init; } = [];
}

/// <summary>
/// The file operations of the main window that can be undone, the latest last (nc_Undo, Ctrl+Z; the user's request,
/// 23 November 2026): copy, move, rename, multi-rename, new folder and moving to the recycle bin. Deleting for good,
/// Explorer's copy and the elevated retries cannot be undone.
/// </summary>
public sealed class UndoHistory
{
    /// <summary>How many operations are kept.</summary>
    public const int Capacity = 30;

    private readonly List<UndoStep> _steps = [];

    public event EventHandler? Changed;

    public int Count => _steps.Count;

    /// <summary>The operation Ctrl+Z undoes next (null: none).</summary>
    public UndoStep? Next => _steps.Count > 0 ? _steps[^1] : null;

    public void Push(UndoStep? step)
    {
        if (step is null)
        {
            return;
        }
        _steps.Add(step);
        if (_steps.Count > Capacity)
        {
            _steps.RemoveAt(0);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes the latest operation off the list (it is undone once, whatever comes of it).</summary>
    public UndoStep? Pop()
    {
        if (_steps.Count == 0)
        {
            return null;
        }
        var step = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return step;
    }
}

/// <summary>How each kind of file operation is undone.</summary>
public static class FileUndo
{
    private static string Items(int count, string one) => count == 1 ? $"“{one}”" : $"{count} items";

    /// <summary>How many names a plan lists before "... and N more".</summary>
    private const int Listed = 8;

#pragma warning disable CA1863 // the pattern is the translation of the running language
    /// <summary>A translated pattern filled in.</summary>
    private static string T(string pattern, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Core.Localization.Localizer.T(pattern), args);
#pragma warning restore CA1863

    /// <summary>The sentence for one item or for several (both are translated patterns).</summary>
    private static string N(int count, string one, string many, params object[] rest) =>
        count == 1 ? T(one, rest) : T(many, [count, .. rest]);

    /// <summary>"  • a.txt" lines (at most <see cref="Listed"/>, then "... and N more").</summary>
    private static IEnumerable<string> Names(IEnumerable<string> names)
    {
        var all = names.ToList();
        foreach (var name in all.Take(Listed))
        {
            yield return "  • " + name;
        }
        if (all.Count > Listed)
        {
            yield return "  " + T("... and {0} more", all.Count - Listed);
        }
    }

    /// <summary>What undoing a copy or move will do.</summary>
    internal static List<string> PlanOf(CopyMoveJob job, IReadOnlyList<CopyMoveDone> done, bool trash)
    {
        var plan = new List<string>();
        var target = job.TargetDirectory.ToDisplayString();
        var files = done.Where(d => d.Kind == CopyMoveDoneKind.File && !d.Overwrote).ToList();
        var replaced = done.Count(d => d.Kind == CopyMoveDoneKind.File && d.Overwrote);
        var aside = done.Where(d => d.Kind == CopyMoveDoneKind.TargetRenamedAside).ToList();
        if (job.IsMove)
        {
            var whole = done.Where(d => d.Kind is CopyMoveDoneKind.File || (d.Kind == CopyMoveDoneKind.Directory && d.Whole)).ToList();
            var from = whole.FirstOrDefault()?.Source.Parent?.ToDisplayString() ?? "?";
            plan.Add(N(whole.Count, "1 item goes back from {0} to {1}:", "{0} items go back from {1} to {2}:", target, from));
            plan.AddRange(Names(whole.Select(d => d.Target.Name)));
        }
        else
        {
            plan.Add(trash
                ? N(files.Count, "The copy in {0} is moved to the Recycle Bin:", "The {0} copies in {1} are moved to the Recycle Bin:", target)
                : N(files.Count, "The copy in {0} is deleted:", "The {0} copies in {1} are deleted:", target));
            plan.AddRange(Names(files.Select(d => d.Target.Name)));
            if (done.Count(d => d.Kind == CopyMoveDoneKind.Directory && d.Created) is var folders and > 0)
            {
                plan.Add(N(folders, "The folder the copy made is deleted if it is empty.", "The {0} folders the copy made are deleted if they are empty."));
            }
            if (replaced > 0)
            {
                plan.Add(N(replaced, "1 file the copy replaced stays as it is: what was there before cannot be brought back.",
                    "{0} files the copy replaced stay as they are: what was there before cannot be brought back."));
            }
        }
        if (aside.Count > 0)
        {
            plan.Add(N(aside.Count, "1 file that was renamed to make room gets its name back.", "{0} files that were renamed to make room get their names back."));
        }
        return plan;
    }

    /// <summary>
    /// A copy: the copies are removed (to the recycle bin when local), then the folders the copy made, if empty; files it
    /// overwrote are left (what was there is gone). A move: everything goes back where it was (renamed back when it can be,
    /// else copied back and removed). A file renamed aside to make room gets its name back.
    /// </summary>
    public static UndoStep? ForCopyMove(ProviderRegistry registry, CopyMoveJob job, ITrashService? trash)
    {
        var done = job.Done;
        if (done.Count == 0 || done.All(d => d.Kind == CopyMoveDoneKind.Directory && !d.Whole && !d.Created))
        {
            return null;
        }
        var shown = Items(job.Sources.Count, job.Sources.Count > 0 ? job.Sources[0].Name : "?");
        var title = job.IsMove ? $"Move of {shown} to {job.TargetDirectory.ToDisplayString()}" : $"Copy of {shown} to {job.TargetDirectory.ToDisplayString()}";
        return new UndoStep(title, ct => job.IsMove ? UndoMoveAsync(registry, done, ct) : UndoCopyAsync(registry, done, trash, ct))
        {
            Plan = PlanOf(job, done, trash is { IsAvailable: true } && job.TargetDirectory.IsLocal),
        };
    }

    private static async Task<UndoResult> UndoCopyAsync(ProviderRegistry registry, IReadOnlyList<CopyMoveDone> done, ITrashService? trash, CancellationToken ct)
    {
        var count = 0;
        var problems = new List<string>();
        foreach (var step in done.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (step.Kind)
                {
                    case CopyMoveDoneKind.File when step.Overwrote:
                        problems.Add($"{step.Target.ToDisplayString()}: it replaced a file, which cannot be brought back; kept.");
                        break;
                    case CopyMoveDoneKind.File:
                        if (await registry.Resolve(step.Target).GetEntryAsync(step.Target, ct) is not null)
                        {
                            await RemoveAsync(registry, step.Target, recursive: false, trash, ct);
                            count++;
                        }
                        break;
                    case CopyMoveDoneKind.Directory when step.Created:
                        if (await RemoveIfEmptyAsync(registry, step.Target, ct))
                        {
                            count++;
                        }
                        break;
                    case CopyMoveDoneKind.TargetRenamedAside:
                        await RenameBackAsync(registry, step.Target, step.Source, ct);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                problems.Add($"{step.Target.ToDisplayString()}: {ex.Message}");
            }
        }
        return new UndoResult(count, problems);
    }

    private static async Task<UndoResult> UndoMoveAsync(ProviderRegistry registry, IReadOnlyList<CopyMoveDone> done, CancellationToken ct)
    {
        var count = 0;
        var problems = new List<string>();
        foreach (var step in done.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (step.Kind)
                {
                    case CopyMoveDoneKind.File when step.KeptSource:
                        if (await registry.Resolve(step.Target).GetEntryAsync(step.Target, ct) is not null)
                        {
                            await registry.Resolve(step.Target).DeleteAsync(step.Target, recursive: false, ct);
                            count++;
                        }
                        break;
                    case CopyMoveDoneKind.File:
                    case CopyMoveDoneKind.Directory when step.Whole:
                        await MoveBackAsync(registry, step.Target, step.Source, ct);
                        count++;
                        break;
                    case CopyMoveDoneKind.Directory:
                        // a folder the move went through: there again at the source (even if it had no files), gone from the target if it was made
                        await EnsureDirectoryAsync(registry, step.Source, ct);
                        if (step.Created)
                        {
                            await RemoveIfEmptyAsync(registry, step.Target, ct);
                        }
                        break;
                    case CopyMoveDoneKind.TargetRenamedAside:
                        await RenameBackAsync(registry, step.Target, step.Source, ct);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                problems.Add($"{step.Target.ToDisplayString()}: {ex.Message}");
            }
        }
        return new UndoResult(count, problems);
    }

    /// <summary>A rename: the new name back to the old one.</summary>
    public static UndoStep ForRename(ProviderRegistry registry, VfsPath from, VfsPath to) =>
        new UndoStep($"Rename of “{from.Name}” to “{to.Name}”", async ct =>
        {
            try
            {
                await RenameBackAsync(registry, to, from, ct);
                return new UndoResult(1, []);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new UndoResult(0, [$"{to.ToDisplayString()}: {ex.Message}"]);
            }
        })
        {
            Plan = [T("“{0}” is renamed back to “{1}” (in {2}).", to.Name, from.Name, to.Parent?.ToDisplayString() ?? "?")],
        };

    /// <summary>A multi-rename: every file back to its name (the job's own undo).</summary>
    public static UndoStep? ForMultiRename(MultiRenameJob job)
    {
        if (job.Done.Count == 0)
        {
            return null;
        }
        return new UndoStep($"Multi-rename of {Items(job.Done.Count, job.Done[0].From.Name)}", async ct =>
        {
            var undo = job.CreateUndo();
            await undo.RunAsync();
            return new UndoResult(undo.Done.Count, [.. undo.Errors.Select(e => $"{e.Item}: {e.Message}")]);
        })
        {
            Plan = [N(job.Done.Count, "1 file gets its name back:", "{0} files get their names back:"), .. Names(job.Done.Select(d => $"{d.To.Name} → {d.From.Name}"))],
        };
    }

    /// <summary>New folders: removed again while they are empty (deepest first).</summary>
    public static UndoStep? ForNewFolders(ProviderRegistry registry, IReadOnlyList<VfsPath> created)
    {
        if (created.Count == 0)
        {
            return null;
        }
        var plan = new List<string> { created.Count == 1 ? T("This folder is deleted if it is still empty:") : T("These folders are deleted if they are still empty:") };
        plan.AddRange(Names(created.Select(c => c.ToDisplayString())));
        return new UndoStep(created.Count == 1 ? $"New folder “{created[0].Name}”" : $"{created.Count} new folders", async ct =>
        {
            var count = 0;
            var problems = new List<string>();
            foreach (var folder in created.OrderByDescending(c => c.Path.Length))
            {
                try
                {
                    if (await RemoveIfEmptyAsync(registry, folder, ct))
                    {
                        count++;
                    }
                    else if (await registry.Resolve(folder).GetEntryAsync(folder, ct) is not null)
                    {
                        problems.Add($"{folder.ToDisplayString()}: not empty any more; kept.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    problems.Add($"{folder.ToDisplayString()}: {ex.Message}");
                }
            }
            return new UndoResult(count, problems);
        })
        {
            Plan = plan,
        };
    }

    /// <summary>
    /// Entries moved to the recycle bin: put back from it (the newest trash entry of each path, not older than the delete).
    /// Null where the trash cannot give things back (macOS).
    /// </summary>
    public static UndoStep? ForTrash(ITrashStore? store, DeleteJob job)
    {
        var trashed = job.Trashed;
        if (store is null || !store.CanRestore || trashed.Count == 0 || job.TrashedSince is not { } since)
        {
            return null;
        }
        return new UndoStep($"Delete of {Items(trashed.Count, trashed[0].Name)} to the recycle bin", ct => Task.Run(() =>
        {
            var count = 0;
            var problems = new List<string>();
            var items = store.List();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var path in trashed)
            {
                ct.ThrowIfCancellationRequested();
                var item = items.Where(i => i.OriginalPath is { } original
                        && string.Equals(Path.TrimEndingDirectorySeparator(original), Path.TrimEndingDirectorySeparator(path.Path), comparison)
                        && (i.Deleted is null || i.Deleted >= since.AddSeconds(-2)))
                    .OrderByDescending(i => i.Deleted ?? DateTime.MinValue).FirstOrDefault();
                if (item is null)
                {
                    problems.Add($"{path.ToDisplayString()}: not in the recycle bin any more.");
                    continue;
                }
                try
                {
                    store.Restore(item);
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    problems.Add($"{path.ToDisplayString()}: {ex.Message}");
                }
            }
            return new UndoResult(count, problems);
        }, ct))
        {
            Plan = [N(trashed.Count, "1 item comes back from the Recycle Bin to where it was:", "{0} items come back from the Recycle Bin to where they were:"),
                .. Names(trashed.Select(t => t.ToDisplayString()))],
        };
    }

    /// <summary>Puts an entry back at its old place: renamed when the provider can, else copied back and removed.</summary>
    private static async Task MoveBackAsync(ProviderRegistry registry, VfsPath now, VfsPath before, CancellationToken ct)
    {
        var from = registry.Resolve(now);
        var to = registry.Resolve(before);
        if (await from.GetEntryAsync(now, ct) is not { } entry)
        {
            throw new IOException("It is not there any more.");
        }
        if (await to.GetEntryAsync(before, ct) is not null)
        {
            throw new IOException("Something is at its old place again.");
        }
        if (before.Parent is { } parent)
        {
            await EnsureDirectoryAsync(registry, parent, ct);
        }
        if (ReferenceEquals(from, to) && from.Capabilities.HasFlag(ProviderCapabilities.Rename))
        {
            try
            {
                await from.MoveAsync(now, before, overwrite: false, ct);
                return;
            }
            catch (CrossDeviceMoveException)
            {
                // copied back below
            }
        }
        var back = new CopyMoveJob(registry, [entry], before.Parent ?? before, move: true,
            new CopyOptions { SingleTargetName = before.Name, ConflictPolicy = ConflictPolicy.Skip });
        await back.RunAsync();
        if (back.Errors.Count > 0)
        {
            throw new IOException(back.Errors[0].Message);
        }
    }

    private static async Task RenameBackAsync(ProviderRegistry registry, VfsPath now, VfsPath before, CancellationToken ct)
    {
        var provider = registry.Resolve(now);
        if (await provider.GetEntryAsync(now, ct) is null)
        {
            throw new IOException("It is not there any more.");
        }
        var caseOnly = string.Equals(now.ToString(), before.ToString(), StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && await provider.GetEntryAsync(before, ct) is not null)
        {
            throw new IOException("Something is at its old name again.");
        }
        await provider.MoveAsync(now, before, overwrite: false, ct);
    }

    private static async Task EnsureDirectoryAsync(ProviderRegistry registry, VfsPath folder, CancellationToken ct)
    {
        var provider = registry.Resolve(folder);
        if (await provider.GetEntryAsync(folder, ct) is null)
        {
            await provider.CreateDirectoryAsync(folder, ct);
        }
    }

    /// <summary>Removes a folder when it is there and empty (true: removed).</summary>
    private static async Task<bool> RemoveIfEmptyAsync(ProviderRegistry registry, VfsPath folder, CancellationToken ct)
    {
        var provider = registry.Resolve(folder);
        if (await provider.GetEntryAsync(folder, ct) is not { IsDirectory: true } entry)
        {
            return false;
        }
        if (!entry.IsLink && (await provider.ListAsync(folder, ct)).Count > 0)
        {
            return false;
        }
        await provider.DeleteAsync(folder, recursive: false, ct);
        return true;
    }

    /// <summary>Removes a copy: a local one into the recycle bin (it can still be had), others for good.</summary>
    private static async Task RemoveAsync(ProviderRegistry registry, VfsPath path, bool recursive, ITrashService? trash, CancellationToken ct)
    {
        if (path.IsLocal && path.Container is null && trash is { IsAvailable: true })
        {
            await trash.MoveToTrashAsync(path.Path, ct);
            return;
        }
        await registry.Resolve(path).DeleteAsync(path, recursive, ct);
    }
}
