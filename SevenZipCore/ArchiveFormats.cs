namespace SevenZipCore;

/// <summary>
/// The archive formats 7-Zip can read. The numeric value of each member is the 7-Zip format identifier
/// (the variable byte of the handler's class id).
/// </summary>
public enum InArchiveFormat : byte
{
    /// <summary>ZIP.</summary>
    Zip = 0x01,

    /// <summary>bzip2.</summary>
    BZip2 = 0x02,

    /// <summary>RAR up to version 4.</summary>
    Rar4 = 0x03,

    /// <summary>ARJ.</summary>
    Arj = 0x04,

    /// <summary>Unix compress (<c>.Z</c>).</summary>
    Lzw = 0x05,

    /// <summary>LZH / LHA.</summary>
    Lzh = 0x06,

    /// <summary>7z.</summary>
    SevenZip = 0x07,

    /// <summary>Microsoft Cabinet.</summary>
    Cab = 0x08,

    /// <summary>Nullsoft installer.</summary>
    Nsis = 0x09,

    /// <summary>Raw LZMA stream.</summary>
    Lzma = 0x0A,

    /// <summary>LZMA86.</summary>
    Lzma86 = 0x0B,

    /// <summary>xz.</summary>
    XZ = 0x0C,

    /// <summary>PPMd stream.</summary>
    Ppmd = 0x0D,

    /// <summary>Zstandard.</summary>
    Zstd = 0x0E,

    /// <summary>LVM volume.</summary>
    Lvm = 0xBF,

    /// <summary>Android verified boot image.</summary>
    Avb = 0xC0,

    /// <summary>Android logical partition image.</summary>
    Lp = 0xC1,

    /// <summary>Android sparse image.</summary>
    Sparse = 0xC2,

    /// <summary>APFS image.</summary>
    Apfs = 0xC3,

    /// <summary>VHDX disk image.</summary>
    Vhdx = 0xC4,

    /// <summary>Base64 encoded data.</summary>
    Base64 = 0xC5,

    /// <summary>COFF object file.</summary>
    Coff = 0xC6,

    /// <summary>ext2/3/4 image.</summary>
    Ext = 0xC7,

    /// <summary>VMDK disk image.</summary>
    Vmdk = 0xC8,

    /// <summary>VirtualBox disk image.</summary>
    Vdi = 0xC9,

    /// <summary>QCOW disk image.</summary>
    QCow = 0xCA,

    /// <summary>GUID partition table.</summary>
    Gpt = 0xCB,

    /// <summary>RAR version 5 and later.</summary>
    Rar = 0xCC,

    /// <summary>Intel HEX.</summary>
    IHex = 0xCD,

    /// <summary>Microsoft help 2 / LIT.</summary>
    Hxs = 0xCE,

    /// <summary>Terse executable.</summary>
    TE = 0xCF,

    /// <summary>UEFI capsule.</summary>
    UefiCapsule = 0xD0,

    /// <summary>UEFI firmware.</summary>
    UefiFirmware = 0xD1,

    /// <summary>SquashFS image.</summary>
    SquashFS = 0xD2,

    /// <summary>CramFS image.</summary>
    CramFS = 0xD3,

    /// <summary>Apple partition map.</summary>
    Apm = 0xD4,

    /// <summary>Microsoft LZ (<c>compress.exe</c>).</summary>
    Mslz = 0xD5,

    /// <summary>Flash video.</summary>
    Flv = 0xD6,

    /// <summary>Shockwave Flash.</summary>
    Swf = 0xD7,

    /// <summary>Compressed Shockwave Flash.</summary>
    Swfc = 0xD8,

    /// <summary>NTFS image.</summary>
    Ntfs = 0xD9,

    /// <summary>FAT image.</summary>
    Fat = 0xDA,

    /// <summary>Master boot record.</summary>
    Mbr = 0xDB,

    /// <summary>VHD disk image.</summary>
    Vhd = 0xDC,

    /// <summary>Windows portable executable.</summary>
    PE = 0xDD,

    /// <summary>ELF executable.</summary>
    Elf = 0xDE,

    /// <summary>Mach-O executable.</summary>
    MachO = 0xDF,

    /// <summary>UDF image.</summary>
    Udf = 0xE0,

    /// <summary>XAR.</summary>
    Xar = 0xE1,

    /// <summary>Mach-O universal binary.</summary>
    Mub = 0xE2,

    /// <summary>HFS image.</summary>
    Hfs = 0xE3,

    /// <summary>Apple disk image.</summary>
    Dmg = 0xE4,

    /// <summary>Microsoft compound file (MSI, DOC, XLS …).</summary>
    Compound = 0xE5,

    /// <summary>Windows imaging format.</summary>
    Wim = 0xE6,

    /// <summary>ISO 9660 image.</summary>
    Iso = 0xE7,

    /// <summary>Microsoft compiled HTML help.</summary>
    Chm = 0xE9,

    /// <summary>A file split into numbered parts.</summary>
    Split = 0xEA,

    /// <summary>RPM package.</summary>
    Rpm = 0xEB,

    /// <summary>Debian package / ar archive.</summary>
    Deb = 0xEC,

    /// <summary>cpio.</summary>
    Cpio = 0xED,

    /// <summary>tar.</summary>
    Tar = 0xEE,

    /// <summary>gzip.</summary>
    GZip = 0xEF,
}

/// <summary>
/// The archive formats 7-Zip can write. The numeric values match <see cref="InArchiveFormat"/>.
/// </summary>
public enum OutArchiveFormat : byte
{
    /// <summary>ZIP — the most widely supported format.</summary>
    Zip = 0x01,

    /// <summary>bzip2 — a single compressed file.</summary>
    BZip2 = 0x02,

    /// <summary>7z — the best compression and strong AES-256 encryption.</summary>
    SevenZip = 0x07,

    /// <summary>xz — a single compressed file.</summary>
    XZ = 0x0C,

    /// <summary>tar — an uncompressed bundle of files.</summary>
    Tar = 0xEE,

    /// <summary>gzip — a single compressed file.</summary>
    GZip = 0xEF,
}

/// <summary>The compression effort, equivalent to the <c>-mx</c> switch of 7-Zip.</summary>
public enum CompressionLevel
{
    /// <summary>Store without compression (<c>-mx0</c>).</summary>
    None = 0,

    /// <summary>Fastest compression (<c>-mx1</c>).</summary>
    Fast = 1,

    /// <summary>Fast compression (<c>-mx3</c>).</summary>
    Low = 3,

    /// <summary>The default (<c>-mx5</c>).</summary>
    Normal = 5,

    /// <summary>Strong compression (<c>-mx7</c>).</summary>
    High = 7,

    /// <summary>Strongest compression (<c>-mx9</c>).</summary>
    Ultra = 9,
}

/// <summary>The compression method. Not every method is valid for every format.</summary>
public enum CompressionMethod
{
    /// <summary>The default method of the format.</summary>
    Default,

    /// <summary>No compression (7z, ZIP).</summary>
    Copy,

    /// <summary>Deflate (ZIP).</summary>
    Deflate,

    /// <summary>Deflate64 (ZIP).</summary>
    Deflate64,

    /// <summary>BZip2 (7z, ZIP).</summary>
    BZip2,

    /// <summary>LZMA (7z, ZIP).</summary>
    Lzma,

    /// <summary>LZMA2 (7z) — the 7z default, scales well across cores.</summary>
    Lzma2,

    /// <summary>PPMd (7z, ZIP) — very effective on text.</summary>
    Ppmd,
}

/// <summary>The encryption applied to password protected ZIP archives.</summary>
public enum ZipEncryptionMethod
{
    /// <summary>The legacy, weak ZipCrypto scheme. Only use it for compatibility with very old tools.</summary>
    ZipCrypto,

    /// <summary>AES with a 128-bit key.</summary>
    Aes128,

    /// <summary>AES with a 192-bit key.</summary>
    Aes192,

    /// <summary>AES with a 256-bit key.</summary>
    Aes256,
}

/// <summary>Helpers for the archive format enumerations.</summary>
public static class ArchiveFormats
{
    /// <summary>Gets the conventional file extension (with leading dot) of a writable format.</summary>
    public static string GetExtension(this OutArchiveFormat format) => format switch
    {
        OutArchiveFormat.SevenZip => ".7z",
        OutArchiveFormat.Zip => ".zip",
        OutArchiveFormat.Tar => ".tar",
        OutArchiveFormat.GZip => ".gz",
        OutArchiveFormat.BZip2 => ".bz2",
        OutArchiveFormat.XZ => ".xz",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>Gets a value indicating whether the format compresses exactly one file (gzip, bzip2, xz).</summary>
    public static bool IsSingleFileFormat(this OutArchiveFormat format) =>
        format is OutArchiveFormat.GZip or OutArchiveFormat.BZip2 or OutArchiveFormat.XZ;

    /// <summary>Gets the readable format corresponding to a writable format.</summary>
    public static InArchiveFormat ToInArchiveFormat(this OutArchiveFormat format) => (InArchiveFormat)(byte)format;

    /// <summary>Gets the COM class id of the 7-Zip handler of a format.</summary>
    internal static Guid GetClassId(byte formatId) =>
        new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, formatId, 0x00, 0x00);
}
