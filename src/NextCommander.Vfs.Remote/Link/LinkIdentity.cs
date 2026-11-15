using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NextCommander.Vfs.Remote.Link;

/// <summary>
/// The PC link identity of this installation: a self-signed certificate (ECDSA P-256) both for sharing (TLS server) and
/// for connecting (TLS client). The other computer remembers its fingerprint after the pairing code was compared once.
/// It is kept as a PKCS#12 blob in the encrypted secret store of the settings folder.
/// </summary>
public static class LinkIdentity
{
    /// <summary>The id of the identity in the secret store.</summary>
    public const string SecretId = "link:identity";

    /// <summary>A new identity for a computer name.</summary>
    public static X509Certificate2 Create(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={LinkProtocol.SafeName(name)}, O=NextCommander PC link", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false)); // server and client authentication
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));
        // through PKCS#12 once: a key that TLS on Windows (SChannel) can use (not an ephemeral one)
        return Load(created.Export(X509ContentType.Pkcs12));
    }

    /// <summary>The identity as text for the secret store (base64 of the PKCS#12 blob without a password).</summary>
    public static string Export(X509Certificate2 identity) => Convert.ToBase64String(identity.Export(X509ContentType.Pkcs12));

    /// <summary>The identity of <see cref="Export"/>.</summary>
    public static X509Certificate2 Import(string text) => Load(Convert.FromBase64String(text));

    private static X509Certificate2 Load(byte[] pkcs12) =>
        X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.Exportable | (OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.UserKeySet));

    /// <summary>
    /// The stored identity, or a new one that is stored at once. <paramref name="read"/> / <paramref name="write"/>: the
    /// secret store (null from <paramref name="read"/>: none yet).
    /// </summary>
    public static async Task<X509Certificate2> LoadOrCreateAsync(Func<Task<string?>> read, Func<string, Task> write, string name)
    {
        if (await read().ConfigureAwait(false) is { Length: > 0 } stored)
        {
            try
            {
                return Import(stored);
            }
            catch (CryptographicException)
            {
                // damaged: a new identity (the other computers ask for the pairing code again)
            }
        }
        var identity = Create(name);
        await write(Export(identity)).ConfigureAwait(false);
        return identity;
    }
}
