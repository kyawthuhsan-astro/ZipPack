using ZipPack;

namespace ZipPackNet.Services;

/// <summary>A failure, worded for the person in front of the screen.</summary>
/// <param name="Title">The caption of the message box.</param>
/// <param name="Text">What went wrong and, where possible, what to do about it.</param>
/// <param name="Icon">The severity.</param>
internal sealed record UserMessage(string Title, string Text, MessageBoxIcon Icon);

/// <summary>Translates exceptions into messages a user can act on, and shows them.</summary>
internal static class ErrorPresenter
{
    private const int DiskFull = unchecked((int)0x80070070);        // ERROR_DISK_FULL
    private const int HandleDiskFull = unchecked((int)0x80070027);  // ERROR_HANDLE_DISK_FULL
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>Describes an exception. Unknown exceptions get a generic text that still includes their message.</summary>
    public static UserMessage Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Task based code wraps the interesting exception.
        if (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            return Describe(aggregate.InnerExceptions[0]);
        }

        return exception switch
        {
            InvalidPasswordException => new UserMessage(
                "Wrong password", exception.Message, MessageBoxIcon.Warning),

            UnsupportedFormatException => new UserMessage(
                "Format not supported", exception.Message, MessageBoxIcon.Warning),

            CompressionException => new UserMessage(
                "Archive error", exception.Message, MessageBoxIcon.Error),

            FileNotFoundException or DirectoryNotFoundException => new UserMessage(
                "File not found",
                $"{exception.Message}\n\nIt may have been moved, renamed or deleted.",
                MessageBoxIcon.Warning),

            UnauthorizedAccessException => new UserMessage(
                "Access denied",
                $"{exception.Message}\n\nCheck that you are allowed to read the source and write to the destination, " +
                "and that the files are not read-only.",
                MessageBoxIcon.Error),

            IOException { HResult: DiskFull or HandleDiskFull } => new UserMessage(
                "Disk full",
                "There is not enough free space on the destination drive. Free up some space or choose another location.",
                MessageBoxIcon.Error),

            IOException { HResult: SharingViolation or LockViolation } => new UserMessage(
                "File in use",
                $"{exception.Message}\n\nClose the program that is using the file and try again.",
                MessageBoxIcon.Warning),

            PathTooLongException => new UserMessage(
                "Path too long", "A file path is too long for Windows. Try a destination closer to the drive root.", MessageBoxIcon.Error),

            IOException => new UserMessage("File error", exception.Message, MessageBoxIcon.Error),

            _ => new UserMessage(
                "Unexpected error",
                $"Something went wrong:\n\n{exception.Message}\n\n({exception.GetType().Name})",
                MessageBoxIcon.Error),
        };
    }

    /// <summary>Shows the description of an exception. Cancellation is not an error and shows nothing.</summary>
    public static void Show(IWin32Window? owner, Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return;
        }

        UserMessage message = Describe(exception);
        MessageBox.Show(owner, message.Text, message.Title, MessageBoxButtons.OK, message.Icon);
    }
}
