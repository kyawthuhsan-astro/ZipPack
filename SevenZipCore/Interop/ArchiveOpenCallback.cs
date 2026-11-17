using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SevenZipCore.Interop;

/// <summary>
/// Answers the questions 7-Zip asks while opening an archive: the password of encrypted headers and the
/// sibling files of multi-volume archives.
/// </summary>
[GeneratedComClass]
internal sealed partial class ArchiveOpenCallback(string? password, string? archivePath, InteropErrors errors)
    : IArchiveOpenCallback, IArchiveOpenVolumeCallback, ICryptoGetTextPassword, IDisposable
{
    private readonly List<FileStream> _volumes = [];

    /// <summary>Gets a value indicating whether 7-Zip asked for a password.</summary>
    public bool PasswordRequested { get; private set; }

    public int SetTotal(nint files, nint bytes) => HResult.Ok;

    public int SetCompleted(nint files, nint bytes) => HResult.Ok;

    public int GetProperty(uint propId, ref PropVariant value)
    {
        // Ownership of the string passes to 7-Zip, which clears the variant.
        value = propId == PropId.Name && archivePath is not null
            ? PropVariant.FromString(Path.GetFileName(archivePath))
            : default;
        return HResult.Ok;
    }

    public int GetStream(nint name, out IInStream? inStream)
    {
        inStream = null;
        try
        {
            string? volumeName = Marshal.PtrToStringUni(name);
            string? directory = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrEmpty(volumeName) || directory is null)
            {
                return HResult.False;
            }

            string volumePath = Path.Combine(directory, Path.GetFileName(volumeName));
            if (!File.Exists(volumePath))
            {
                return HResult.False;
            }

            var volume = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _volumes.Add(volume);
            inStream = new InStreamWrapper(volume, errors);
            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }

    public int CryptoGetTextPassword(out nint password1)
    {
        PasswordRequested = true;
        if (password is null)
        {
            password1 = 0;
            return HResult.Abort;
        }

        // 7-Zip frees the BSTR.
        password1 = Marshal.StringToBSTR(password);
        return HResult.Ok;
    }

    public void Dispose()
    {
        foreach (FileStream volume in _volumes)
        {
            volume.Dispose();
        }

        _volumes.Clear();
    }
}
