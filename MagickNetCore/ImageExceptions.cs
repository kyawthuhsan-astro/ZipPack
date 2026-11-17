namespace MagickNetCore;

/// <summary>Thrown when an image cannot be decoded, processed or encoded, or exceeds the resource limits.</summary>
public class ImageException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ImageException"/> class.</summary>
    public ImageException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ImageException"/> class.</summary>
    public ImageException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when data is not an image of a supported format, or when a request cannot be expressed in the
/// requested output format (for example an animation written as JPEG).
/// </summary>
public sealed class UnsupportedImageFormatException : ImageException
{
    /// <summary>Initializes a new instance of the <see cref="UnsupportedImageFormatException"/> class.</summary>
    public UnsupportedImageFormatException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
