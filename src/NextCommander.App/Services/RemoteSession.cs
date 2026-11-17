using System.Collections.Concurrent;
using Avalonia.Threading;
using NextCommander.Core.Config;
using NextCommander.Platform;
using NextCommander.Vfs.Remote;

namespace NextCommander.App.Services;

/// <summary>
/// Connects the FTP / SFTP providers to the application: saved connections, passwords from the encrypted store (asked
/// and optionally saved otherwise), trusted server fingerprints in the settings. Questions run on the UI thread.
/// </summary>
public sealed class RemoteSession
{
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly ConcurrentDictionary<string, string> _saveAfterLogin = new(StringComparer.Ordinal);

    public RemoteSession(AppSettings settings, SecretStore secrets, IDialogService dialogs, IPlatformService? platform = null)
    {
        _settings = settings;
        Secrets = secrets;
        _dialogs = dialogs;
        secrets.MasterPasswordPrompt = retry => OnUiAsync(() => dialogs.PromptPasswordAsync("Master password",
            retry ? "Wrong master password. Enter it again:" : "Master password to unlock saved passwords:"));
        Hooks = new RemoteHooks
        {
            ResolveConnection = Resolve,
            GetPassword = GetPasswordAsync,
            LoginSucceeded = LoginSucceededAsync,
            KnownFingerprint = c => settings.KnownHosts.GetValueOrDefault(HostKey(c)),
            ConfirmFingerprint = (c, fingerprint, description, changed) => OnUiAsync(async () =>
            {
                var trusted = await dialogs.ConfirmFingerprintAsync($"{c.Host}:{c.EffectivePort}", fingerprint, description, changed);
                if (trusted)
                {
                    settings.KnownHosts[HostKey(c)] = fingerprint;
                }
                return trusted;
            }),
            // cloud drives: the refresh token is the saved "password" of the connection
            LoadToken = async c => c.SavePassword && Secrets.Contains(c.SecretId) ? await Secrets.GetAsync(c.SecretId) : null,
            SaveToken = async (c, token) =>
            {
                if (token is null)
                {
                    Secrets.Remove(c.SecretId);
                }
                else if (c.SavePassword)
                {
                    await Secrets.SetAsync(c.SecretId, token);
                }
            },
            OpenBrowser = uri =>
            {
                if (platform is null)
                {
                    throw new IOException("Cannot open the browser to sign in.");
                }
                Serilog.Log.Information("Opening cloud sign-in in the browser: {Host}", uri.Host);
                platform.OpenWithDefaultApp(uri.AbsoluteUri);
                return Task.CompletedTask;
            },
        };
        // the connections' own proxies: their passwords from the password store; an SSH proxy checks its key with these hooks
        NextCommander.Core.Net.WebProxyConfig.LoadPassword = async id => Secrets.Contains(id) ? await Secrets.GetAsync(id) : null;
        NextCommander.Vfs.Remote.SshProxy.Hooks = Hooks;
    }

    public SecretStore Secrets { get; }

    public RemoteHooks Hooks { get; }

    public static string HostKey(RemoteConnection c) => $"{c.Scheme}:{c.Host}:{c.EffectivePort}";

    private RemoteConnection Resolve(string scheme, string authority) =>
        _settings.Connections.FirstOrDefault(c => (c.Scheme == scheme || (scheme == "ftps" && c.Scheme == "ftps")) && c.Authority == authority)?.Clone()
        ?? RemoteConnection.FromAuthority(scheme, authority);

    private async Task<string?> GetPasswordAsync(RemoteConnection connection, bool retry)
    {
        if (!retry && Secrets.Contains(connection.SecretId) && await Secrets.GetAsync(connection.SecretId) is { } saved)
        {
            return saved;
        }
        var answer = await OnUiAsync(() => _dialogs.PromptLoginAsync(connection, retry));
        if (answer is not { } a)
        {
            return null;
        }
        if (a.Save)
        {
            _saveAfterLogin[connection.SecretId] = a.Password;
        }
        else
        {
            _saveAfterLogin.TryRemove(connection.SecretId, out _);
            if (retry)
            {
                Secrets.Remove(connection.SecretId); // the saved one was wrong
            }
        }
        return a.Password;
    }

    /// <summary>Stores the password only after it worked.</summary>
    private async Task LoginSucceededAsync(RemoteConnection connection, string? password)
    {
        if (password is not null && _saveAfterLogin.TryRemove(connection.SecretId, out var pending) && pending == password)
        {
            await Secrets.SetAsync(connection.SecretId, password);
        }
    }

    private static Task<T> OnUiAsync<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);
}
