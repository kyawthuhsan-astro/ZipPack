namespace ZstdCore;

/// <summary>Thrown when Zstandard data cannot be compressed, decompressed or is damaged.</summary>
public class ZstdException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ZstdException"/> class.</summary>
    public ZstdException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ZstdException"/> class.</summary>
    public ZstdException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when password protected data is opened without a password or with a wrong one.</summary>
public sealed class ZstdPasswordException : ZstdException
{
    /// <summary>Initializes a new instance of the <see cref="ZstdPasswordException"/> class.</summary>
    public ZstdPasswordException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
