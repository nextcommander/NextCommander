using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NextCommander.Plugin.Viewers;

/// <summary>The certificates inside a PKCS #7 SignedData (a .p7b file, a program's Authenticode signature), read with the ASN.1 reader.</summary>
internal static class Pkcs7
{
    private const string SignedDataOid = "1.2.840.113549.1.7.2";

    /// <summary>The certificates of a SignedData (empty when there are none or it is not one).</summary>
    public static List<X509Certificate2> Certificates(byte[] der)
    {
        var certificates = new List<X509Certificate2>();
        try
        {
            var contentInfo = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            if (contentInfo.ReadObjectIdentifier() != SignedDataOid)
            {
                return certificates;
            }
            var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)).ReadSequence();
            signedData.ReadInteger(); // version
            signedData.ReadSetOf(skipSortOrderValidation: true); // digest algorithms
            signedData.ReadSequence(); // the signed content
            var certificatesTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(certificatesTag))
            {
                var set = signedData.ReadSetOf(skipSortOrderValidation: true, certificatesTag);
                while (set.HasData)
                {
                    var encoded = set.ReadEncodedValue();
                    try
                    {
                        certificates.Add(X509CertificateLoader.LoadCertificate(encoded.Span));
                    }
                    catch (CryptographicException)
                    {
                        // an attribute certificate or one it cannot read: left out
                    }
                }
            }
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException)
        {
        }
        return certificates;
    }

    /// <summary>The certificates from the leaf (the one no other was issued by) up to the root, as far as the set has them.</summary>
    public static List<X509Certificate2> Chain(IReadOnlyList<X509Certificate2> certificates)
    {
        if (certificates.Count <= 1)
        {
            return [.. certificates];
        }
        // the leaf issued no other certificate of the set
        bool IssuedAnother(X509Certificate2 c) =>
            certificates.Any(o => !ReferenceEquals(o, c) && o.IssuerName.RawData.AsSpan().SequenceEqual(c.SubjectName.RawData));
        var leaf = certificates.FirstOrDefault(c => !IssuedAnother(c)) ?? certificates[0];
        var chain = new List<X509Certificate2> { leaf };
        while (chain.Count < certificates.Count)
        {
            var last = chain[^1];
            if (last.SubjectName.RawData.AsSpan().SequenceEqual(last.IssuerName.RawData))
            {
                break; // self-signed: the root
            }
            var issuer = certificates.FirstOrDefault(c => !chain.Contains(c) && c.SubjectName.RawData.AsSpan().SequenceEqual(last.IssuerName.RawData));
            if (issuer is null)
            {
                break;
            }
            chain.Add(issuer);
        }
        chain.AddRange(certificates.Where(c => !chain.Contains(c)));
        return chain;
    }
}
