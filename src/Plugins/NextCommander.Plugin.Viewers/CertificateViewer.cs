using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// Certificates (.cer, .crt, .der, .pem with one or more, .p7b / .p7c, and .pfx / .p12 without a password): for each
/// one its subject, issuer, validity (and whether it is valid now), serial number, key, signature algorithm,
/// fingerprints, uses and alternative names, from the leaf up to the root. Nothing is installed or trusted.
/// </summary>
public sealed class CertificateViewer : IViewerPlugin
{
    private static readonly string[] s_extensions = [".cer", ".crt", ".der", ".pem", ".p7b", ".p7c", ".pfx", ".p12"];

    public string Name => "Certificate";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) =>
        s_extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        var bytes = await ViewerHtml.ReadAllAsync(content, 20L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var (certificates, problem) = Load(bytes, Path.GetExtension(fileName));
        var page = new ViewerHtml(fileName, null, dark);
        if (problem is not null)
        {
            page.Note(problem);
        }
        var chain = Pkcs7.Chain(certificates);
        foreach (var certificate in chain)
        {
            Describe(page, certificate);
        }
        var status = ViewerHtml.T($"Certificates: {chain.Count}");
        foreach (var certificate in certificates)
        {
            certificate.Dispose();
        }
        return new ViewerContent { Html = page.ToString(), Status = status };
    }

    /// <summary>The certificates of a file, or why there are none.</summary>
    internal static (List<X509Certificate2> Certificates, string? Problem) Load(byte[] bytes, string extension)
    {
        try
        {
            if (extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return ([.. X509CertificateLoader.LoadPkcs12Collection(bytes, null, X509KeyStorageFlags.EphemeralKeySet)], null);
                }
                catch (CryptographicException)
                {
                    return ([], "The file is protected by a password: its certificates cannot be shown.");
                }
            }
            var text = Encoding.ASCII.GetString(bytes);
            if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                var collection = new X509Certificate2Collection();
                collection.ImportFromPem(text);
                return ([.. collection], null);
            }
            if (text.Contains("-----BEGIN PKCS7-----", StringComparison.Ordinal))
            {
                var base64 = text.Split("-----BEGIN PKCS7-----")[1].Split("-----END PKCS7-----")[0];
                return (Pkcs7.Certificates(Convert.FromBase64String(base64)), null);
            }
            if (Pkcs7.Certificates(bytes) is { Count: > 0 } signed)
            {
                return (signed, null);
            }
            return ([X509CertificateLoader.LoadCertificate(bytes)], null);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return ([], "The file is not a certificate this view can read.");
        }
    }

    private static void Describe(ViewerHtml page, X509Certificate2 certificate)
    {
        var now = DateTime.Now;
        var valid = now >= certificate.NotBefore && now <= certificate.NotAfter;
        var state = now < certificate.NotBefore ? ViewerHtml.T("Not valid yet") : now > certificate.NotAfter ? ViewerHtml.T("Expired") : ViewerHtml.T("Valid");
        var selfSigned = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
        // (the name is the certificate's: a heading of its own, not translated)
        page.Raw("<h2>").Raw(ViewerHtml.E(certificate.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } name ? name : certificate.Subject)).Raw("</h2>");
        page.Raw(valid ? "<p class=\"ok\">" : "<p class=\"bad\">").Raw(ViewerHtml.E(state)).Raw("</p>");
        page.Facts(
        [
            ("Subject", certificate.Subject),
            ("Issuer", selfSigned ? ViewerHtml.T("Self-signed") : certificate.Issuer),
            ("Valid from", certificate.NotBefore.ToString("f", CultureInfo.CurrentCulture)),
            ("Valid until", certificate.NotAfter.ToString("f", CultureInfo.CurrentCulture)),
            ("Serial number", certificate.SerialNumber),
            ("Public key", KeyOf(certificate)),
            ("Signature algorithm", certificate.SignatureAlgorithm.FriendlyName ?? certificate.SignatureAlgorithm.Value),
            ("Certificate authority", Extension<X509BasicConstraintsExtension>(certificate) is { CertificateAuthority: true } ? ViewerHtml.T("Yes") : null),
            ("Key usage", Extension<X509KeyUsageExtension>(certificate)?.KeyUsages.ToString()),
            ("Intended purposes", Extension<X509EnhancedKeyUsageExtension>(certificate) is { } eku
                ? string.Join(", ", eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.FriendlyName ?? o.Value)) : null),
            ("Alternative names", Extension<X509SubjectAlternativeNameExtension>(certificate) is { } san
                ? string.Join(", ", san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(a => a.ToString()))) : null),
            ("SHA-256 fingerprint", Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))),
            ("SHA-1 fingerprint", certificate.Thumbprint),
            ("Private key", certificate.HasPrivateKey ? ViewerHtml.T("In the file") : null),
        ]);
    }

    private static T? Extension<T>(X509Certificate2 certificate) where T : X509Extension =>
        certificate.Extensions.OfType<T>().FirstOrDefault();

    private static string KeyOf(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            return $"RSA {rsa.KeySize} bit";
        }
        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is not null)
        {
            return $"ECDSA {ecdsa.KeySize} bit";
        }
        return certificate.PublicKey.Oid.FriendlyName ?? certificate.PublicKey.Oid.Value ?? string.Empty;
    }
}
