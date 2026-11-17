using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SevenZipCore.Interop;

// The COM interfaces exported by 7z.dll, declared for source-generated COM interop
// ([GeneratedComInterface]) so no runtime marshalling stubs or reflection are needed.
//
// Conventions:
//  * every method is [PreserveSig] and returns the raw HRESULT, so error handling is explicit;
//  * 7-Zip interfaces that derive from each other in C++ are declared flat (the base methods are repeated
//    first), which yields the identical vtable layout;
//  * pointers that native code may legitimately pass as NULL are declared as nint.

/// <summary>Receives progress while an archive is being opened.</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600100000")]
internal partial interface IArchiveOpenCallback
{
    [PreserveSig]
    int SetTotal(nint files, nint bytes);

    [PreserveSig]
    int SetCompleted(nint files, nint bytes);
}

/// <summary>Supplies the sibling volumes of a multi-volume archive.</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600300000")]
internal partial interface IArchiveOpenVolumeCallback
{
    [PreserveSig]
    int GetProperty(uint propId, ref PropVariant value);

    [PreserveSig]
    int GetStream(nint name, out IInStream? inStream);
}

/// <summary>Supplies the password for reading an archive.</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000500100000")]
internal partial interface ICryptoGetTextPassword
{
    [PreserveSig]
    int CryptoGetTextPassword(out nint password);
}

/// <summary>Supplies the password for writing an archive.</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000500110000")]
internal partial interface ICryptoGetTextPassword2
{
    [PreserveSig]
    int CryptoGetTextPassword2(out int passwordIsDefined, out nint password);
}

/// <summary>Drives the extraction of archive items (<c>IProgress</c> + <c>IArchiveExtractCallback</c>).</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600200000")]
internal partial interface IArchiveExtractCallback
{
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(nint completeValue);

    [PreserveSig]
    int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode);

    [PreserveSig]
    int PrepareOperation(int askExtractMode);

    [PreserveSig]
    int SetOperationResult(int operationResult);
}

/// <summary>Describes and supplies the items of an archive being written (<c>IProgress</c> + <c>IArchiveUpdateCallback</c>).</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600800000")]
internal partial interface IArchiveUpdateCallback
{
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(nint completeValue);

    [PreserveSig]
    int GetUpdateItemInfo(uint index, nint newData, nint newProperties, nint indexInArchive);

    [PreserveSig]
    int GetProperty(uint index, uint propId, ref PropVariant value);

    [PreserveSig]
    int GetStream(uint index, out ISequentialInStream? inStream);

    [PreserveSig]
    int SetOperationResult(int operationResult);
}

/// <summary>A forward-only input stream.</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300010000")]
internal partial interface ISequentialInStream
{
    [PreserveSig]
    int Read(nint data, uint size, nint processedSize);
}

/// <summary>A forward-only output stream.</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300020000")]
internal partial interface ISequentialOutStream
{
    [PreserveSig]
    int Write(nint data, uint size, nint processedSize);
}

/// <summary>A seekable input stream (<c>ISequentialInStream</c> + <c>IInStream</c>).</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300030000")]
internal partial interface IInStream
{
    [PreserveSig]
    int Read(nint data, uint size, nint processedSize);

    [PreserveSig]
    int Seek(long offset, uint seekOrigin, nint newPosition);
}

/// <summary>A seekable output stream (<c>ISequentialOutStream</c> + <c>IOutStream</c>).</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300040000")]
internal partial interface IOutStream
{
    [PreserveSig]
    int Write(nint data, uint size, nint processedSize);

    [PreserveSig]
    int Seek(long offset, uint seekOrigin, nint newPosition);

    [PreserveSig]
    int SetSize(ulong newSize);
}

/// <summary>Reads an archive.</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600600000")]
internal partial interface IInArchive
{
    [PreserveSig]
    int Open(IInStream stream, ref ulong maxCheckStartPosition, IArchiveOpenCallback? openCallback);

    [PreserveSig]
    int Close();

    [PreserveSig]
    int GetNumberOfItems(out uint numItems);

    [PreserveSig]
    int GetProperty(uint index, uint propId, ref PropVariant value);

    /// <param name="indices">A sorted <c>UInt32</c> array, or NULL together with <c>numItems = 0xFFFFFFFF</c> for all items.</param>
    /// <param name="numItems">The number of indices.</param>
    /// <param name="testMode">Non-zero to test instead of extract.</param>
    /// <param name="extractCallback">The callback that receives the data.</param>
    [PreserveSig]
    int Extract(nint indices, uint numItems, int testMode, IArchiveExtractCallback extractCallback);

    [PreserveSig]
    int GetArchiveProperty(uint propId, ref PropVariant value);

    [PreserveSig]
    int GetNumberOfProperties(out uint numProperties);

    [PreserveSig]
    int GetPropertyInfo(uint index, out nint name, out uint propId, out ushort varType);

    [PreserveSig]
    int GetNumberOfArchiveProperties(out uint numProperties);

    [PreserveSig]
    int GetArchivePropertyInfo(uint index, out nint name, out uint propId, out ushort varType);
}

/// <summary>Writes an archive.</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600A00000")]
internal partial interface IOutArchive
{
    [PreserveSig]
    int UpdateItems(ISequentialOutStream outStream, uint numItems, IArchiveUpdateCallback updateCallback);

    [PreserveSig]
    int GetFileTimeType(out uint type);
}

/// <summary>Configures the codec of an archive handler (the <c>-m</c> switches of the 7-Zip command line).</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600030000")]
internal partial interface ISetProperties
{
    /// <param name="names">An array of <c>const wchar_t*</c>.</param>
    /// <param name="values">An array of <c>PROPVARIANT</c>.</param>
    /// <param name="numProperties">The number of elements in both arrays.</param>
    [PreserveSig]
    int SetProperties(nint names, nint values, uint numProperties);
}
