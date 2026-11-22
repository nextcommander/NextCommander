using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography.X509Certificates;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// Programs and libraries (.exe, .dll, .sys, .ocx, .scr, .cpl, .efi): the version information (product, company,
/// description, versions, copyright), the architecture, kind (window / console / driver; .NET), when it was built, its
/// sections, and the certificates of its Authenticode signature (who signed it; the signature is not checked here).
/// </summary>
public sealed class ExecutableViewer : IViewerPlugin
{
    private static readonly string[] s_extensions = [".exe", ".dll", ".sys", ".ocx", ".scr", ".cpl", ".efi", ".mui"];

    public string Name => "Program";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) =>
        s_extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase) && header.Length >= 2 && header[0] == 'M' && header[1] == 'Z';

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        // the version information is read from a file (a temporary copy when the program is elsewhere)
        string? temp = null;
        var path = content is FileStream { Name: { } name } && File.Exists(name) ? name : null;
        try
        {
            if (path is null)
            {
                temp = Path.Combine(Path.GetTempPath(), "nc-exe-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
                await using (var copy = File.Create(temp))
                {
                    await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                }
                path = temp;
            }
            var html = await Task.Run(() => Describe(path, fileName, dark), cancellationToken).ConfigureAwait(false);
            return new ViewerContent { Html = html.Html, Status = html.Status };
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    internal static (string Html, string Status) Describe(string path, string fileName, bool dark)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        var page = new ViewerHtml(version.FileDescription is { Length: > 0 } description ? description : fileName,
            version.ProductName is { Length: > 0 } product && product != version.FileDescription ? product : null, dark);
        page.Facts(
        [
            ("Company", version.CompanyName),
            ("Product", version.ProductName),
            ("Product version", version.ProductVersion),
            ("File version", version.FileVersion),
            ("Original file name", version.OriginalFilename),
            ("Copyright", version.LegalCopyright),
            ("Comments", version.Comments),
        ]);

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var headers = pe.PEHeaders;
        var coff = headers.CoffHeader;
        var optional = headers.PEHeader;
        var architecture = coff.Machine switch
        {
            Machine.Amd64 => "x64",
            Machine.I386 => "x86",
            Machine.Arm64 => "ARM64",
            Machine.Arm or Machine.ArmThumb2 => "ARM",
            Machine.IA64 => "Itanium",
            _ => coff.Machine.ToString(),
        };
        var kind = optional?.Subsystem switch
        {
            Subsystem.WindowsGui => ViewerHtml.T("Windows program"),
            Subsystem.WindowsCui => ViewerHtml.T("Console program"),
            Subsystem.Native => ViewerHtml.T("Driver"),
            Subsystem.EfiApplication or Subsystem.EfiBootServiceDriver or Subsystem.EfiRuntimeDriver => "EFI",
            _ => optional?.Subsystem.ToString(),
        };
        if (coff.Characteristics.HasFlag(Characteristics.Dll))
        {
            kind = ViewerHtml.T("Library (DLL)");
        }
        string? dotnet = null;
        if (headers.CorHeader is not null && pe.HasMetadata)
        {
            var metadata = pe.GetMetadataReader();
            var assembly = metadata.IsAssembly ? metadata.GetAssemblyDefinition() : (AssemblyDefinition?)null;
            dotnet = assembly is { } a ? $"{metadata.GetString(a.Name)} {a.Version} ({metadata.MetadataVersion})" : metadata.MetadataVersion;
        }
        // a build time with the high bit set is a hash of a reproducible build, not a time
        var stamp = coff.TimeDateStamp;
        var built = stamp > 0 && stamp < 0x7FFFFFFF ? DateTimeOffset.FromUnixTimeSeconds(stamp).LocalDateTime.ToString("f", CultureInfo.CurrentCulture) : null;
        page.Heading("Program").Facts(
        [
            ("Kind", kind),
            ("Architecture", architecture + (optional?.Magic == PEMagic.PE32Plus ? " (64-bit)" : " (32-bit)")),
            (".NET", dotnet),
            ("Built", built),
            ("Size", ViewerHtml.Size(stream.Length)),
        ]);
        page.Heading("Sections").Table(["Name", "Size", "Readable", "Writable", "Executable"], headers.SectionHeaders.Select(s => (IReadOnlyList<string?>)
        [
            s.Name,
            ViewerHtml.Size(s.VirtualSize),
            s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemRead) ? "✓" : string.Empty,
            s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite) ? "✓" : string.Empty,
            s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute) ? "✓" : string.Empty,
        ]));

        // the Authenticode signature: a PKCS #7 at a file offset (not an address) in the certificate table
        var signers = optional?.CertificateTableDirectory is { Size: > 8 } table ? SignatureCertificates(stream, table.RelativeVirtualAddress, table.Size) : [];
        page.Heading("Signature");
        if (signers.Count == 0)
        {
            page.Note("The program has no signature of its own (Windows' own files are often signed in a catalog of the system instead).");
        }
        else
        {
            var chain = Pkcs7.Chain(signers);
            page.Facts([("Signed by", chain[0].GetNameInfo(X509NameType.SimpleName, false)), ("Issued by", chain[0].GetNameInfo(X509NameType.SimpleName, true)),
                ("Valid until", chain[0].NotAfter.ToString("d", CultureInfo.CurrentCulture))]);
            page.Note("The signature is shown, not checked.");
            foreach (var certificate in signers)
            {
                certificate.Dispose();
            }
        }
        var status = $"{architecture} · {kind}";
        return (page.ToString(), status);
    }

    /// <summary>The certificates of the WIN_CERTIFICATE structures at <paramref name="offset"/>: length, revision, type, then the PKCS #7.</summary>
    private static List<X509Certificate2> SignatureCertificates(Stream stream, int offset, int size)
    {
        if (offset <= 0 || offset + size > stream.Length || size > 10 * 1024 * 1024)
        {
            return [];
        }
        var bytes = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        return type == 2 && length > 8 && length <= size ? Pkcs7.Certificates(bytes[8..length]) : [];
    }
}
