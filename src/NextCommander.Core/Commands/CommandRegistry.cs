namespace NextCommander.Core.Commands;

public sealed record CommandDefinition(string Name, string Description, Func<string?, Task> Handler);

/// <summary>
/// Named commands (<c>nc_*</c> built in, <c>uc_*</c> the user's). Hotkeys, menus, button bars and the command line all
/// execute commands through this registry; names of earlier versions (<see cref="LegacyNames"/>) still run. Several commands can be chained with ',':
/// <c>nc_GoToRoot,nc_SrcByName</c>; text after the first space is the parameter: <c>nc_SortByCol 2</c>.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How deep the commands being run are nested (a user command runs others): only the outer ones are told.</summary>
    private static readonly AsyncLocal<int> s_depth = new();

    /// <summary>
    /// A command is about to run that no other command started (a menu, a key, a button, the command line): its name
    /// and parameter. The macro recorder listens.
    /// </summary>
    public event EventHandler<(string Name, string? Parameter)>? Executing;

    public IReadOnlyCollection<CommandDefinition> All => _commands.Values;

    public void Register(string name, string description, Func<string?, Task> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _commands[name] = new CommandDefinition(name, description, handler);
    }

    public void Register(string name, string description, Action handler) =>
        Register(name, description, _ =>
        {
            handler();
            return Task.CompletedTask;
        });

    /// <summary>Takes a command away (a deleted macro or user command); false when there was none.</summary>
    public bool Unregister(string name) => _commands.Remove(LegacyNames.Command(name));

    public bool Contains(string name) => _commands.ContainsKey(LegacyNames.Command(name));

    public bool TryGet(string name, out CommandDefinition? command) => _commands.TryGetValue(LegacyNames.Command(name), out command);

    /// <summary>Executes one or more comma-separated commands. Returns false if any name is unknown.</summary>
    public async Task<bool> ExecuteAsync(string commandLine)
    {
        var allKnown = true;
        foreach (var (name, parameter) in Parse(commandLine))
        {
            if (_commands.TryGetValue(LegacyNames.Command(name), out var command))
            {
                if (s_depth.Value == 0)
                {
                    Executing?.Invoke(this, (command.Name, parameter));
                }
                s_depth.Value++;
                try
                {
                    await command.Handler(parameter).ConfigureAwait(true);
                }
                finally
                {
                    s_depth.Value--;
                }
            }
            else
            {
                allKnown = false;
            }
        }
        return allKnown;
    }

    /// <summary>True if the text starts with a registered command name (used by the command line).</summary>
    public bool IsCommand(string text)
    {
        var first = Parse(text).FirstOrDefault();
        return first.Name is not null && _commands.ContainsKey(LegacyNames.Command(first.Name));
    }

    public static IEnumerable<(string Name, string? Parameter)> Parse(string commandLine)
    {
        foreach (var part in commandLine.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = part.IndexOf(' ', StringComparison.Ordinal);
            yield return space < 0 ? (part, null) : (part[..space], part[(space + 1)..].Trim());
        }
    }
}
