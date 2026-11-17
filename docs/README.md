# ZipPack

<!-- Replace kyawthuhsan-astro with your GitHub user or organisation once the repository is published. -->
[![CI](https://github.com/kyawthuhsan-astro/ZipPack/actions/workflows/ci.yml/badge.svg)](https://github.com/kyawthuhsan-astro/ZipPack/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/kyawthuhsan-astro/ZipPack)](https://github.com/kyawthuhsan-astro/ZipPack/releases/latest)
[![Licence: MIT + LGPL](https://img.shields.io/badge/licence-MIT%20%2B%20LGPL--3.0%20(SevenZipCore)-blue)](../LICENSE)

**ZipPack** is a free, open-source file compressor for Windows, written in C# on .NET 11. It creates and
extracts 7z, ZIP, Zstandard, tar, gzip, bzip2 and xz archives, opens RAR, ISO, CAB and the other formats
7-Zip reads, and makes images smaller — with a clean WinForms interface, an Explorer right-click menu and
automatic updates.

Every engine is a small, self-contained library, so you can also use just the Zstandard, 7-Zip or image
part in your own .NET project.

<!-- Add a screenshot: docs/images/main-window.png -->

## Features

- **Archives** — create 7z, ZIP, `.tar.zst` / `.zst`, tar, gzip, bzip2 and xz; extract everything 7-Zip
  reads (RAR, ISO, CAB, WIM, …); list and test archives.
- **Encryption** — AES-256 for 7z and ZIP (optionally with encrypted file names); AES-256-GCM for Zstandard.
- **Image compression** — re-encode, convert (JPEG, PNG, WebP, AVIF, JPEG XL, GIF, TIFF, BMP; HEIC
  read-only), shrink, and remove metadata, in batches. A file is never made larger.
- **Delete files after compression** — the archive is tested first; only then are the originals moved to the
  Recycle Bin.
- **Windows integration** — Explorer "Compress / Extract with ZipPack" menu (per user, no admin rights),
  drag and drop, progress on the taskbar button.
- **Automatic updates** — installed with [Velopack](https://velopack.io); click the version in the status
  bar to check for a new release.
- **Safe by design** — no half-written output, protection against path traversal ("zip slip") and
  decompression bombs.

## Download and install

1. Open the [latest release](https://github.com/kyawthuhsan-astro/ZipPack/releases/latest).
2. Download `ZipPackNet-win-Setup.exe` and run it. No administrator rights are needed; ZipPack installs for
   the current user and updates itself.
3. The installer adds the Explorer menu entries and opens `.7z` / `.zst` files with ZipPack on double-click;
   both can be switched off in *Settings*. Uninstalling removes them and gives `.7z` / `.zst` back to the
   program that had them before.

Requirements: Windows 10 or 11 (x64). The installer is self-contained; no separate .NET installation is
needed.

## Using ZipPack

Drop files or folders on the window and choose **Compress**, or drop a single archive to look inside.
**Images** compresses the pictures among the listed items. From a command prompt:

```text
ZipPackNet [path ...]                      Open the window (an archive is opened, anything else is queued)
ZipPackNet --compress <path> [path ...]    Ask for options, compress, exit
ZipPackNet --extract <archive> [...]       Ask for a destination, extract, exit
ZipPackNet --register-shell                Add the Explorer menu entries
ZipPackNet --unregister-shell              Remove them
```

## Using the libraries in your own project

| You want | Reference | Licence |
| --- | --- | --- |
| Zstandard (+ AES-256-GCM) | `ZstdCore` | MIT |
| 7-Zip formats | `SevenZipCore` (+ `7z.dll`) | LGPL-3.0-or-later |
| Image compression | `MagickNetCore` (+ Magick.NET) | MIT (Magick.NET: Apache-2.0) |
| One API over all engines | `ZipPack` | MIT (+ the licences of the engines) |

```csharp
// Zstandard
await new ZstdCore.ZstdCompressor(new() { Quality = 9 }).CompressDirectoryAsync(@"C:\data", @"C:\backup\data.tar.zst");

// 7-Zip
await new SevenZipCore.SevenZipCompressor(new() { Password = "…", EncryptHeaders = true }).CompressDirectoryAsync(@"C:\data", @"C:\backup\data.7z");

// Images
await new MagickNetCore.ImageCompressor(new() { OutputFormat = MagickNetCore.ImageFormat.WebP, MaxWidth = 1920 })
    .CompressFileAsync(@"C:\photos\holiday.jpg", @"C:\web\holiday.webp");
```

The design, conventions and the full project layout are described in [ARCHITECTURE.md](ARCHITECTURE.md).

## Building from source

Requirements: Windows, the [.NET 11 SDK](https://dotnet.microsoft.com/download), optionally Visual Studio 2026.

```text
git clone https://github.com/kyawthuhsan-astro/ZipPack.git
cd ZipPack
dotnet build ZipPack.slnx
dotnet test --project ZipPackTest\ZipPackTest.csproj
dotnet run --project ZipPackNet
```

The build must finish with 0 warnings. Releases are packaged with Velopack; the step-by-step checklist is
[RELEASING.md](RELEASING.md) (see also [ARCHITECTURE.md](ARCHITECTURE.md#version-and-updates-velopack) and the
[release workflow](../.github/workflows/release.yml)).

## Project layout

| Folder | What it is |
| --- | --- |
| `SevenZipCore/` | 7-Zip engine: source-generated COM interop over `7z.dll` (Windows) |
| `ZstdCore/` | Zstandard engine on the .NET built-in `ZstandardStream` (cross-platform) |
| `MagickNetCore/` | Image engine on Magick.NET / ImageMagick |
| `ZipPack/` | Engine-independent interfaces, one strategy per format, `CompressionService` |
| `ZipPackNet/` | The Windows application |
| `ZipPackTest/` | xUnit v3 tests for everything above |

## Contributing

Bug reports, ideas and pull requests are welcome — please read [CONTRIBUTING.md](CONTRIBUTING.md) first and
follow the [Code of Conduct](CODE_OF_CONDUCT.md). Security problems: see [SECURITY.md](SECURITY.md).

## Licence

ZipPack is released under the [MIT licence](../LICENSE), **except** `SevenZipCore/`, which is
[LGPL-3.0-or-later](../SevenZipCore/LICENSE.md) because it is derived from SharpSevenZip, and the bundled
`7z.dll`, which is 7-Zip under the GNU LGPL with the unRAR restriction.

## Credits

ZipPack would not exist without:

- **[7-Zip](https://www.7-zip.org)** by Igor Pavlov — the archive engine behind every 7-Zip format.
- **[SharpSevenZip](https://github.com/JeremyAnsel/SharpSevenZip)** by Jérémy Ansel and
  **[SevenZipSharp](https://github.com/squid-box/SevenZipSharp)** by Markovtsev Vadim — the 7-Zip COM
  interop definitions `SevenZipCore` is adapted from.
- **[Magick.NET](https://github.com/dlemstra/Magick.NET)** by Dirk Lemstra and
  **[ImageMagick](https://imagemagick.org)** — image decoding, processing and encoding.
- **[Velopack](https://github.com/velopack/velopack)** — installer and automatic updates.
- **[.NET](https://dotnet.microsoft.com)** and **[xUnit.net](https://xunit.net)**.

All third-party components and their licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
