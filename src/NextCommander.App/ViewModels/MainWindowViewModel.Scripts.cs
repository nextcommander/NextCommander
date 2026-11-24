using NextCommander.App.Services;
using NextCommander.Core.Commands;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>
/// JavaScript scripts (Commands > Scripts; user's request, 24 November 2026): the .js files of scripts/ in the settings
/// folder run as commands (nc_RunScript name [args]), those of scripts/columns give custom columns.
/// </summary>
public partial class MainWindowViewModel
{
    private ScriptRunner? _scripts;

    /// <summary>scripts/ in the settings folder.</summary>
    public string ScriptsFolder => Path.Combine(_store.Directory, "scripts");

    /// <summary>scripts/columns in the settings folder (read at the start of the program).</summary>
    public string ScriptColumnsFolder => Path.Combine(ScriptsFolder, "columns");

    private ScriptRunner Scripts => _scripts ??= new ScriptRunner(this, _dialogs, Path.Combine(_store.Directory, "logs", "scripts.log"));

    /// <summary>The scripts that run as commands (the names of the .js files of the scripts folder).</summary>
    public IReadOnlyList<string> ScriptNames() =>
        Directory.Exists(ScriptsFolder)
            ? [.. Directory.EnumerateFiles(ScriptsFolder, "*.js").Select(Path.GetFileName).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase)]
            : [];

    private void RegisterScriptCommands(CommandRegistry c)
    {
        c.Register("nc_RunScript", "Run a JavaScript script of the scripts folder (parameter: its name and what it is given)", RunScriptAsync);
        c.Register("nc_NewScript", "New JavaScript script: an example in the scripts folder, opened in the editor", _ => NewScriptAsync(column: false));
        c.Register("nc_NewColumnScript", "New JavaScript column: an example in scripts/columns, opened in the editor", _ => NewScriptAsync(column: true));
        c.Register("nc_OpenScriptsFolder", "Open the scripts folder in the panel", _ => OpenScriptsFolderAsync());
    }

    /// <summary>"name.js rest of the line": the file (in the scripts folder, .js optional, or a full path) and its arguments.</summary>
    internal (string? File, string? Args) ResolveScript(string? parameter)
    {
        var text = (parameter ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return (null, null);
        }
        string name;
        string? args;
        if (text.StartsWith('"') && text.IndexOf('"', 1) is var close and > 0)
        {
            name = text[1..close];
            args = text[(close + 1)..].Trim();
        }
        else
        {
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            (name, args) = space < 0 ? (text, null) : (text[..space], text[(space + 1)..].Trim());
        }
        var candidates = Path.IsPathFullyQualified(name) ? [name] : new[] { Path.Combine(ScriptsFolder, name), Path.Combine(ScriptsFolder, name + ".js") };
        return (candidates.FirstOrDefault(File.Exists), string.IsNullOrEmpty(args) ? null : args);
    }

    private async Task RunScriptAsync(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
        {
            var names = ScriptNames();
            if (names.Count == 0)
            {
                await _dialogs.ShowMessageAsync("Scripts", "There is no script yet. Commands > Scripts > New script... writes an example.");
                return;
            }
            var result = await _dialogs.ShowQuickListAsync("Scripts", [.. names.Select(n => new QuickListItem(n, null, n))]);
            if (result?.Item.Value is not string chosen)
            {
                return;
            }
            parameter = "\"" + chosen + "\"";
        }
        var (file, args) = ResolveScript(parameter);
        if (file is null)
        {
            await _dialogs.ShowMessageAsync("Scripts", $"No script found: {parameter.Trim()}");
            return;
        }
        await Scripts.RunAsync(file, args);
    }

    private async Task NewScriptAsync(bool column)
    {
        var folder = column ? ScriptColumnsFolder : ScriptsFolder;
        var name = await _dialogs.PromptAsync(column ? "New column script" : "New script", "File name:", column ? "lines.js" : "script.js", selectBaseName: true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        name = name.Trim();
        if (!name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            name += ".js";
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await _dialogs.ShowMessageAsync("Scripts", $"\"{name}\" cannot be a file name.");
            return;
        }
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, column ? ScriptTemplates.Column : ScriptTemplates.Command);
        }
        var vfs = VfsPath.Local(path);
        await ActivePanel.NavigateAsync(VfsPath.Local(folder), focusName: name);
        if (await _registry.Resolve(vfs).GetEntryAsync(vfs, CancellationToken.None) is { } entry)
        {
            await EditEntryAsync(entry);
        }
    }

    private Task OpenScriptsFolderAsync()
    {
        Directory.CreateDirectory(ScriptsFolder);
        return ActivePanel.NavigateAsync(VfsPath.Local(ScriptsFolder));
    }

    /// <summary>The program closes: a running script is stopped.</summary>
    private void StopScripts() => _scripts?.Stop();
}
