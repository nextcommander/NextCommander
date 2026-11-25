using NextCommander.Core.Commands;
using NextCommander.Core.Config;

namespace NextCommander.App.ViewModels;

/// <summary>
/// Macro recording (Tools > Record macro; user's request, 24 November 2026): while it records, every command run from
/// a menu, a key, a button or the command line is noted; at the end the list is saved as a user command (uc_Name), which
/// gets a key or a button like any other.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly HashSet<string> s_notRecorded = new(StringComparer.OrdinalIgnoreCase) { "nc_RecordMacro" };

    private List<string>? _macro;

    /// <summary>A macro is being recorded (the check mark of its menu item, the window's title).</summary>
    public bool IsRecordingMacro => _macro is not null;

    /// <summary>The commands recorded so far (null: not recording).</summary>
    public IReadOnlyList<string>? RecordedMacro => _macro;

    private void RegisterMacroCommands(CommandRegistry c)
    {
        c.Register("nc_RecordMacro", "Record a macro: the commands run from now on, saved as a user command (again: stop)", _ => ToggleMacroAsync());
        c.Register("nc_Macros", "The recorded macros: run or delete one", _ => ShowMacrosAsync());
        c.Register("nc_DeleteMacro", "Delete a recorded macro (parameter: its command, uc_...)", name => DeleteMacroAsync(name));
        c.Executing += (_, command) =>
        {
            if (_macro is not null && !s_notRecorded.Contains(command.Name))
            {
                _macro.Add(string.IsNullOrWhiteSpace(command.Parameter) ? command.Name : command.Name + " " + command.Parameter);
            }
        };
    }

    private async Task ToggleMacroAsync()
    {
        if (_macro is null)
        {
            _macro = [];
            OnPropertyChanged(nameof(IsRecordingMacro));
            return;
        }
        var recorded = _macro;
        _macro = null;
        OnPropertyChanged(nameof(IsRecordingMacro));
        await SaveMacroAsync(recorded);
    }

    /// <summary>The recorded commands as a user command under a name the user gives.</summary>
    internal async Task SaveMacroAsync(IReadOnlyList<string> recorded)
    {
        // a parameter with a comma would split the chain of commands: such a step cannot be part of it
        var steps = recorded.Where(s => !s.Contains(',', StringComparison.Ordinal)).ToList();
        var left = recorded.Count - steps.Count;
        if (steps.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Record macro", "No command was run while recording: nothing to save. Menus, keys, buttons and the command line run commands; moving the cursor does not.");
            return;
        }
        var shown = string.Join("\n", steps.Take(12)) + (steps.Count > 12 ? "\n..." : string.Empty);
        var name = await _dialogs.PromptAsync("Record macro", $"{steps.Count} commands:\n{shown}\n\nName of the user command (uc_...):", "Macro" + (_settings.UserCommands.Count + 1));
        while (name is not null && MacroNameProblem(name) is { } problem)
        {
            name = await _dialogs.PromptAsync("Record macro", $"{problem}\n\nName of the user command (uc_...):", name);
        }
        if (name is null)
        {
            return;
        }
        var command = "uc_" + StripUserPrefix(name.Trim());
        if (_settings.UserCommands.ContainsKey(command)
            && !await _dialogs.ConfirmAsync("Record macro", $"{command} exists. Replace it?", "Replace"))
        {
            return;
        }
        var definition = new ButtonDefinition
        {
            Command = string.Join(", ", steps),
            Label = StripUserPrefix(name.Trim()),
            Tooltip = $"Macro: {steps.Count} commands",
            IsMacro = true,
        };
        _settings.UserCommands[command] = definition;
        Commands.Register(command, definition.Tooltip, extra => ExecuteButtonAsync(definition, extra));
        SaveSettings();
        await _dialogs.ShowMessageAsync("Record macro", left > 0
            ? $"Saved as {command}. Configuration > Commands and shortcuts gives it a key; the button bar's editor a button.\n\n{left} commands with a comma in their parameter could not be kept."
            : $"Saved as {command}. Configuration > Commands and shortcuts gives it a key; the button bar's editor a button.");
    }

    /// <summary>
    /// The recorded macros (user's request, 25 November 2026): their commands (uc_...) by name with their steps. Macros
    /// recorded before <see cref="ButtonDefinition.IsMacro"/> are known by their tooltip.
    /// </summary>
    public IReadOnlyList<(string Command, ButtonDefinition Definition)> Macros() =>
        [.. _settings.UserCommands
            .Where(p => p.Value.IsMacro || (p.Value.Tooltip?.StartsWith("Macro: ", StringComparison.Ordinal) == true && p.Value.Command.StartsWith("nc_", StringComparison.OrdinalIgnoreCase)))
            .Select(p => (Command: p.Key.StartsWith("uc_", StringComparison.OrdinalIgnoreCase) ? p.Key : "uc_" + p.Key, Definition: p.Value))
            .OrderBy(p => p.Command, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Tools > Macros > Macros...: the macros window (the user's template, 25 November 2026): run or delete one.</summary>
    private async Task ShowMacrosAsync()
    {
        var macros = Macros().Select(m => new Views.Dialogs.MacroInfo(m.Definition.Label ?? StripUserPrefix(m.Command), m.Command,
            [.. CommandRegistry.Parse(m.Definition.Command).Select(s => s.Parameter is null ? s.Name : s.Name + " " + s.Parameter)])).ToList();
        await _dialogs.ShowMacrosAsync(new Views.Dialogs.MacrosRequest(macros, command => ExecuteAsync(command), RemoveMacro));
    }

    /// <summary>nc_DeleteMacro uc_...: deletes a macro after asking, with its keys and its buttons on the button bars.</summary>
    internal async Task DeleteMacroAsync(string? command)
    {
        var macro = Macros().FirstOrDefault(m => string.Equals(m.Command, command?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (macro.Definition is null)
        {
            await _dialogs.ShowMessageAsync("Macros", $"There is no macro named {command}.");
            return;
        }
        var name = macro.Definition.Label ?? StripUserPrefix(macro.Command);
        if (await _dialogs.ConfirmAsync("Macros", $"Delete the macro \"{name}\" ({macro.Command})? Its keys and buttons go with it.", "Delete"))
        {
            RemoveMacro(macro.Command);
        }
    }

    /// <summary>The macro's user command, its keys and its buttons go (asked already).</summary>
    private void RemoveMacro(string command)
    {
        var macro = Macros().FirstOrDefault(m => string.Equals(m.Command, command.Trim(), StringComparison.OrdinalIgnoreCase));
        if (macro.Definition is null)
        {
            return;
        }
        foreach (var key in _settings.UserCommands.Keys.Where(k => string.Equals(k.StartsWith("uc_", StringComparison.OrdinalIgnoreCase) ? k : "uc_" + k, macro.Command, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _settings.UserCommands.Remove(key);
        }
        Commands.Unregister(macro.Command);
        foreach (var key in _settings.Hotkeys.Where(h => string.Equals(h.Value.Trim(), macro.Command, StringComparison.OrdinalIgnoreCase)).Select(h => h.Key).ToList())
        {
            _settings.Hotkeys.Remove(key);
        }
        static bool Drop(List<ButtonDefinition> bar, string command)
        {
            var removed = bar.RemoveAll(b => string.Equals(b.Command.Trim(), command, StringComparison.OrdinalIgnoreCase)) > 0;
            foreach (var menu in bar.Where(b => b.Menu is not null))
            {
                removed |= Drop(menu.Menu!, command);
            }
            return removed;
        }
        if (Drop(_settings.ButtonBar, macro.Command) | Drop(_settings.VerticalButtonBar, macro.Command))
        {
            ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
        }
        Hotkeys.Rebuild();
        SaveSettings();
    }

    private static string StripUserPrefix(string name) => name.StartsWith("uc_", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;

    /// <summary>What is wrong with a macro's name: it becomes a command name (letters, digits, _ and -).</summary>
    internal static string? MacroNameProblem(string name)
    {
        var bare = StripUserPrefix(name.Trim());
        return bare.Length == 0 ? "The name is empty."
            : bare.All(c => char.IsLetterOrDigit(c) || c is '_' or '-') ? null
            : "A command name has only letters, digits, _ and -.";
    }
}
