using System.Text.Json;
using Avalonia.Threading;
using Jint;
using NextCommander.App.ViewModels;
using NextCommander.Core.Scripting;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Services;

/// <summary>
/// Runs a JavaScript file as a command (nc_RunScript; user's request, 24 November 2026). The script runs in Jint on a
/// thread of its own; what it asks of the program (<c>nc.run</c>, the panels, the dialogs) is done on the UI thread and
/// waited for, so the window stays usable. The <c>nc</c> object:
/// <list type="bullet">
/// <item><c>nc.args</c>: the text after the script's name; <c>nc.script</c>: its path.</item>
/// <item><c>nc.run("nc_Copy")</c>: runs commands (a chain with commas; true when all are known).</item>
/// <item><c>nc.panel()</c> / <c>nc.otherPanel()</c>: <c>{ path, cursor, selected, files, folders }</c> (full paths, names).</item>
/// <item><c>nc.selected()</c>: the full paths of the selected files (or the one under the cursor).</item>
/// <item><c>nc.navigate(path)</c>, <c>nc.refresh()</c>: the active panel.</item>
/// <item><c>nc.message(text)</c>, <c>nc.confirm(text)</c>, <c>nc.prompt(text, default)</c>: dialogs.</item>
/// <item><c>nc.readText(path)</c>, <c>nc.writeText(path, text)</c>, <c>nc.appendText(path, text)</c>, <c>nc.exists(path)</c>,
/// <c>nc.list(path)</c>, <c>nc.log(text)</c>: local files; the log goes to logs/scripts.log.</item>
/// </list>
/// </summary>
public sealed class ScriptRunner(MainWindowViewModel vm, IDialogService dialogs, string logFile)
{
    private const string Prelude = """
        var nc = {
          args: __args, script: __script,
          run: function (line) { return __run(String(line)); },
          panel: function () { return JSON.parse(__panel(true)); },
          otherPanel: function () { return JSON.parse(__panel(false)); },
          selected: function () { return JSON.parse(__selected()); },
          navigate: function (path) { return __navigate(String(path)); },
          refresh: function () { __refresh(); },
          message: function (text) { __message(String(text)); },
          confirm: function (text) { return __confirm(String(text)); },
          prompt: function (text, value) { return __prompt(String(text), value === undefined || value === null ? "" : String(value)); },
          readText: function (path) { return readText(String(path), null); },
          writeText: function (path, text) { __write(String(path), String(text), false); },
          appendText: function (path, text) { __write(String(path), String(text), true); },
          exists: function (path) { return __exists(String(path)); },
          list: function (path) { return JSON.parse(__list(String(path))); },
          log: function (text) { __log(String(text)); }
        };
        """;

    /// <summary>The script running now (one at a time), to stop it when the program closes.</summary>
    private CancellationTokenSource? _running;

    /// <summary>Stops the running script (the program closes).</summary>
    public void Stop() => _running?.Cancel();

    /// <summary>Runs <paramref name="file"/>; an error of the script is shown with its line.</summary>
    public async Task RunAsync(string file, string? args)
    {
        string source;
        try
        {
            source = await File.ReadAllTextAsync(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowMessageAsync("Script", ex.Message);
            return;
        }
        using var cts = new CancellationTokenSource();
        _running = cts;
        string? failure = null;
        await Task.Run(() =>
        {
            try
            {
                var engine = new Engine(o => o.LimitRecursion(512).LimitMemory(512L * 1024 * 1024).Strict(false).CancellationToken(cts.Token));
                engine.SetValue("readText", new Func<string, int?, string?>(ScriptColumnProvider.ReadText));
                engine.SetValue("__args", args ?? string.Empty);
                engine.SetValue("__script", file);
                engine.SetValue("__run", new Func<string, bool>(line => Ui(() => vm.Commands.ExecuteAsync(line))));
                engine.SetValue("__panel", new Func<bool, string>(active => Ui(() => Task.FromResult(PanelJson(active ? vm.ActivePanel : vm.OtherPanel)))));
                engine.SetValue("__selected", new Func<string>(() => Ui(() => Task.FromResult(
                    JsonSerializer.Serialize(vm.ActivePanel.GetSelectedEntries().Select(e => PathText(e.Path)).ToList())))));
                engine.SetValue("__navigate", new Func<string, bool>(path => Ui(async () =>
                {
                    if (!VfsPath.TryParse(path, out var target) || target is null)
                    {
                        return false;
                    }
                    await vm.ActivePanel.NavigateAsync(target);
                    return vm.ActivePanel.CurrentPath == target;
                })));
                engine.SetValue("__refresh", new Action(() => Ui(async () =>
                {
                    await vm.ActivePanel.RefreshAsync();
                    return true;
                })));
                engine.SetValue("__message", new Action<string>(text => Ui(async () =>
                {
                    await dialogs.ShowMessageAsync("Script", text);
                    return true;
                })));
                engine.SetValue("__confirm", new Func<string, bool>(text => Ui(() => dialogs.ConfirmAsync("Script", text))));
                engine.SetValue("__prompt", new Func<string, string, string?>((text, value) => Ui(() => dialogs.PromptAsync("Script", text, value))));
                engine.SetValue("__write", new Action<string, string, bool>((path, text, append) =>
                {
                    if (append)
                    {
                        File.AppendAllText(path, text);
                    }
                    else
                    {
                        File.WriteAllText(path, text);
                    }
                }));
                engine.SetValue("__exists", new Func<string, bool>(path => File.Exists(path) || Directory.Exists(path)));
                engine.SetValue("__list", new Func<string, string>(path => JsonSerializer.Serialize(
                    Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList() : [])));
                engine.SetValue("__log", new Action<string>(Log));
                engine.Execute(Prelude);
                engine.Execute(source, file);
            }
            catch (Exception ex) when (ScriptColumnProvider.IsScriptError(ex) || ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or NotSupportedException or OperationCanceledException)
            {
                failure = ex is OperationCanceledException ? "The script was stopped." : Describe(ex);
                Log($"{Path.GetFileName(file)}: {failure}");
            }
        });
        _running = null;
        if (failure is not null && !cts.IsCancellationRequested)
        {
            await dialogs.ShowMessageAsync("Script", $"{Path.GetFileName(file)}: {failure}");
        }
    }

    /// <summary>The message of a script's error with its place ("line 3: x is not defined").</summary>
    internal static string Describe(Exception ex) => ex switch
    {
        Jint.Runtime.JavaScriptException js => $"line {js.Location.Start.Line}: {js.Error}",
        Acornima.ParseErrorException parse => $"line {parse.LineNumber}: {parse.Description}",
        _ => ex.Message,
    };

    /// <summary>Runs <paramref name="work"/> on the UI thread and waits for it (the script's thread waits, not the window).</summary>
    private static T Ui<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work).GetAwaiter().GetResult();

    private static string PathText(VfsPath path) => path.IsLocal ? path.Path : path.ToDisplayString();

    private static string PanelJson(PanelViewModel panel)
    {
        var items = panel.Items.Where(i => !i.IsParent && i.Entry is not null).ToList();
        return JsonSerializer.Serialize(new
        {
            path = panel.CurrentPath is { } p ? PathText(p) : null,
            cursor = panel.CursorItem is { IsParent: false, Entry: { } e } ? PathText(e.Path) : null,
            selected = items.Where(i => i.IsMarked).Select(i => PathText(i.Entry!.Path)).ToList(),
            files = items.Where(i => !i.Entry!.IsDirectory).Select(i => i.Entry!.Name).ToList(),
            folders = items.Where(i => i.Entry!.IsDirectory).Select(i => i.Entry!.Name).ToList(),
        });
    }

    private void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the log is a help, not a must
        }
    }
}
