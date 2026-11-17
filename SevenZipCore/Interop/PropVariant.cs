using System.Runtime.InteropServices;

namespace SevenZipCore.Interop;

/// <summary>
/// A blittable view of the Win32 <c>PROPVARIANT</c> structure, limited to the value kinds 7-Zip uses.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    private const ushort VtEmpty = 0;
    private const ushort VtI2 = 2;
    private const ushort VtI4 = 3;
    private const ushort VtBstr = 8;
    private const ushort VtBool = 11;
    private const ushort VtUI1 = 17;
    private const ushort VtUI2 = 18;
    private const ushort VtUI4 = 19;
    private const ushort VtI8 = 20;
    private const ushort VtUI8 = 21;
    private const ushort VtFileTime = 64;

    [FieldOffset(0)] private ushort _varType;
    [FieldOffset(8)] private nint _pointer;
    [FieldOffset(8)] private uint _uint32;
    [FieldOffset(8)] private int _int32;
    [FieldOffset(8)] private long _int64;
    [FieldOffset(8)] private ulong _uint64;
    [FieldOffset(8)] private short _bool;

    // Pads the union to two pointers so the size matches the native structure on x86 (16) and x64 (24).
    [FieldOffset(8)] private readonly PointerPair _size;

    public readonly bool IsEmpty => _varType == VtEmpty;

    public static PropVariant FromUInt32(uint value) => new() { _varType = VtUI4, _uint32 = value };

    public static PropVariant FromUInt64(ulong value) => new() { _varType = VtUI8, _uint64 = value };

    public static PropVariant FromBool(bool value) => new() { _varType = VtBool, _bool = value ? (short)-1 : (short)0 };

    public static PropVariant FromFileTime(DateTime value) => new() { _varType = VtFileTime, _int64 = ToFileTime(value) };

    /// <summary>Creates a string variant. The BSTR is owned by the variant; release it with <see cref="Clear"/>
    /// unless ownership is handed over to native code.</summary>
    public static PropVariant FromString(string value) => new() { _varType = VtBstr, _pointer = Marshal.StringToBSTR(value) };

    public readonly string? AsString() => _varType == VtBstr && _pointer != 0 ? Marshal.PtrToStringBSTR(_pointer) : null;

    public readonly bool AsBool() => _varType switch
    {
        VtBool => _bool != 0,
        VtEmpty => false,
        _ => AsUInt64() is > 0,
    };

    public readonly ulong? AsUInt64() => _varType switch
    {
        VtUI1 => (byte)_uint32,
        VtUI2 => (ushort)_uint32,
        VtUI4 => _uint32,
        VtUI8 => _uint64,
        VtI2 => (ulong)Math.Max((short)_int32, (short)0),
        VtI4 => (ulong)Math.Max(_int32, 0),
        VtI8 => (ulong)Math.Max(_int64, 0),
        _ => null,
    };

    public readonly DateTime? AsDateTime()
    {
        if (_varType != VtFileTime || _int64 <= 0)
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc(_int64);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Releases the memory owned by the variant and resets it to empty.</summary>
    public void Clear()
    {
        if (_varType == VtBstr && _pointer != 0)
        {
            Marshal.FreeBSTR(_pointer);
        }

        this = default;
    }

    private static long ToFileTime(DateTime value)
    {
        try
        {
            return value.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PointerPair
    {
        private readonly nint _first;
        private readonly nint _second;
    }
}

/// <summary>The 7-Zip item and archive property identifiers (<c>kpid*</c>) used by this library.</summary>
internal static class PropId
{
    public const uint Path = 3;
    public const uint Name = 4;
    public const uint IsDirectory = 6;
    public const uint Size = 7;
    public const uint PackedSize = 8;
    public const uint Attributes = 9;
    public const uint CreationTime = 10;
    public const uint LastAccessTime = 11;
    public const uint LastWriteTime = 12;
    public const uint Solid = 13;
    public const uint Encrypted = 15;
    public const uint Crc = 19;
    public const uint IsAnti = 21;
    public const uint Method = 22;
    public const uint Comment = 28;
}

/// <summary>The <c>HRESULT</c> values exchanged with 7-Zip.</summary>
internal static class HResult
{
    public const int Ok = 0;
    public const int False = 1;
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int NoInterface = unchecked((int)0x80004002);
    public const int Abort = unchecked((int)0x80004004);
    public const int Fail = unchecked((int)0x80004005);
    public const int OutOfMemory = unchecked((int)0x8007000E);
    public const int InvalidArgument = unchecked((int)0x80070057);
}
