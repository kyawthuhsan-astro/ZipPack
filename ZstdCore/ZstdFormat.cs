using ZstdCore.Internal;

namespace ZstdCore;

/// <summary>The kinds of data <see cref="ZstdFormat"/> can tell apart.</summary>
public enum ZstdFileKind
{
    /// <summary>The data is neither a Zstandard frame nor a protected container.</summary>
    NotZstd,

    /// <summary>The data starts with a standard Zstandard frame.</summary>
    Zstd,

    /// <summary>The data is a password protected Zstandard container written by <see cref="ZstdCompressor"/>.</summary>
    ProtectedZstd,
}

/// <summary>Identifies Zstandard data by its signature.</summary>
public static class ZstdFormat
{
    /// <summary>Gets the magic number every Zstandard frame starts with.</summary>
    public static ReadOnlySpan<byte> FrameMagic => [0x28, 0xB5, 0x2F, 0xFD];

    /// <summary>Identifies data from its first bytes (8 bytes are enough).</summary>
    public static ZstdFileKind Identify(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(EncryptionFormat.Magic))
        {
            return ZstdFileKind.ProtectedZstd;
        }

        return header.StartsWith(FrameMagic) ? ZstdFileKind.Zstd : ZstdFileKind.NotZstd;
    }

    /// <summary>Identifies a file from its first bytes.</summary>
    /// <param name="path">The path of an existing file.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static async ValueTask<ZstdFileKind> IdentifyFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] header = new byte[EncryptionFormat.MagicSize];
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous);
        int read = await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return Identify(header.AsSpan(0, read));
    }
}
