namespace ZipPack;

/// <summary>The base class of every failure reported by a compression strategy.</summary>
public class CompressionException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CompressionException"/> class.</summary>
    public CompressionException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CompressionException"/> class.</summary>
    public CompressionException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when protected data is opened without a password or with a wrong one.</summary>
public sealed class InvalidPasswordException : CompressionException
{
    /// <summary>Initializes a new instance of the <see cref="InvalidPasswordException"/> class.</summary>
    public InvalidPasswordException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when no registered strategy can handle a file, format or request.</summary>
public sealed class UnsupportedFormatException : CompressionException
{
    /// <summary>Initializes a new instance of the <see cref="UnsupportedFormatException"/> class.</summary>
    public UnsupportedFormatException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
