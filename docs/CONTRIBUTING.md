# Contributing to ZipPack

Thank you for helping! This page explains how to report problems, propose changes and get a pull request
merged.

## Reporting a bug or asking for a feature

- Search the [existing issues](https://github.com/kyawthuhsan-astro/ZipPack/issues) first.
- Use the **Bug report** or **Feature request** template. For bugs, include the ZipPack version (bottom
  right of the window), your Windows version, the exact steps, and — if you can — a small file that shows
  the problem. Never attach files containing personal data or passwords.
- Security problems are **not** reported in public issues; see [SECURITY.md](SECURITY.md).

## Development setup

1. Install the [.NET 11 SDK](https://dotnet.microsoft.com/download) on Windows (Visual Studio 2026 is optional).
2. Build and test:

   ```text
   dotnet build ZipPack.slnx
   dotnet test --project ZipPackTest\ZipPackTest.csproj
   ```

3. Read [ARCHITECTURE.md](ARCHITECTURE.md). Its rules are what reviews check against.

## Rules every change follows

- **A `*Core` library never references `ZipPack`, the application or another core.** New engines follow the
  "Conventions every core follows" section of the architecture document.
- **0 warnings.** The solution builds without warnings; analyzers are part of the build.
- **Tests.** Every behaviour change comes with tests in `ZipPackTest`. Generate test data in code — no
  binary test assets.
- **Style.** Nullable enabled, file-scoped namespaces, `sealed` by default, XML documentation on public
  API, guard clauses with `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrEmpty`.
- **Licences.** Code in `SevenZipCore/` is LGPL-3.0-or-later; everything else is MIT. Do not copy code from
  projects with incompatible licences, and credit any code you adapt in the relevant `NOTICE.md` and in
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). New NuGet packages need a reason in the pull request.

## Pull requests

1. Fork the repository and create a branch from `main` (`fix/…`, `feature/…`).
2. Keep a pull request to one topic; describe *what* changed and *why*.
3. Make sure `dotnet build` (0 warnings) and `dotnet test` pass — CI runs both.
4. Add a line under **Unreleased** in [CHANGELOG.md](CHANGELOG.md) for user-visible changes.

By submitting a pull request you agree that your contribution is licensed under the licence of the files
you change (MIT, or LGPL-3.0-or-later inside `SevenZipCore/`).

## Releasing (maintainers)

The full checklist is in [RELEASING.md](RELEASING.md); in short:

1. Raise `<Version>` in `ZipPackNet/ZipPackNet.csproj` and move the **Unreleased** changelog entries under
   the new version.
2. Optionally try it locally with `.\build-release.ps1` (output in `Releases\`).
3. Push a tag `vX.Y.Z`. The [release workflow](../.github/workflows/release.yml) builds, packs with Velopack and
   publishes the GitHub release that installed copies update from.
