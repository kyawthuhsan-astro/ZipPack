using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace ZipPackNet.Services;

/// <summary>What an update check found.</summary>
internal enum UpdateCheckStatus
{
    /// <summary>No feed is configured, or the app runs from a build folder rather than a Velopack install.</summary>
    Unavailable,

    /// <summary>The installed version is the newest one.</summary>
    UpToDate,

    /// <summary>A newer version can be downloaded.</summary>
    UpdateAvailable,

    /// <summary>An update was already downloaded and is applied on restart.</summary>
    PendingRestart,
}

/// <summary>The outcome of <see cref="UpdateService.CheckAsync"/>.</summary>
/// <param name="Status">What was found.</param>
/// <param name="NewVersion">The version that can be installed, for <see cref="UpdateCheckStatus.UpdateAvailable"/> and <see cref="UpdateCheckStatus.PendingRestart"/>.</param>
/// <param name="ReleaseNotes">The release notes (Markdown) of <paramref name="NewVersion"/>, when published.</param>
/// <param name="Reason">Why updates are unavailable, for <see cref="UpdateCheckStatus.Unavailable"/>.</param>
internal sealed record UpdateCheckResult(UpdateCheckStatus Status, string? NewVersion = null, string? ReleaseNotes = null, string? Reason = null)
{
    internal UpdateInfo? Info { get; init; }

    internal VelopackAsset? PendingAsset { get; init; }
}

/// <summary>
/// Velopack auto-update: finds, downloads and applies new releases. The feed is set at build time with the
/// <c>UpdateFeedUrl</c> MSBuild property (see <c>ZipPackNet.csproj</c>).
/// </summary>
/// <remarks>
/// Updates only work in a copy installed by the Velopack <c>Setup.exe</c>; a build run from Visual Studio or a
/// plain publish folder reports <see cref="UpdateCheckStatus.Unavailable"/>. Nothing is downloaded or installed
/// without the user agreeing first — the UI decides when to call <see cref="DownloadAsync"/>.
/// </remarks>
internal sealed class UpdateService
{
    private const string NotInstalledReason =
        "Automatic updates work in the installed copy of ZipPack (installed with Setup.exe), not in a development build.";

    private readonly UpdateManager? _manager;

    public UpdateService(string? feed)
    {
        Feed = string.IsNullOrWhiteSpace(feed) ? null : feed.Trim();
        if (Feed is not null)
        {
            try
            {
                _manager = new UpdateManager(CreateSource(Feed));
            }
            catch (InvalidOperationException)
            {
                // Velopack finds its installation through VelopackApp.Build().Run() (first line of Main);
                // without it — tests, tools hosting this assembly — there is no installation to update.
                UnavailableReason = NotInstalledReason;
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException)
            {
                UnavailableReason = $"The update feed '{Feed}' is not valid: {ex.Message}";
            }
        }

        CurrentVersion = (_manager is { IsInstalled: true } ? _manager.CurrentVersion?.ToString() : null) ?? AssemblyVersion();
    }

    /// <summary>Gets the update feed configured in the project file, or <see langword="null"/>.</summary>
    public string? Feed { get; }

    /// <summary>Gets the running version without build metadata, e.g. <c>1.0.0</c>.</summary>
    public string CurrentVersion { get; }

    /// <summary>Gets the version as shown in the window, e.g. <c>v1.0.0</c>.</summary>
    public string DisplayVersion => "v" + CurrentVersion;

    /// <summary>Gets a value indicating whether update checks can run at all.</summary>
    public bool CanCheck => _manager is { IsInstalled: true };

    private string? UnavailableReason { get; }

    /// <summary>Creates the service for the feed compiled into this executable.</summary>
    public static UpdateService CreateDefault() =>
        new(typeof(UpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UpdateFeedUrl")?.Value);

    /// <summary>A GitHub repository uses GitHub Releases; anything else is a web URL or a folder holding the release feed.</summary>
    internal static IUpdateSource CreateSource(string feed)
    {
        if (Uri.TryCreate(feed, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https"
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new GithubSource(feed.TrimEnd('/'), accessToken: null, prerelease: false);
        }

        if (Uri.TryCreate(feed, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https")
        {
            return new SimpleWebSource(uri);
        }

        return new SimpleFileSource(new DirectoryInfo(feed));
    }

    /// <summary>Asks the feed whether a newer version exists. Network failures throw; the caller decides whether to show them.</summary>
    public async Task<UpdateCheckResult> CheckAsync()
    {
        if (_manager is null)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Unavailable,
                Reason: UnavailableReason ?? "No update feed is configured for this build of ZipPack.");
        }

        if (!_manager.IsInstalled)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, Reason: NotInstalledReason);
        }

        if (_manager.UpdatePendingRestart is { } pending)
        {
            return new UpdateCheckResult(UpdateCheckStatus.PendingRestart, pending.Version.ToString(), pending.NotesMarkdown) { PendingAsset = pending };
        }

        UpdateInfo? info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return info is null
            ? new UpdateCheckResult(UpdateCheckStatus.UpToDate)
            : new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                info.TargetFullRelease.Version.ToString(),
                info.TargetFullRelease.NotesMarkdown) { Info = info };
    }

    /// <summary>Downloads the update found by <see cref="CheckAsync"/>, reporting 0..100.</summary>
    public Task DownloadAsync(UpdateCheckResult update, Action<int> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (_manager is null || update.Info is null)
        {
            throw new InvalidOperationException("There is no update to download.");
        }

        return _manager.DownloadUpdatesAsync(update.Info, progress, cancellationToken);
    }

    /// <summary>Closes ZipPack, installs the downloaded update and starts the new version.</summary>
    public void ApplyAndRestart(UpdateCheckResult update)
    {
        ArgumentNullException.ThrowIfNull(update);
        VelopackAsset asset = update.PendingAsset ?? update.Info?.TargetFullRelease
            ?? throw new InvalidOperationException("There is no update to install.");
        _manager!.ApplyUpdatesAndRestart(asset);
    }

    /// <summary>Gets the informational version without the "+commit" suffix the SDK appends.</summary>
    private static string AssemblyVersion()
    {
        string? version = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return version is { Length: > 0 } ? version.Split('+')[0] : "0.0.0";
    }
}
