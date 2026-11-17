using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SevenZipCore;

/// <summary>
/// Locates and loads the native 7-Zip engine (<c>7z.dll</c>) and creates its archive handler objects.
/// </summary>
/// <remarks>
/// The library is loaded once, on first use, and stays loaded for the lifetime of the process. By default it
/// is searched next to the application in an architecture specific sub-folder (<c>x64\7z.dll</c>,
/// <c>x86\7z.dll</c>, <c>arm64\7z.dll</c>), then directly next to the application, then in the 7-Zip
/// installation folder. Call <see cref="SetLibraryPath"/> before the first operation to use a specific file.
/// </remarks>
public static unsafe class SevenZipLibrary
{
    private const string LibraryFileName = "7z.dll";
    private static readonly Lock Gate = new();
    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private static string? _configuredPath;
    private static string? _loadedPath;
    private static delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int> _createObject;

    /// <summary>Gets the full path of the loaded native library, or <see langword="null"/> when it is not loaded yet.</summary>
    public static string? LibraryPath
    {
        get
        {
            lock (Gate)
            {
                return _loadedPath;
            }
        }
    }

    /// <summary>Gets a value indicating whether the native library can be found and loaded on this machine.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                EnsureLoaded();
                return true;
            }
            catch (SevenZipLibraryException)
            {
                return false;
            }
        }
    }

    /// <summary>Selects the native library file to use instead of the default search.</summary>
    /// <param name="path">The full path of a <c>7z.dll</c> matching the bitness of the process.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidOperationException">A different library has already been loaded.</exception>
    public static void SetLibraryPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The 7-Zip library '{fullPath}' does not exist.", fullPath);
        }

        lock (Gate)
        {
            if (_loadedPath is not null && !string.Equals(_loadedPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The 7-Zip library has already been loaded from '{_loadedPath}'.");
            }

            _configuredPath = fullPath;
        }
    }

    /// <summary>Creates the archive handler of a format and returns the requested interface of it.</summary>
    /// <exception cref="SevenZipLibraryException">The library is unavailable or does not support the format.</exception>
    internal static TInterface CreateArchiveHandler<TInterface>(byte formatId, string formatName)
        where TInterface : class
    {
        delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int> createObject = EnsureLoaded();

        Guid classId = ArchiveFormats.GetClassId(formatId);
        Guid interfaceId = typeof(TInterface).GUID;
        nint instance = 0;
        int result = createObject(&classId, &interfaceId, &instance);
        if (result != 0 || instance == 0)
        {
            throw new SevenZipLibraryException(
                $"The 7-Zip library '{_loadedPath}' does not support the {formatName} format (HRESULT 0x{result:X8}).");
        }

        try
        {
            return (TInterface)ComWrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            // The wrapper holds its own reference.
            Marshal.Release(instance);
        }
    }

    /// <summary>Releases a native object immediately instead of waiting for the garbage collector.</summary>
    internal static void Release(object? comObject)
    {
        if (comObject is ComObject wrapper)
        {
            wrapper.FinalRelease();
        }
    }

    private static delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int> EnsureLoaded()
    {
        lock (Gate)
        {
            if (_createObject is not null)
            {
                return _createObject;
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new SevenZipLibraryException("The 7-Zip engine (7z.dll) is only available on Windows.");
            }

            string path = ResolveLibraryPath();
            try
            {
                nint handle = NativeLibrary.Load(path);
                _createObject = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(handle, "CreateObject");
                _loadedPath = path;
                return _createObject;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                throw new SevenZipLibraryException(
                    $"'{path}' could not be loaded as a {RuntimeInformation.ProcessArchitecture} 7-Zip library.", ex);
            }
        }
    }

    private static string ResolveLibraryPath()
    {
        if (_configuredPath is not null)
        {
            return _configuredPath;
        }

        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string?[] roots = [AppContext.BaseDirectory, Path.GetDirectoryName(typeof(SevenZipLibrary).Assembly.Location)];

        var candidates = new List<string>();
        foreach (string? root in roots)
        {
            if (!string.IsNullOrEmpty(root))
            {
                candidates.Add(Path.Combine(root, architecture, LibraryFileName));
                candidates.Add(Path.Combine(root, LibraryFileName));
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0)
        {
            candidates.Add(Path.Combine(programFiles, "7-Zip", LibraryFileName));
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new SevenZipLibraryException(
                $"The 7-Zip library was not found. Searched: {string.Join("; ", candidates.Distinct())}. " +
                $"Deploy {LibraryFileName} with the application or call {nameof(SevenZipLibrary)}.{nameof(SetLibraryPath)}.");
    }
}
