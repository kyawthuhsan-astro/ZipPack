using System.Text.Json;
using System.Text.Json.Serialization;
using ZipPack;

namespace ZipPackNet.Services;

/// <summary>The user's preferences, stored as JSON in <c>%AppData%\ZipPackNet\settings.json</c>.</summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Gets the default location of the settings file.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZipPackNet", "settings.json");

    /// <summary>Gets or sets the <see cref="ICompressionStrategy.Id"/> of the format offered first when compressing.</summary>
    public string DefaultFormatId { get; set; } = "7z";

    /// <summary>Gets or sets the compression level offered first when compressing.</summary>
    public CompressionPreset DefaultPreset { get; set; } = CompressionPreset.Normal;

    /// <summary>Gets or sets a value indicating whether Explorer opens the destination after an extraction.</summary>
    public bool OpenFolderAfterExtract { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether existing files are replaced when extracting.</summary>
    public bool OverwriteOnExtract { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether ZipPack looks for a new version in the background at start-up.</summary>
    public bool CheckForUpdatesAtStartup { get; set; } = true;

    /// <summary>Gets or sets the image format id to convert to, or <see langword="null"/> to keep each image's format.</summary>
    public string? ImageFormatId { get; set; }

    /// <summary>Gets or sets the image quality level offered first.</summary>
    public CompressionPreset ImagePreset { get; set; } = CompressionPreset.Normal;

    /// <summary>Gets or sets the longest side images are shrunk to, or <see langword="null"/> to keep their size.</summary>
    public int? ImageMaxSide { get; set; }

    /// <summary>Gets or sets a value indicating whether EXIF, XMP and similar metadata are removed from images.</summary>
    public bool ImageStripMetadata { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether compressed images are written next to (or over) the originals.</summary>
    public bool ImageReplaceOriginals { get; set; }

    /// <summary>Loads the settings; a missing or damaged file yields the defaults.</summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Saves the settings. Failing to save preferences is never worth interrupting the user for.</summary>
    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
