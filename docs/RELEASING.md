# Releasing ZipPack

Two checklists: one for **every new version**, one for **publishing the repository on GitHub the first time**.
Replace `kyawthuhsan-astro` with your GitHub user or organisation everywhere below.

## A. Every new version

ZipPack updates itself with [Velopack](https://velopack.io). Installed copies read the update feed (the GitHub
releases of the repository) and install any release with a **higher** version and the **same package id**
(`ZipPackNet`). Both rules are absolute: never reuse a version number, never change the package id.

1. **Write down the changes.** In `docs/CHANGELOG.md`, move the entries under *Unreleased* into a new section
   `## [1.1.0] — YYYY-MM-DD` and add the compare links at the bottom.

2. **Raise the version** in `ZipPackNet/ZipPackNet.csproj` — the only place it is written:

   ```xml
   <Version>1.1.0</Version>
   <AssemblyVersion>1.1.0.0</AssemblyVersion>
   <FileVersion>1.1.0.0</FileVersion>
   ```

   Semantic versioning: `1.0.1` for fixes, `1.1.0` for new features, `2.0.0` for breaking changes,
   `1.1.0-beta.1` for a test release.

3. **Build and test.**

   ```text
   dotnet build ZipPack.slnx -c Release -warnaserror
   dotnet test --project ZipPackTest\ZipPackTest.csproj -c Release
   ```

4. **Package locally (optional but recommended).**

   ```text
   .\build-release.ps1 -UpdateFeedUrl https://github.com/kyawthuhsan-astro/ZipPack
   ```

   Output in `Releases\`: `ZipPackNet-win-Setup.exe` (+ a copy with the version in the name),
   `ZipPackNet-<version>-full.nupkg`, `releases.win.json`, `ZipPackNet-win-Portable.zip`.
   Keep the previous release's files in `Releases\` (or run `vpk download github --repoUrl
   https://github.com/kyawthuhsan-astro/ZipPack -o Releases`) so a small delta update is built as well.

5. **Test the installer** on a clean Windows user account or virtual machine:
   - install with `ZipPackNet-win-Setup.exe`; start ZipPack from the Start menu;
   - drag files from Explorer onto the window; compress and extract; *Images*;
   - right-click a file → *Show more options* → *Compress with ZipPack*; right-click a `.7z` → *Extract with ZipPack*;
   - double-click a `.7z` / `.zst` file — it opens in ZipPack;
   - **update path:** with the previous version installed, click the version at the bottom right → it offers the
     new version → *Yes* → ZipPack restarts on the new version;
   - uninstall from *Settings → Apps*: the menu entries and associations disappear.

6. **Publish.** Commit, tag and push; GitHub Actions does the rest (`.github/workflows/release.yml`: checks the
   tag against `<Version>`, tests, packs with the repository as the update feed, uploads the release).

   ```text
   git add -A
   git commit -m "Release 1.1.0"
   git tag v1.1.0
   git push origin main --tags
   ```

   Without Actions, upload the local build instead (needs a token with *contents: write*):

   ```text
   vpk upload github --repoUrl https://github.com/kyawthuhsan-astro/ZipPack --publish --releaseName "ZipPack 1.1.0" --tag v1.1.0 --token <token> -o Releases
   ```

7. **Check** the release page (Setup, `.nupkg`, `releases.win.json` attached) and that an installed older copy
   shows *Update to v1.1.0*.

## B. Publishing the repository on GitHub (first time)

1. **Create the repository** on github.com: *New repository* → name `ZipPack`, **Public**, and *no* README,
   licence or `.gitignore` (the project has its own).

2. **Fill in the placeholders.** Replace `kyawthuhsan-astro` in `docs/README.md`, `docs/CONTRIBUTING.md` and
   `docs/CHANGELOG.md`. `UpdateFeedUrl` in the project file can stay empty: the release workflow passes the
   repository URL when it builds.

3. **Check what will be published.** `.gitignore` already leaves out `bin/`, `obj/`, `.vs/`, `Releases/` and
   `*.user` files. Decide whether `docs/prompts/` (the AI prompt used to build `MagickNetCore`) should be public;
   delete it or add it to `.gitignore` if not. Make sure no passwords, tokens or personal files are in the folder.
   `SevenZipCore/x64/7z.dll` and `x86/7z.dll` are committed on purpose (the LGPL allows redistribution).

4. **First commit and push.**

   ```text
   cd D:\Projects\ZipPack
   git init -b main
   git add -A
   git status                      # review the list before committing
   git commit -m "Initial import of ZipPack 1.0.0"
   git remote add origin https://github.com/kyawthuhsan-astro/ZipPack.git
   git push -u origin main
   ```

5. **Repository settings** on GitHub:
   - *Settings → Actions → General → Workflow permissions*: **Read and write permissions** (the release workflow
     creates releases with `GITHUB_TOKEN`).
   - *Settings → Code security*: enable **Private vulnerability reporting** (`SECURITY.md` points to it).
   - *Settings → Branches*: protect `main` and require the **CI** check before merging (optional).
   - *About* (gear icon on the main page): description, website, topics such as `compression`, `7zip`,
     `zstandard`, `winforms`, `dotnet`.
   - GitHub shows `docs/README.md` on the main page and links `docs/CONTRIBUTING.md`, `docs/SECURITY.md` and
     `docs/CODE_OF_CONDUCT.md` automatically.

6. **First release.** Delete any local test output (`Releases\`), then tag the current version:

   ```text
   git tag v1.0.0
   git push origin v1.0.0
   ```

   Watch *Actions → Release*. When it is green, the *Releases* page has `ZipPackNet-win-Setup.exe`. From now on,
   follow checklist A.

7. **Recommended later.**
   - **Code signing.** Without a signature Windows SmartScreen warns "Windows protected your PC" on new
     downloads. A code-signing certificate (or Azure Trusted Signing) plus `vpk pack --signParams …` removes that.
   - Distribution through `winget` once releases are stable.
