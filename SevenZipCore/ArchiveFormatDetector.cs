using System.Collections.Frozen;

namespace SevenZipCore;

/// <summary>
/// Recognises archive formats by their signature ("magic bytes") and, as a fallback, by file extension.
/// The detection is purely managed and does not load the native 7-Zip library.
/// </summary>
public static class ArchiveFormatDetector
{
    private const int HeaderSize = 512;
    private const int TarMagicOffset = 257;
    private const int IsoMagicOffset = 0x8001;

    // Ordered so that longer, more specific signatures are tested first.
    private static readonly (byte[] Signature, int Offset, InArchiveFormat Format)[] Signatures =
    [
        ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], 0, InArchiveFormat.SevenZip),
        ([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00], 0, InArchiveFormat.Rar),
        ([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00], 0, InArchiveFormat.Rar4),
        ([0x50, 0x4B, 0x03, 0x04], 0, InArchiveFormat.Zip),
        ([0x50, 0x4B, 0x05, 0x06], 0, InArchiveFormat.Zip),
        ([0x50, 0x4B, 0x07, 0x08], 0, InArchiveFormat.Zip),
        ([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], 0, InArchiveFormat.XZ),
        ([0x28, 0xB5, 0x2F, 0xFD], 0, InArchiveFormat.Zstd),
        ([0x1F, 0x8B, 0x08], 0, InArchiveFormat.GZip),
        ([0x42, 0x5A, 0x68], 0, InArchiveFormat.BZip2),
        ("ustar"u8.ToArray(), TarMagicOffset, InArchiveFormat.Tar),
        ("MSCF"u8.ToArray(), 0, InArchiveFormat.Cab),
        ("MSWIM\0\0\0"u8.ToArray(), 0, InArchiveFormat.Wim),
        ("ITSF"u8.ToArray(), 0, InArchiveFormat.Chm),
        ("ITOLITLS"u8.ToArray(), 0, InArchiveFormat.Hxs),
        ("!<arch>\n"u8.ToArray(), 0, InArchiveFormat.Deb),
        ([0xED, 0xAB, 0xEE, 0xDB], 0, InArchiveFormat.Rpm),
        ("07070"u8.ToArray(), 0, InArchiveFormat.Cpio),
        ("xar!"u8.ToArray(), 0, InArchiveFormat.Xar),
        ("vhdxfile"u8.ToArray(), 0, InArchiveFormat.Vhdx),
        ("conectix"u8.ToArray(), 0, InArchiveFormat.Vhd),
        ("KDMV"u8.ToArray(), 0, InArchiveFormat.Vmdk),
        ([0x51, 0x46, 0x49, 0xFB], 0, InArchiveFormat.QCow),
        ("hsqs"u8.ToArray(), 0, InArchiveFormat.SquashFS),
        ([0x7F, 0x45, 0x4C, 0x46], 0, InArchiveFormat.Elf),
        ("-lh"u8.ToArray(), 2, InArchiveFormat.Lzh),
        ("FLV"u8.ToArray(), 0, InArchiveFormat.Flv),
        ("FWS"u8.ToArray(), 0, InArchiveFormat.Swf),
        ("CWS"u8.ToArray(), 0, InArchiveFormat.Swfc),
        ("ZWS"u8.ToArray(), 0, InArchiveFormat.Swfc),
        ([0x60, 0xEA], 0, InArchiveFormat.Arj),
        ([0x1F, 0x9D], 0, InArchiveFormat.Lzw),
    ];

    private static readonly FrozenDictionary<string, InArchiveFormat> Extensions =
        new Dictionary<string, InArchiveFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".7z"] = InArchiveFormat.SevenZip,
            [".zip"] = InArchiveFormat.Zip,
            [".jar"] = InArchiveFormat.Zip,
            [".rar"] = InArchiveFormat.Rar,
            [".tar"] = InArchiveFormat.Tar,
            [".gz"] = InArchiveFormat.GZip,
            [".tgz"] = InArchiveFormat.GZip,
            [".bz2"] = InArchiveFormat.BZip2,
            [".tbz2"] = InArchiveFormat.BZip2,
            [".xz"] = InArchiveFormat.XZ,
            [".txz"] = InArchiveFormat.XZ,
            [".zst"] = InArchiveFormat.Zstd,
            [".tzst"] = InArchiveFormat.Zstd,
            [".lzma"] = InArchiveFormat.Lzma,
            [".lzh"] = InArchiveFormat.Lzh,
            [".lha"] = InArchiveFormat.Lzh,
            [".arj"] = InArchiveFormat.Arj,
            [".z"] = InArchiveFormat.Lzw,
            [".cab"] = InArchiveFormat.Cab,
            [".chm"] = InArchiveFormat.Chm,
            [".deb"] = InArchiveFormat.Deb,
            [".ar"] = InArchiveFormat.Deb,
            [".a"] = InArchiveFormat.Deb,
            [".rpm"] = InArchiveFormat.Rpm,
            [".cpio"] = InArchiveFormat.Cpio,
            [".iso"] = InArchiveFormat.Iso,
            [".udf"] = InArchiveFormat.Udf,
            [".wim"] = InArchiveFormat.Wim,
            [".esd"] = InArchiveFormat.Wim,
            [".xar"] = InArchiveFormat.Xar,
            [".pkg"] = InArchiveFormat.Xar,
            [".hfs"] = InArchiveFormat.Hfs,
            [".dmg"] = InArchiveFormat.Dmg,
            [".msi"] = InArchiveFormat.Compound,
            [".msp"] = InArchiveFormat.Compound,
            [".msm"] = InArchiveFormat.Compound,
            [".vhd"] = InArchiveFormat.Vhd,
            [".vhdx"] = InArchiveFormat.Vhdx,
            [".avhdx"] = InArchiveFormat.Vhdx,
            [".vdi"] = InArchiveFormat.Vdi,
            [".vmdk"] = InArchiveFormat.Vmdk,
            [".qcow"] = InArchiveFormat.QCow,
            [".qcow2"] = InArchiveFormat.QCow,
            [".squashfs"] = InArchiveFormat.SquashFS,
            [".ntfs"] = InArchiveFormat.Ntfs,
            [".fat"] = InArchiveFormat.Fat,
            [".ext"] = InArchiveFormat.Ext,
            [".apfs"] = InArchiveFormat.Apfs,
            [".gpt"] = InArchiveFormat.Gpt,
            [".mbr"] = InArchiveFormat.Mbr,
            [".simg"] = InArchiveFormat.Sparse,
            [".ihex"] = InArchiveFormat.IHex,
            [".b64"] = InArchiveFormat.Base64,
            [".flv"] = InArchiveFormat.Flv,
            [".swf"] = InArchiveFormat.Swf,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Detects the format of a file from its content, falling back to its extension.</summary>
    /// <param name="path">The path of an existing file.</param>
    /// <returns>The detected format, or <see langword="null"/> when the file is not a known archive.</returns>
    public static InArchiveFormat? Detect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        IReadOnlyList<InArchiveFormat> candidates = GetCandidates(stream, path);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>Detects the format of a seekable stream from its signature. The position is restored afterwards.</summary>
    /// <param name="stream">A readable, seekable stream holding the archive from its very beginning.</param>
    /// <returns>The detected format, or <see langword="null"/> when no known signature matches.</returns>
    public static InArchiveFormat? Detect(Stream stream)
    {
        IReadOnlyList<InArchiveFormat> candidates = GetCandidates(stream, fileName: null);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>Gets the format conventionally associated with the extension of a file name.</summary>
    public static InArchiveFormat? DetectByExtension(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Extensions.TryGetValue(Path.GetExtension(fileName), out InArchiveFormat format) ? format : null;
    }

    /// <summary>
    /// Gets the formats worth trying for a stream, most likely first: the signature match, then the extension match.
    /// </summary>
    internal static IReadOnlyList<InArchiveFormat> GetCandidates(Stream stream, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        // The archive is expected to span the whole stream, wherever the caller left the position.
        var candidates = new List<InArchiveFormat>(2);
        long position = stream.Position;
        try
        {
            stream.Position = 0;
            if (DetectBySignature(stream) is { } bySignature)
            {
                candidates.Add(bySignature);
            }
        }
        finally
        {
            stream.Position = position;
        }

        if (fileName is not null && DetectByExtension(fileName) is { } byExtension && !candidates.Contains(byExtension))
        {
            candidates.Add(byExtension);
        }

        return candidates;
    }

    private static InArchiveFormat? DetectBySignature(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header = header[..stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false)];

        foreach ((byte[] signature, int offset, InArchiveFormat format) in Signatures)
        {
            if (header.Length >= offset + signature.Length && header.Slice(offset, signature.Length).SequenceEqual(signature))
            {
                return format;
            }
        }

        // ISO 9660 keeps its "CD001" marker in the first volume descriptor, 32 KiB into the image.
        if (stream.Length >= IsoMagicOffset + 5)
        {
            Span<byte> marker = stackalloc byte[5];
            stream.Position = IsoMagicOffset;
            if (stream.ReadAtLeast(marker, marker.Length, throwOnEndOfStream: false) == marker.Length && marker.SequenceEqual("CD001"u8))
            {
                return InArchiveFormat.Iso;
            }
        }

        return null;
    }
}
