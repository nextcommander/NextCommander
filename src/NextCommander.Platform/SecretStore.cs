using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NextCommander.Platform;

/// <summary>
/// Encrypted passwords (FTP / SFTP / plugins) in <c>secrets.json</c> next to the settings. Every secret is AES-256-GCM
/// encrypted. The key comes from the master password (PBKDF2-SHA256) when one is set; otherwise it is a random key
/// protected by the operating system (DPAPI on Windows, a file only the user can read elsewhere).
/// </summary>
public sealed class SecretStore
{
    private const int Iterations = 600_000;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private readonly string _file;
    private readonly string _keyFile;
    private readonly object _lock = new();
    private SecretFile _data;
    private byte[]? _key;

    public SecretStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "secrets.json");
        _keyFile = Path.Combine(directory, "secrets.key");
        _data = Load();
        if (!_data.UsesMasterPassword)
        {
            _key = LoadOrCreateMachineKey();
        }
    }

    /// <summary>Asks for the master password: (wrong last time?) → password or null to cancel.</summary>
    public Func<bool, Task<string?>>? MasterPasswordPrompt { get; set; }

    public bool UsesMasterPassword => _data.UsesMasterPassword;

    /// <summary>A master password is set and not entered yet (secrets cannot be read).</summary>
    public bool IsLocked => _key is null;

    public bool Contains(string id)
    {
        lock (_lock)
        {
            return _data.Secrets.ContainsKey(id);
        }
    }

    /// <summary>Unlocks with the master password; false when it is wrong.</summary>
    public bool Unlock(string masterPassword)
    {
        lock (_lock)
        {
            var key = DeriveKey(masterPassword, Convert.FromBase64String(_data.Salt!));
            if (!Decrypt(key, _data.Check!, out var check) || check != "ok")
            {
                return false;
            }
            _key = key;
            return true;
        }
    }

    /// <summary>Forgets the key (e.g. when minimized); the master password is asked again.</summary>
    public void Lock()
    {
        lock (_lock)
        {
            if (_data.UsesMasterPassword)
            {
                _key = null;
            }
        }
    }

    /// <summary>Unlocks through <see cref="MasterPasswordPrompt"/> when needed; false when cancelled.</summary>
    public async Task<bool> EnsureUnlockedAsync()
    {
        var retry = false;
        while (IsLocked)
        {
            var password = MasterPasswordPrompt is null ? null : await MasterPasswordPrompt(retry).ConfigureAwait(false);
            if (password is null)
            {
                return false;
            }
            retry = !Unlock(password);
        }
        return true;
    }

    public async Task<string?> GetAsync(string id)
    {
        lock (_lock)
        {
            if (!_data.Secrets.ContainsKey(id))
            {
                return null;
            }
        }
        if (!await EnsureUnlockedAsync().ConfigureAwait(false))
        {
            return null;
        }
        lock (_lock)
        {
            return _data.Secrets.TryGetValue(id, out var blob) && Decrypt(_key!, blob, out var value) ? value : null;
        }
    }

    public async Task<bool> SetAsync(string id, string secret)
    {
        if (!await EnsureUnlockedAsync().ConfigureAwait(false))
        {
            return false;
        }
        lock (_lock)
        {
            _data.Secrets[id] = Encrypt(_key!, secret);
            Save();
        }
        return true;
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_data.Secrets.Remove(id))
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Sets, changes (<paramref name="newPassword"/> not empty) or removes (empty) the master password; every secret is
    /// re-encrypted. The store must be unlocked.
    /// </summary>
    public void SetMasterPassword(string? newPassword)
    {
        lock (_lock)
        {
            if (_key is null)
            {
                throw new InvalidOperationException("The current master password must be entered first.");
            }
            var plain = _data.Secrets.ToDictionary(p => p.Key, p => Decrypt(_key, p.Value, out var v) ? v : null);
            byte[] key;
            if (string.IsNullOrEmpty(newPassword))
            {
                key = LoadOrCreateMachineKey();
                _data.Salt = null;
                _data.Check = null;
            }
            else
            {
                var salt = RandomNumberGenerator.GetBytes(16);
                key = DeriveKey(newPassword, salt);
                _data.Salt = Convert.ToBase64String(salt);
                _data.Check = Encrypt(key, "ok");
            }
            _data.Secrets = plain.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => Encrypt(key, p.Value!), StringComparer.Ordinal);
            _key = key;
            Save();
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static string Encrypt(byte[] key, string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private static bool Decrypt(byte[] key, string blob, out string plain)
    {
        plain = string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(blob);
            if (bytes.Length < 28)
            {
                return false;
            }
            var data = new byte[bytes.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), data);
            plain = Encoding.UTF8.GetString(data);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Random 256-bit key, protected by DPAPI (Windows) or by file permissions (Linux / macOS).</summary>
    private byte[] LoadOrCreateMachineKey()
    {
        if (File.Exists(_keyFile))
        {
            var stored = File.ReadAllBytes(_keyFile);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    return ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException)
                {
                    // Another user / machine (portable mode): the saved passwords cannot be read; start a new key.
                }
            }
            else if (stored.Length == 32)
            {
                return stored;
            }
        }
        var key = RandomNumberGenerator.GetBytes(32);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(_keyFile, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        }
        else
        {
            File.WriteAllBytes(_keyFile, key);
            File.SetUnixFileMode(_keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return key;
    }

    private SecretFile Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var data = JsonSerializer.Deserialize<SecretFile>(File.ReadAllText(_file));
                if (data is not null)
                {
                    data.Secrets = new Dictionary<string, string>(data.Secrets, StringComparer.Ordinal);
                    return data;
                }
            }
        }
        catch (JsonException)
        {
            File.Copy(_file, _file + ".bad", overwrite: true);
        }
        return new SecretFile();
    }

    private void Save()
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, s_json));
        File.Move(temp, _file, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class SecretFile
    {
        /// <summary>PBKDF2 salt when a master password is used.</summary>
        public string? Salt { get; set; }

        /// <summary>"ok" encrypted with the master key: tells a wrong master password apart.</summary>
        public string? Check { get; set; }

        public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);

        [System.Text.Json.Serialization.JsonIgnore]
        public bool UsesMasterPassword => Salt is not null;
    }
}
