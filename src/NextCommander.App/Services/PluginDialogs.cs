using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Services;

/// <summary>
/// The program's dialogs lent to the plugins' actions (<see cref="IPluginDialogs"/>): a prompt, a list to choose from
/// (the quick list), a yes / no, and a long text shown in the Lister from a file of the temp folder (a ".diff" is colored).
/// </summary>
public sealed class PluginDialogs(IDialogService dialogs, Func<ToolContext> tools) : IPluginDialogs
{
    public Task<string?> PromptAsync(string title, string message, string initialText) => dialogs.PromptAsync(title, message, initialText);

    public async Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices)
    {
        var result = await dialogs.ShowQuickListAsync(title, [.. choices.Select(c => new QuickListItem(c, null, c))]);
        return result?.Item.Value as string;
    }

    public Task<bool> ConfirmAsync(string title, string message) => dialogs.ConfirmAsync(title, message);

    public async Task<bool> ResolveConflictsAsync(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return false;
        }
        var entry = new VfsEntry { Name = info.Name, Path = VfsPath.Local(path), Size = info.Length, LastWriteTime = info.LastWriteTime };
        return await dialogs.ShowMergeAsync(entry, null, tools());
    }

    public async Task ShowTextAsync(string fileName, string text)
    {
        var folder = Path.Combine(Path.GetTempPath(), "NextCommander", "plugin-texts");
        Directory.CreateDirectory(folder);
        var safe = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = Path.Combine(folder, safe.Length > 0 ? safe : "text.txt");
        await File.WriteAllTextAsync(file, text);
        var info = new FileInfo(file);
        var entry = new VfsEntry { Name = info.Name, Path = VfsPath.Local(file), Size = info.Length, LastWriteTime = info.LastWriteTime };
        dialogs.ShowLister(new ListerRequest([entry], 0, tools()));
    }
}
