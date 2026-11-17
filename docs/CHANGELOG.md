# Changelog

All notable changes to ZipPack are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- The installer associates `.7z` and `.zst` with ZipPack (double-click opens the archive) and adds the Explorer
  menu; updates keep both current; uninstalling removes them and restores the previous owner. Switchable in
  Settings.
- `build-release.ps1`: tests, self-contained win-x64 publish and Velopack packaging in one command.
- Double-click (or Enter, or *Open*) on a file inside an open archive extracts just that file to a temporary
  folder and opens it with its program; a folder opens in Explorer. Programs and scripts ask first, and the
  archive's "downloaded from the internet" mark is kept.
- `ArchiveExtractRequest.EntryPaths` extracts only selected entries (7-Zip formats and Zstandard).

### Fixed

- "Compress with ZipPack" / "Extract with ZipPack" from the Explorer menu crashed silently while the ZipPack
  window was open.

### Changed

- Faster 7z compression: the LZMA2 block size is chosen per level (Low 1 MiB … Ultra 16 MiB) so every core
  works. Measured 3.8× faster on 399 MiB with 12 cores, for a 2.2% larger archive.
- Faster Zstandard compression on all cores (independent frames, like `zstd -T0`): `.zst` 3×, `.tar.zst` 2.2×,
  Ultra about 5× faster, at the same size. `ThreadCount = 1` restores single-threaded compression.
- Extraction reserves each file's size in advance and restores timestamps and attributes without reopening
  the file.

## [1.0.0] — 2026-11-17

### Added

- Create 7z, ZIP, Zstandard (`.zst`, `.tar.zst`), tar, gzip, bzip2 and xz archives; extract every format
  7-Zip reads; list and test archives.
- AES-256 encryption for 7z and ZIP (optionally with encrypted file names) and AES-256-GCM for Zstandard.
- Image compression with Magick.NET: re-encode, convert to JPEG, PNG, WebP, AVIF, JPEG XL, GIF, TIFF or
  BMP, shrink, remove metadata; batches; files are never made larger.
- "Delete files after compression": the archive is tested, then the originals go to the Recycle Bin.
- Progress on the Windows taskbar button.
- Explorer right-click menu for the current user; drag and drop; command line.
- Installer and automatic updates with Velopack; the version in the status bar checks for updates.

[Unreleased]: https://github.com/kyawthuhsan-astro/ZipPack/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/kyawthuhsan-astro/ZipPack/releases/tag/v1.0.0
