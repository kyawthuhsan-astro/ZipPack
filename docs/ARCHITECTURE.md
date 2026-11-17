# ZipPack — project structure

ZipPack is a .NET 11 compression toolkit made of small, independently usable libraries, one
orchestration library that unifies them, a WinForms application and one test project.

## The one rule

> **A `*Core` library never references `ZipPack`, the application, or another `*Core` library.**

Each core is a finished product on its own. That is what lets another solution take *one* feature
(say, Zstandard) by referencing *one* project, without pulling in 7-Zip, ImageMagick or any UI.
`ZipPack` is the only place where the cores meet.

## Dependency graph

```
                    ┌───────────────┐
                    │  ZipPackNet   │  WinForms app (UI, shell integration, CLI)
                    └───────┬───────┘
                            │
                    ┌───────▼───────┐
                    │    ZipPack    │  interfaces + strategies + CompressionService
                    └─┬─────┬─────┬─┘
          ┌───────────┘     │     └───────────┐
┌─────────▼────────┐ ┌──────▼──────┐ ┌────────▼────────┐
│   SevenZipCore   │ │  ZstdCore   │ │  MagickNetCore  │   ← no references between these,
│  (7z.dll, COM)   │ │ (BCL only)  │ │  (Magick.NET)   │     and none upwards
└──────────────────┘ └─────────────┘ └─────────────────┘

ZipPackTest ──► ZipPackNet ──► ZipPack ──► all cores   (tests everything)
```

## Projects

| Project | Target | Depends on | What it is |
| --- | --- | --- | --- |
| `SevenZipCore` | `net11.0` | native `7z.dll` (shipped in `x64/`, `x86/`) | 7z, ZIP, tar, gzip, bzip2, xz creation; extraction of everything 7-Zip reads. Source-generated COM interop. **Windows only.** |
| `ZstdCore` | `net11.0` | nothing (BCL `ZstandardStream`, `System.Formats.Tar`) | `.zst` / `.tar.zst` with optional AES-256-GCM password container. Cross-platform. |
| `MagickNetCore` | `net11.0` | NuGet `Magick.NET-Q8-AnyCPU` 15.1.0 (native ImageMagick for win/linux/osx x64, arm64, x86) | Image compression: re-encode, convert (JPEG, PNG, WebP, AVIF, JPEG XL, GIF, TIFF, BMP; HEIC read-only), strip metadata, downscale, batch. No Magick.NET type in its public API. |
| `ZipPack` | `net11.0` | every core | Engine-independent interfaces, one strategy per format, `CompressionService`. |
| `ZipPackNet` | `net11.0-windows` | `ZipPack` | The desktop application. |
| `ZipPackTest` | `net11.0-windows` | `ZipPackNet` (and transitively everything) | xUnit v3 on Microsoft.Testing.Platform. |

### Folder layout

```
SevenZipCore/
  SevenZipCompressor.cs, SevenZipExtractor.cs      public API
  SevenZipCompressionOptions.cs, ArchiveEntry.cs    public models
  ArchiveFormats.cs, ArchiveFormatDetector.cs       formats and signature detection
  SevenZipLibrary.cs                                locates/loads 7z.dll
  SevenZipExceptions.cs
  Interop/                                          COM interfaces, callbacks, stream wrappers (internal)
  Internal/                                         item collection, extract sinks (internal)
  x64/7z.dll, x86/7z.dll, NOTICE.md

ZstdCore/
  ZstdCompressor.cs, ZstdExtractor.cs               public API
  ZstdCompressionOptions.cs, ZstdFormat.cs, ZstdExceptions.cs
  Internal/                                         encryption container, stream helpers (internal)

MagickNetCore/
  ImageCompressor.cs                                public API (file, stream, batch, info, detection)
  ImageCompressionOptions.cs                        options + ImageInfo, ImageCompressionResult, ImageProgress
  ImageFormat.cs, ImageExceptions.cs
  Internal/                                         ImagePipeline (all Magick.NET calls), ImageFormats
                                                    (signatures, format map), MagickEnvironment (resource limits)
  NOTICE.md

ZipPack/
  Abstractions/   ICompressionStrategy, IArchiver, IStreamCompressor, IMagickNetCore, ICompressionService
  Models/         CompressionOptions, ArchiveCreateRequest, ArchiveExtractRequest, ArchiveEntryInfo, CompressionProgress,
                  ImageCompressOptions, ImageCompressRequest, ImageBatchCompressRequest, ImageCompressResult
  Exceptions/     CompressionException, InvalidPasswordException, UnsupportedFormatException
  Strategies/     SevenZipArchiver, ZstdStrategy, MagickImageCompressor (adapters over the cores)
  CompressionService.cs

ZipPackNet/
  Program.cs      command line routing
  Cli/            CommandLine, LaunchBatcher (merges Explorer multi-selection launches)
  Shell/          ShellIntegration (HKCU registry context menu), TaskbarProgress (ITaskbarList3)
  Services/       AppSettings, ErrorPresenter, UpdateService (Velopack), SourceCleanup (delete after compression)
  UI/             MainForm, dialogs, ArchiveWorkflows, ImageWorkflow + ImageCompressDialog,
                  UpdateWorkflow, FileListView, UiKit
  Assets/         app.ico, make-icon.ps1
```

## Using one feature in another solution

Reference the core directly. Do **not** reference `ZipPack` unless you want every engine.

| You want | Reference | What comes with it | Notes |
| --- | --- | --- | --- |
| Zstandard only | `ZstdCore` | nothing | Pure managed, any OS .NET 11 runs on. |
| 7-Zip formats only | `SevenZipCore` | `x64\7z.dll`, `x86\7z.dll` copied to your output | Windows only. LGPL — see `SevenZipCore/NOTICE.md`. |
| Images only | `MagickNetCore` | Magick.NET and its native ImageMagick libraries (`runtimes/<rid>/native`) | Any OS Magick.NET supports. Apache-2.0 / ImageMagick licence — see `MagickNetCore/NOTICE.md`. Sets process-wide ImageMagick resource limits once (only lowers them). |
| One API over everything | `ZipPack` | all of the above | Use `CompressionService.CreateDefault()`. |

```csharp
// Zstandard alone — no ZipPack involved.
var compressor = new ZstdCore.ZstdCompressor(new ZstdCore.ZstdCompressionOptions { Quality = 9, Password = "…" });
await compressor.CompressDirectoryAsync(@"C:\data", @"C:\backup\data.tar.zst");

// 7-Zip alone.
var sevenZip = new SevenZipCore.SevenZipCompressor(new SevenZipCore.SevenZipCompressionOptions { Password = "…", EncryptHeaders = true });
await sevenZip.CompressDirectoryAsync(@"C:\data", @"C:\backup\data.7z");
using var archive = SevenZipCore.SevenZipExtractor.Open(@"C:\backup\data.7z", "…");
await archive.ExtractArchiveAsync(@"C:\restore");

// Images alone — no "using ImageMagick;" needed.
var images = new MagickNetCore.ImageCompressor(new MagickNetCore.ImageCompressionOptions
{
    OutputFormat = MagickNetCore.ImageFormat.WebP, Quality = 75, MaxWidth = 1920,
});
await images.CompressFileAsync(@"C:\photos\holiday.jpg", @"C:\web\holiday.webp");
IReadOnlyList<MagickNetCore.ImageCompressionResult> results =
    await images.CompressFilesAsync(Directory.EnumerateFiles(@"C:\photos"), @"C:\web");   // failures are in result.Error
```

## Conventions every core follows

New cores must follow these so they feel like one family and adapt cleanly into `ZipPack`.

- **Self-contained.** No reference to `ZipPack`. Own options, own progress type, own exception hierarchy.
- **Options are an immutable `sealed record`** with `init` properties and a static `Default`; the worker
  class takes it in the constructor and is thread-safe.
- **Async surface:** every long operation takes `IProgress<TProgress>? progress = null` and
  `CancellationToken cancellationToken = default`, in that order, last.
- **Honest async:** truly asynchronous where the engine is (`ZstdCore`); `Task.Run` around a
  synchronous native engine (`SevenZipCore`), with the token checked inside callbacks.
- **No half-written output.** A failed or cancelled operation deletes the file it was writing.
- **Path safety.** Anything that writes entries from an archive validates the target stays inside the
  destination (`NormalizeRoot` + `ResolveSafePath`), including drive-root destinations such as `D:\`.
- **Exceptions:** one base exception per core (`SevenZipException`, `ZstdException`, `ImageException`) plus
  specific subclasses (`…PasswordException`, `UnsupportedImageFormatException`). I/O and argument exceptions
  are left as they are.
- **Public API has XML documentation**; `GenerateDocumentationFile` is on. Internal helpers live in
  `Internal/` or `Interop/`. `InternalsVisibleTo` is granted to `ZipPackTest` only.
- **Style:** nullable enabled, file-scoped namespaces, `sealed` by default, collection expressions,
  `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrEmpty` guards.

## How `ZipPack` unifies the cores (Strategy pattern)

```
ICompressionStrategy            Id, DisplayName, FileExtensions
 ├─ IArchiver                   Create / Extract / ListEntries / Test / CanOpen + Capabilities
 ├─ IStreamCompressor           stream → stream
 ├─ IMagickNetCore              image file → smaller image file (single + batch), CanCompress
 └─ (future) IVideoCompressor …
```

The image strategy (`id = "image"`) is registered in `DefaultStrategies` but is not an `IArchiver`, so
`GetArchiverForNewArchive` / `ResolveArchiverAsync` never select it. Reach it with
`service.GetStrategy<IMagickNetCore>("image")`.

- A **strategy** is a thin adapter in `ZipPack/Strategies` that maps the engine-independent models to
  one core's options, relays progress, and translates the core's exceptions into
  `CompressionException` / `InvalidPasswordException` / `UnsupportedFormatException`.
- `CompressionService` is an immutable registry: it picks an archiver by **file extension** for new
  archives and by **file content** for existing ones, and exposes `GetStrategies<T>()` /
  `GetStrategy<T>(id)` for every kind of strategy.

### Adding a new module (Open/Closed)

1. Create `XyzCore` following the conventions above. It references nothing in this solution.
2. If it is a new *kind* of work, add an interface in `ZipPack/Abstractions` deriving from
   `ICompressionStrategy`, plus its request/result models in `ZipPack/Models`.
3. Add an adapter in `ZipPack/Strategies` and append it to `CompressionService.DefaultStrategies`.
4. Add tests to `ZipPackTest`: the core directly, and through `ICompressionService`.

No existing interface, strategy or the service's logic changes.

## Application (`ZipPackNet`)

| Command line | Effect |
| --- | --- |
| `ZipPackNet [path …]` | Main window; a single archive is opened, anything else is queued. |
| `ZipPackNet --compress <path> …` | Options dialog → progress → exit. Used by the Explorer menu. |
| `ZipPackNet --extract <archive> …` | Destination dialog → progress → exit. Used by the Explorer menu. |
| `ZipPackNet --register-shell` / `--unregister-shell` | Adds/removes the per-user Explorer menu entries. |

The UI never calls a core directly; it only talks to `ICompressionService`. `UI/ArchiveWorkflows` holds the
compress / extract / test flows shared by the main window and the shell launches.

**Images** (tool bar button, Ctrl+I, or "Compress images…" in the list's context menu) runs `UI/ImageWorkflow`
over the queued items: folders are searched for images, `ImageCompressDialog` asks for format, quality, a
size limit, metadata removal and the destination (next to the originals, or one folder), and the work runs
through `IMagickNetCore` behind the progress dialog.

**Delete files after compression** (a check box in the compress dialog, never remembered) first tests the new
archive; only when it is intact are the sources moved to the Recycle Bin (`Services/SourceCleanup`). A source
folder that contains the archive is kept, and anything that cannot be removed is reported.

**Taskbar progress.** `ProgressDialog` shows every operation's progress on the taskbar button of the main
window (or of the dialog itself for Explorer launches): a pulsing bar until the size is known, then a green
bar, yellow while cancelling, cleared when done. `Shell/TaskbarProgress` calls the shell's `ITaskbarList3`
through source-generated COM interop — the interface the Windows API Code Pack wraps, without its
.NET Framework packages.

### Version and updates (Velopack)

The version label in the status bar (`v1.0.0`, from `<Version>` in `ZipPackNet.csproj`) is also the
"check for updates" link. At start-up ZipPack checks quietly in the background (Settings → Updates can turn
this off) and, when a newer release exists, the label changes to "Update to vX". Nothing is downloaded until
the user agrees; then `UpdateService` downloads it and `ApplyUpdatesAndRestart` installs it.

The release feed is the `<UpdateFeedUrl>` property in `ZipPackNet.csproj` (empty = updates disabled): a GitHub
repository URL (GitHub Releases), a web folder, or a local/network folder. Updates only work in a copy
installed by the Velopack `Setup.exe`; a debug build reports that updates are unavailable.

A release is built with `build-release.ps1` at the repository root. It reads `<Version>` (the only place the
version is written; `AssemblyVersion` / `FileVersion` follow it), runs the tests, publishes self-contained for
win-x64 into `ZipPackNet\bin\Release\net11.0-windows\publish\win-x64`, checks that `x64\7z.dll` is there, and
runs `vpk pack --packId ZipPackNet` into `Releases\` (`ZipPackNet-win-Setup.exe` plus a copy named
`ZipPackNet-<Version>-win-Setup.exe`, the `.nupkg` packages and `releases.win.json`):

```
.\build-release.ps1 -UpdateFeedUrl https://github.com/<owner>/ZipPack
vpk upload github --repoUrl https://github.com/<owner>/ZipPack --publish --releaseName "ZipPack 1.0.0" --tag v1.0.0 -o Releases
```

Never change the package id `ZipPackNet` once a release is out: installed copies only update from packages
with their own id. On GitHub the whole sequence is automated by `.github/workflows/release.yml`: pushing a tag
`vX.Y.Z` (which must equal `<Version>`) downloads the previous release for delta updates, runs
`build-release.ps1` with the repository as the feed and uploads the GitHub release.

### Installer hooks (Explorer menu and file associations)

`Program.Main` registers Velopack's fast callbacks before anything else (`Shell/InstallerHooks`):

| Hook | What happens |
| --- | --- |
| `OnAfterInstallFastCallback` | Adds the Explorer menu entries and associates `.7z` / `.zst` (`ZipPackNet.Archive.7z`, `ZipPackNet.Archive.zst`) |
| `OnAfterUpdateFastCallback` | Rewrites whatever is registered (new extensions, new path); entries the user removed stay removed |
| `OnBeforeUninstallFastCallback` | Removes everything; `.7z` / `.zst` go back to the ProgID that owned them before |

Everything lives under `HKCU\Software\Classes` (no administrator rights) and every command uses
`Environment.ProcessPath`. Double-click runs `ZipPackNet.exe "%1"`, which `CommandLine` routes to the main
window (an archive opens, anything else is queued); the menu verbs pass `--compress` / `--extract`.
`SHChangeNotify(SHCNE_ASSOCCHANGED)` makes Explorer pick up icons and verbs at once. A user's own
"Always use this app" choice (`UserChoice`) still takes precedence. Settings can switch the menu and the
associations off; `--register-shell` / `--unregister-shell` do both from the command line. `.github/workflows/ci.yml` builds with `-warnaserror` and runs the tests on every
push and pull request.

Open-source files: `LICENSE` at the repository root (MIT, except `SevenZipCore/` which is LGPL-3.0-or-later —
see `SevenZipCore/LICENSE.md`); `README.md`, `THIRD-PARTY-NOTICES.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`,
`SECURITY.md` and `CHANGELOG.md` in `docs/` (GitHub also finds them there); issue / pull-request templates in
`.github/`. Each library keeps its own licence notice next to its code (`SevenZipCore/NOTICE.md`,
`SevenZipCore/LICENSE.md`, `MagickNetCore/NOTICE.md`).

## Build and test

```
dotnet build ZipPack.slnx
dotnet test --project ZipPackTest\ZipPackTest.csproj
dotnet run --project ZipPackNet
```

## Third-party code and licences

| Component | Used by | Licence |
| --- | --- | --- |
| SharpSevenZip (adapted interop definitions) | `SevenZipCore` | LGPL-3.0-or-later |
| 7-Zip `7z.dll` 26.03 | `SevenZipCore` | LGPL (+ unRAR restriction, BSD parts) |
| Magick.NET 15.1.0 (`Magick.NET-Q8-AnyCPU`) / ImageMagick | `MagickNetCore` | Apache-2.0 / ImageMagick licence |
| Velopack 1.2.161 | `ZipPackNet` | MIT |
| xUnit v3 | `ZipPackTest` | Apache-2.0 |
