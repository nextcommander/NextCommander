using NextCommander.App.Services;
using NextCommander.Core.Automation;
using NextCommander.Core.Commands;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>
/// Rules (Tools > Rules; user's request, 23 November 2026): files arriving in a folder moved or copied elsewhere,
/// folders synchronized every night, while the program runs; the work goes through the job queue (background transfers)
/// and every step is written to logs/rules.log.
/// </summary>
public partial class MainWindowViewModel
{
    private RuleRunner? _rules;

    /// <summary>The rules' log (logs/rules.log in the settings folder).</summary>
    public RuleLog RuleLog => _ruleLog ??= new RuleLog(Path.Combine(_store.Directory, "logs", "rules.log"));

    private RuleLog? _ruleLog;

    /// <summary>The running rules (null until <see cref="StartRules"/>).</summary>
    internal RuleRunner? Rules => _rules;

    private void RegisterRuleCommands(CommandRegistry c)
    {
        c.Register("nc_Rules", "Rules: files arriving in a folder moved or copied, folders synchronized every night", _ => ShowSettingsAsync("Rules"));
        c.Register("nc_RunRules", "Carry out the rules now (synchronizations run, waiting files are taken)", _ => RunRulesNowAsync());
        c.Register("nc_RuleLog", "Show what the rules did (their log)", _ => ShowRuleLogAsync());
    }

    /// <summary>Starts (or restarts after the settings) the enabled rules; at the start of the program and after the configuration.</summary>
    public void StartRules()
    {
        if (_rules is null)
        {
            if (_settings.Rules.Count == 0)
            {
                return; // nothing to watch: no watcher, no timer
            }
            _rules = new RuleRunner(_registry, job => _jobs.RunAsync(job), RuleLog);
            _rules.RuleRan += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(SaveSettings);
        }
        _rules.Apply([.. _settings.Rules]);
    }

    private async Task RunRulesNowAsync()
    {
        var rules = _settings.Rules.Where(r => r.Enabled && r.Problem() is null).ToList();
        if (rules.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Rules", "There is no rule to carry out. Tools > Rules > Rules... adds one.");
            return;
        }
        StartRules();
        foreach (var rule in rules)
        {
            await _rules!.RunAsync(rule);
        }
        SaveSettings();
        await ShowRuleLogAsync();
    }

    private async Task ShowRuleLogAsync()
    {
        if (!File.Exists(RuleLog.Path))
        {
            await _dialogs.ShowMessageAsync("Rules", "The rules have done nothing yet.");
            return;
        }
        var path = VfsPath.Local(RuleLog.Path);
        if (await _registry.Resolve(path).GetEntryAsync(path, CancellationToken.None) is { } entry)
        {
            _dialogs.ShowLister(new ListerRequest([entry], 0, Tools));
        }
    }

    private void StopRules()
    {
        _rules?.Dispose();
        _rules = null;
    }
}
