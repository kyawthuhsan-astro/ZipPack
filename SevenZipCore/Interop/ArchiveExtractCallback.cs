using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SevenZipCore.Interop;

/// <summary>Decides where the content of extracted entries goes.</summary>
internal interface IExtractSink
{
    /// <summary>Returns the stream to write an entry to, or <see langword="null"/> to skip its content.</summary>
    Stream? OpenEntry(ArchiveEntry entry);

    /// <summary>Called when 7-Zip has finished writing an entry that <see cref="OpenEntry"/> returned a stream for.</summary>
    void CloseEntry(ArchiveEntry entry, Stream stream, bool succeeded);
}

/// <summary>
/// Receives the data, progress and per-entry results of an extraction (or test) run from 7-Zip.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class ArchiveExtractCallback(
    IReadOnlyList<ArchiveEntry> entries,
    IExtractSink? sink,
    string? password,
    IProgress<SevenZipProgress>? progress,
    InteropErrors errors,
    CancellationToken cancellationToken)
    : IArchiveExtractCallback, ICryptoGetTextPassword
{
    private const int AskModeExtract = 0;
    private const int ResultOk = 0;
    private const int ResultDataError = 2;
    private const int ResultCrcError = 3;
    private const int ResultWrongPassword = 9;

    private ArchiveEntry? _currentEntry;
    private Stream? _currentStream;
    private long _total;

    /// <summary>Gets a value indicating whether 7-Zip asked for a password that was not supplied.</summary>
    public bool PasswordMissing { get; private set; }

    /// <summary>Gets the failure of the first entry that could not be extracted, if any.</summary>
    public SevenZipException? FirstEntryError { get; private set; }

    /// <summary>Gets the number of entries that could not be extracted.</summary>
    public int FailedEntries { get; private set; }

    public int SetTotal(ulong total)
    {
        _total = (long)total;
        return HResult.Ok;
    }

    public int SetCompleted(nint completeValue)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return HResult.Abort;
        }

        if (progress is not null && completeValue != 0)
        {
            try
            {
                progress.Report(new SevenZipProgress((long)*(ulong*)completeValue, _total, _currentEntry?.Path));
            }
            catch (Exception ex)
            {
                return errors.Record(ex);
            }
        }

        return HResult.Ok;
    }

    public int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode)
    {
        outStream = null;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return HResult.Abort;
            }

            CloseCurrent(succeeded: false);
            _currentEntry = entries[(int)index];
            if (askExtractMode != AskModeExtract || sink is null)
            {
                return HResult.Ok;
            }

            _currentStream = sink.OpenEntry(_currentEntry);
            if (_currentStream is not null)
            {
                outStream = new SequentialOutStreamWrapper(_currentStream, errors);
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }

    public int PrepareOperation(int askExtractMode) => HResult.Ok;

    public int SetOperationResult(int operationResult)
    {
        try
        {
            ArchiveEntry? entry = _currentEntry;
            CloseCurrent(succeeded: operationResult == ResultOk);

            if (operationResult != ResultOk)
            {
                FailedEntries++;
                FirstEntryError ??= CreateEntryError(operationResult, entry);
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }

    public int CryptoGetTextPassword(out nint password1)
    {
        if (password is null)
        {
            PasswordMissing = true;
            password1 = 0;
            return HResult.Abort;
        }

        // 7-Zip frees the BSTR.
        password1 = Marshal.StringToBSTR(password);
        return HResult.Ok;
    }

    /// <summary>Closes the stream of the entry in flight, e.g. after 7-Zip aborted in the middle of it.</summary>
    public void CloseCurrent(bool succeeded)
    {
        if (_currentStream is { } stream && _currentEntry is { } entry)
        {
            _currentStream = null;
            sink?.CloseEntry(entry, stream, succeeded);
        }
    }

    private SevenZipException CreateEntryError(int operationResult, ArchiveEntry? entry)
    {
        string name = entry?.Path ?? "?";
        bool encrypted = entry?.IsEncrypted ?? false;

        if (operationResult == ResultWrongPassword
            || (encrypted && operationResult is ResultDataError or ResultCrcError))
        {
            return new SevenZipPasswordException(
                password is null
                    ? $"'{name}' is encrypted; a password is required."
                    : $"The password is wrong or '{name}' is corrupted.");
        }

        string reason = operationResult switch
        {
            1 => "the compression method is not supported",
            ResultDataError => "the data is corrupted",
            ResultCrcError => "the checksum does not match",
            4 => "the data is unavailable",
            5 => "the data ends unexpectedly",
            6 => "there is data after the end of the archive",
            7 => "the file is not an archive",
            8 => "the archive headers are corrupted",
            _ => $"7-Zip reported error {operationResult}",
        };

        return new SevenZipArchiveException($"'{name}' could not be extracted: {reason}.");
    }
}
