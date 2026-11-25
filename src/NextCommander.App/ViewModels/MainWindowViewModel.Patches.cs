using NextCommander.App.Services;
using NextCommander.Core.Commands;
using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>
/// Applying a patch (Tools > Apply patch...; the compare and merge plan, phase 5, 25 November 2026): the .patch / .diff file under
/// the cursor goes to the other panel's folder (a patch chosen with the picker to the active panel's folder); each of its
/// files is shown with how many of its changes fit, a file's result can be seen in the compare window first, then the
/// changes are written in each file's own encoding and line ends.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>One file of a patch: where it goes, its text now, the result, what is wrong.</summary>
    internal sealed record PatchFile(FilePatch Patch, string Name, VfsPath? Target, TextContent? Original, PatchResult? Result, string? Problem)
    {
        public bool IsNew => UnifiedDiff.IsNewFile(Patch);

        public bool IsDeleted => UnifiedDiff.IsDeletedFile(Patch);

        public int Fitting => Patch.Hunks.Count - (Result?.Rejected.Count ?? Patch.Hunks.Count);

        /// <summary>Something of it is written (a deleted file only when all of it fits).</summary>
        public bool Applies => Problem is null && Result is not null && Fitting > 0 && (!IsDeleted || Fitting == Patch.Hunks.Count);
    }

    private void RegisterPatchCommands(CommandRegistry c) =>
        c.Register("nc_ApplyPatch", "Apply a patch (.patch / .diff) to the files of a folder", path => ApplyPatchAsync(path));

    private static readonly string[] s_patchActions = ["Apply the patch"];

    private async Task ApplyPatchAsync(string? parameter)
    {
        // the patch and the folder it goes to
        string? patchPath;
        VfsPath? folder;
        if (!string.IsNullOrWhiteSpace(parameter))
        {
            patchPath = parameter.Trim().Trim('"');
            folder = ActivePanel.CurrentPath;
        }
        else if (ActivePanel.CursorItem?.Entry is { IsDirectory: false } cursor
            && (cursor.Extension.Equals("patch", StringComparison.OrdinalIgnoreCase) || cursor.Extension.Equals("diff", StringComparison.OrdinalIgnoreCase)))
        {
            patchPath = cursor.Path.ToString();
            folder = OtherPanel.CurrentPath;
        }
        else
        {
            patchPath = await _dialogs.PickOpenFileAsync("Choose the patch (.patch, .diff)");
            folder = ActivePanel.CurrentPath;
        }
        if (patchPath is null || folder is null || !VfsPath.TryParse(patchPath, out var patchVfs) || patchVfs is null)
        {
            return;
        }
        string text;
        try
        {
            text = (await ReadTextAsync(patchVfs)).Lines is var lines ? string.Join('\n', lines) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync("Apply patch", ex.Message);
            return;
        }
        var patches = UnifiedDiff.Parse(text);
        if (patches.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Apply patch", $"No changes were found in {patchVfs.Name}.");
            return;
        }
        var plan = new List<PatchFile>();
        foreach (var patch in patches)
        {
            plan.Add(await PlanAsync(patch, folder));
        }
        await ShowPatchPlanAsync(patchVfs.Name, folder, plan);
    }

    /// <summary>The list of the patch's files: Enter shows one's result in the compare window, the button applies them all.</summary>
    internal async Task ShowPatchPlanAsync(string patchName, VfsPath folder, List<PatchFile> plan)
    {
        while (true)
        {
            var items = plan.Select(p => new QuickListItem(p.Name, Describe(p), p)).ToList();
            var choice = await _dialogs.ShowQuickListAsync($"Apply {patchName} to {folder.ToDisplayString()} (Enter: see a file's result)", items, s_patchActions);
            if (choice?.Item.Value is not PatchFile chosen)
            {
                return;
            }
            if (choice.Action == 0)
            {
                await WritePatchAsync(plan);
                return;
            }
            await PreviewPatchAsync(chosen);
        }
    }

    private static string Describe(PatchFile file) =>
        file.Problem is { } problem ? problem
        : file.IsNew ? "new file"
        : file.IsDeleted ? (file.Applies ? "deleted" : "not deleted: the file is not as the patch expects")
        : file.Fitting == file.Patch.Hunks.Count ? (file.Patch.Hunks.Count == 1 ? "1 change fits" : $"all {file.Patch.Hunks.Count} changes fit")
        : $"{file.Fitting} of {file.Patch.Hunks.Count} changes fit";

    /// <summary>Where one file of a patch is, and its result.</summary>
    internal async Task<PatchFile> PlanAsync(FilePatch patch, VfsPath folder)
    {
        var candidates = UnifiedDiff.Candidates(patch);
        var name = candidates.Count > 0 ? candidates[0] : patch.NewPath;
        if (UnifiedDiff.IsNewFile(patch))
        {
            var target = Under(folder, name);
            if (await _registry.Resolve(target).GetEntryAsync(target, CancellationToken.None) is not null)
            {
                return new PatchFile(patch, name, target, null, null, "the new file exists already");
            }
            var created = TextContent.Empty();
            var result = UnifiedDiff.Apply([], patch, endsWithNewline: true);
            return new PatchFile(patch, name, target, created, result, null);
        }
        foreach (var candidate in candidates)
        {
            var target = Under(folder, candidate);
            if (await _registry.Resolve(target).GetEntryAsync(target, CancellationToken.None) is not { IsDirectory: false })
            {
                continue;
            }
            try
            {
                var original = await ReadTextAsync(target);
                return new PatchFile(patch, candidate, target, original, UnifiedDiff.Apply(original.Lines, patch, original.EndsWithNewline), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new PatchFile(patch, candidate, target, null, null, ex.Message);
            }
        }
        return new PatchFile(patch, name, null, null, null, "not found in this folder");
    }

    private static VfsPath Under(VfsPath folder, string relative)
    {
        var path = folder;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Combine(part);
        }
        return path;
    }

    private async Task<TextContent> ReadTextAsync(VfsPath path)
    {
        await using var stream = await _registry.Resolve(path).OpenReadAsync(path, CancellationToken.None);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return TextContent.Read(memory.ToArray());
    }

    /// <summary>A file's result beside its text now (the compare window; the right side is a copy in the temp folder).</summary>
    private async Task PreviewPatchAsync(PatchFile file)
    {
        if (file.Result is null || file.Original is null || file.Target is null)
        {
            await _dialogs.ShowMessageAsync("Apply patch", $"{file.Name}: {Describe(file)}");
            return;
        }
        var folder = Path.Combine(Path.GetTempPath(), "nc-patch", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var leafName = file.Name.Split('/')[^1];
        var after = Path.Combine(folder, leafName);
        var before = Path.Combine(folder, "before-" + leafName);
        var result = Patched(file);
        await File.WriteAllBytesAsync(after, result.Bytes());
        if (!file.IsNew && await _registry.Resolve(file.Target).GetEntryAsync(file.Target, CancellationToken.None) is { } current)
        {
            var patched = (await _registry.Resolve(VfsPath.Local(after)).GetEntryAsync(VfsPath.Local(after), CancellationToken.None))!;
            _dialogs.ShowCompare(current, patched, Tools);
            return;
        }
        await File.WriteAllBytesAsync(before, TextContent.Empty().Bytes());
        var empty = (await _registry.Resolve(VfsPath.Local(before)).GetEntryAsync(VfsPath.Local(before), CancellationToken.None))!;
        var created = (await _registry.Resolve(VfsPath.Local(after)).GetEntryAsync(VfsPath.Local(after), CancellationToken.None))!;
        _dialogs.ShowCompare(empty, created, Tools);
    }

    /// <summary>A file as the patch makes it (its encoding, BOM and line ends kept).</summary>
    private static TextContent Patched(PatchFile file) => new()
    {
        Lines = [.. file.Result!.Lines],
        Encoding = file.Original!.Encoding,
        Bom = file.Original.Bom,
        NewLine = file.Original.NewLine,
        EndsWithNewline = file.Result.EndsWithNewline,
    };

    /// <summary>Writes what fits; tells what did not.</summary>
    internal async Task WritePatchAsync(IReadOnlyList<PatchFile> plan)
    {
        var written = 0;
        var problems = new List<string>();
        foreach (var file in plan)
        {
            if (!file.Applies)
            {
                problems.Add($"{file.Name}: {Core.Localization.Localizer.T(Describe(file))}");
                continue;
            }
            try
            {
                var provider = _registry.Resolve(file.Target!);
                if (file.IsDeleted)
                {
                    await provider.DeleteAsync(file.Target!, recursive: false, CancellationToken.None);
                }
                else
                {
                    if (file.IsNew && file.Target!.Parent is { } parent)
                    {
                        await provider.CreateDirectoryAsync(parent, CancellationToken.None);
                    }
                    await using var stream = await provider.OpenWriteAsync(file.Target!, overwrite: true, CancellationToken.None);
                    await stream.WriteAsync(Patched(file).Bytes());
                }
                written++;
                if (file.Fitting < file.Patch.Hunks.Count)
                {
                    problems.Add($"{file.Name}: {Core.Localization.Localizer.T(Describe(file))}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.EncoderFallbackException)
            {
                problems.Add($"{file.Name}: {ex.Message}");
            }
        }
        await Left.RefreshAsync();
        await Right.RefreshAsync();
        // a message made of pieces: each translated here (the dialog translates whole texts only)
        var summary = Core.Localization.Localizer.T(written == 1 ? "The patch changed 1 file." : $"The patch changed {written} files.");
        await _dialogs.ShowMessageAsync("Apply patch", problems.Count == 0 ? summary : summary + "\n\n" + string.Join("\n", problems));
    }
}
