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

    public static string HostKey(RemoteConnection c) => $"{(c.Protocol == RemoteProtocol.Ssh ? "sftp" : c.Scheme)}:{c.Host}:{c.EffectivePort}"; // an SSH session trusts the key its server has for SFTP

    private RemoteConnection Resolve(string scheme, string authority) =>
        _settings.Connections.FirstOrDefault(c => (c.Scheme == scheme || (scheme == "ftps" && c.Scheme == "ftps")) && c.Authority == authority)?.Clone()
        ?? (_remembered.TryGetValue(scheme + "://" + authority, out var remembered) ? remembered.Clone() : null)
        ?? RemoteConnection.FromAuthority(scheme, authority);

    private readonly ConcurrentDictionary<string, RemoteConnection> _remembered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _passwordOf = new(StringComparer.Ordinal);

    /// <summary>
    /// A connection that is not saved (the files of an SSH session, a remote desktop's SMB share): opened by its address,
    /// it keeps its login settings; <paramref name="passwordFrom"/> is the secret whose saved password it may use (the
    /// SSH session's).
    /// </summary>
    public void Remember(RemoteConnection connection, string? passwordFrom = null)
    {
        _remembered[connection.SecretId] = connection.Clone();
        if (passwordFrom is not null && passwordFrom != connection.SecretId)
        {
            _passwordOf[connection.SecretId] = passwordFrom;
        }
    }

    private async Task<string?> GetPasswordAsync(RemoteConnection connection, bool retry)
    {
        var secret = connection.PasswordSecretId; // a shared login keeps one password for all its connections
        if (!retry && Secrets.Contains(secret) && await Secrets.GetAsync(secret) is { } saved)
        {
            return saved;
        }
        if (!retry && _passwordOf.TryGetValue(connection.SecretId, out var other) && Secrets.Contains(other) && await Secrets.GetAsync(other) is { } shared)
        {
            return shared; // the SSH session's saved password for its files
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
                Secrets.Remove(secret); // the saved one was wrong
            }
        }
        return a.Password;
    }

    /// <summary>Stores the password only after it worked.</summary>
    private async Task LoginSucceededAsync(RemoteConnection connection, string? password)
    {
        if (password is not null && _saveAfterLogin.TryRemove(connection.SecretId, out var pending) && pending == password)
        {
            await Secrets.SetAsync(connection.PasswordSecretId, password);
        }
    }

    private static Task<T> OnUiAsync<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);
}
