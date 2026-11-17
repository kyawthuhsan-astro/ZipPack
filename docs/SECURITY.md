# Security policy

ZipPack opens files from untrusted sources (downloaded archives and images), so security reports are taken
seriously.

## Supported versions

Only the latest release receives fixes. Installed copies update themselves; click the version in the
status bar to check.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on the repository page.

Include the ZipPack version, what an attacker can achieve, and the smallest file or steps that reproduce
it. You will get an answer within a week; once a fix is released you will be credited in the release notes
unless you prefer otherwise.

## Scope

In scope: anything in this repository, for example writing outside the extraction folder ("zip slip"),
crashes or memory exhaustion from a crafted archive or image, password handling, the update process and
the Explorer integration.

Out of scope: vulnerabilities in 7-Zip, ImageMagick or Velopack themselves — report those upstream
([7-Zip](https://www.7-zip.org), [ImageMagick](https://github.com/ImageMagick/ImageMagick/security),
[Velopack](https://github.com/velopack/velopack/security)); we update the bundled versions once a fix exists.
