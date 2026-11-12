using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Services;

/// <summary>
/// Files on the system clipboard (Ctrl+C / Ctrl+X / Ctrl+V), interoperable with Explorer/Finder/Nautilus where the
/// platform supports file lists. Paths are also put on the clipboard as text as a fallback.
/// </summary>
public sealed class FileClipboard
{
    /// <summary>Windows: 2 = move (cut), 5 = copy. Explorer honours it on paste.</summary>
    private static readonly DataFormat<byte[]> s_preferredDropEffect = DataFormat.CreateBytesPlatformFormat("Preferred DropEffect");

    private HashSet<string> _cutPaths = new(StringComparer.Ordinal);

    public IClipboard? Clipboard { get; set; }

    public IStorageProvider? Storage { get; set; }

    public async Task SetAsync(IReadOnlyList<VfsEntry> entries, bool cut)
    {
        if (Clipboard is null)
        {
            return;
        }
        var local = entries.Where(e => e.Path.IsLocal).Select(e => e.Path.Path).ToList();
        var data = await CreateTransferAsync(Storage, entries);
        if (OperatingSystem.IsWindows())
        {
            var effect = new DataTransferItem();
            effect.Set(s_preferredDropEffect, [(byte)(cut ? 2 : 5), 0, 0, 0]);
            data.Add(effect);
        }
        await Clipboard.SetDataAsync(data);
        _cutPaths = cut ? local.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Local paths on the clipboard and whether they were cut (move on paste).</summary>
    public async Task<(IReadOnlyList<string> Paths, bool Cut)> GetAsync()
    {
        if (Clipboard is null)
        {
            return ([], false);
        }
        var paths = new List<string>();
        if (await Clipboard.TryGetFilesAsync() is { } items)
        {
            paths.AddRange(items.Select(i => i.TryGetLocalPath()).OfType<string>());
        }
        if (paths.Count == 0 && await Clipboard.TryGetTextAsync() is { Length: > 0 } text)
        {
            paths.AddRange(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Trim('"'))
                .Where(p => Path.IsPathRooted(p) && (File.Exists(p) || Directory.Exists(p))));
        }

        var cut = paths.Count > 0 && paths.All(_cutPaths.Contains);
        if (!cut && OperatingSystem.IsWindows() && await Clipboard.TryGetValueAsync(s_preferredDropEffect) is { Length: >= 1 } effect)
        {
            cut = (effect[0] & 2) != 0 && (effect[0] & 1) == 0;
        }
        return (paths, cut);
    }

    /// <summary>After a cut has been pasted (moved), the clipboard content is stale.</summary>
    public async Task ClearCutAsync()
    {
        _cutPaths.Clear();
        if (Clipboard is not null)
        {
            await Clipboard.ClearAsync();
        }
    }

    /// <summary>Files (as storage items, one item each) plus their paths as text.</summary>
    public static async Task<DataTransfer> CreateTransferAsync(IStorageProvider? storage, IReadOnlyList<VfsEntry> entries)
    {
        var data = new DataTransfer();
        var local = entries.Where(e => e.Path.IsLocal).ToList();
        if (local.Count == 0)
        {
            return data;
        }
        if (storage is not null)
        {
            foreach (var entry in local)
            {
                IStorageItem? item = entry.IsDirectory
                    ? await storage.TryGetFolderFromPathAsync(entry.Path.Path)
                    : await storage.TryGetFileFromPathAsync(entry.Path.Path);
                if (item is not null)
                {
                    data.Add(DataTransferItem.CreateFile(item));
                }
            }
        }
        data.Add(DataTransferItem.CreateText(string.Join(Environment.NewLine, local.Select(e => e.Path.Path))));
        return data;
    }
}
