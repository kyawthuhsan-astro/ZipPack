using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZstdCore.Internal;

/// <summary>
/// Defines the password protection container that wraps a Zstandard frame.
/// </summary>
/// <remarks>
/// <code>
/// header (40 bytes) : magic "ZPZSTENC" | version (1) | reserved (3) | PBKDF2 iterations (int32 LE)
///                     | salt (16) | nonce prefix (7) | reserved (1)
/// chunk (repeated)  : length (uint32 LE, bit 31 = final chunk) | ciphertext (length) | GCM tag (16)
/// </code>
/// Every chunk is sealed with AES-256-GCM. The nonce is <c>prefix (7) | chunk counter (4, BE) | final flag (1)</c>
/// and the header is the associated data, so reordering, truncating or re-parameterising the stream is detected.
/// </remarks>
internal static class EncryptionFormat
{
    public const int HeaderSize = 40;
    public const int MagicSize = 8;
    public const int SaltSize = 16;
    public const int NoncePrefixSize = 7;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32;
    public const int ChunkSize = 64 * 1024;
    public const int DefaultIterations = 600_000;
    public const uint FinalChunkFlag = 0x8000_0000;

    private const byte Version = 1;
    private const int MaxIterations = 10_000_000;

    public static ReadOnlySpan<byte> Magic => "ZPZSTENC"u8;

    /// <summary>Writes a fresh header with random salt and nonce prefix into <paramref name="header"/>.</summary>
    public static void CreateHeader(Span<byte> header)
    {
        header.Clear();
        Magic.CopyTo(header);
        header[MagicSize] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], DefaultIterations);
        RandomNumberGenerator.Fill(header.Slice(16, SaltSize));
        RandomNumberGenerator.Fill(header.Slice(32, NoncePrefixSize));
    }

    /// <summary>Validates a header and derives the content key from the password.</summary>
    /// <exception cref="ZstdException">The header is malformed or of an unknown version.</exception>
    public static byte[] DeriveKey(ReadOnlySpan<byte> header, string password)
    {
        if (header.Length != HeaderSize || !header[..MagicSize].SequenceEqual(Magic))
        {
            throw new ZstdException("The data is not a password protected Zstandard container.");
        }

        if (header[MagicSize] != Version)
        {
            throw new ZstdException($"Unsupported protected container version {header[MagicSize]}.");
        }

        int iterations = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (iterations is < 1 or > MaxIterations)
        {
            throw new ZstdException("The protected container header is corrupted.");
        }

        return Rfc2898DeriveBytes.Pbkdf2(password, header.Slice(16, SaltSize), iterations, HashAlgorithmName.SHA256, KeySize);
    }

    /// <summary>Builds the nonce of a chunk.</summary>
    public static void BuildNonce(ReadOnlySpan<byte> header, uint counter, bool isFinal, Span<byte> nonce)
    {
        header.Slice(32, NoncePrefixSize).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixSize..], counter);
        nonce[NonceSize - 1] = isFinal ? (byte)1 : (byte)0;
    }
}
