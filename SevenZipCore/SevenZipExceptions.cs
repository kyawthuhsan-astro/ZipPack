namespace SevenZipCore;

/// <summary>The base class of every failure reported by this library.</summary>
public class SevenZipException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SevenZipException"/> class.</summary>
    public SevenZipException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SevenZipException"/> class.</summary>
    public SevenZipException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when the native 7-Zip library cannot be found, loaded or does not support a format.</summary>
public sealed class SevenZipLibraryException : SevenZipException
{
    /// <summary>Initializes a new instance of the <see cref="SevenZipLibraryException"/> class.</summary>
    public SevenZipLibraryException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a file is not a valid archive, is damaged, or is of an unsupported format.</summary>
public sealed class SevenZipArchiveException : SevenZipException
{
    /// <summary>Initializes a new instance of the <see cref="SevenZipArchiveException"/> class.</summary>
    public SevenZipArchiveException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an encrypted archive is opened without a password or with a wrong one.</summary>
public sealed class SevenZipPasswordException : SevenZipException
{
    /// <summary>Initializes a new instance of the <see cref="SevenZipPasswordException"/> class.</summary>
    public SevenZipPasswordException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
