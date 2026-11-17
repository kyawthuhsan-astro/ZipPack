# SevenZipCore licence

SPDX-License-Identifier: `LGPL-3.0-or-later`

`SevenZipCore` is free software: you can redistribute it and/or modify it under the terms of the
**GNU Lesser General Public License** as published by the Free Software Foundation, either version 3 of
the License, or (at your option) any later version.

`SevenZipCore` is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even
the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General
Public License for more details.

- GNU LGPL v3.0: https://www.gnu.org/licenses/lgpl-3.0.html
- GNU GPL v3.0 (which the LGPL builds on): https://www.gnu.org/licenses/gpl-3.0.html

## Why this folder is not MIT

The COM interface definitions, property and format identifiers and signature tables in `Interop/` and
`ArchiveFormats.cs` are adapted from [SharpSevenZip](https://github.com/JeremyAnsel/SharpSevenZip)
(LGPL-3.0), itself derived from [SevenZipSharp](https://github.com/squid-box/SevenZipSharp). As a
derivative work, `SevenZipCore` keeps that licence. See [NOTICE.md](NOTICE.md) for the full attribution.

## What this means for you

- You may use `SevenZipCore.dll` from closed-source or MIT/Apache software, as the rest of ZipPack does,
  as long as it stays a separate library that users can replace with their own build.
- If you distribute a **modified** `SevenZipCore`, you must publish the source of your modifications under
  the LGPL as well.
- The bundled `x64/7z.dll` and `x86/7z.dll` are 7-Zip binaries under the GNU LGPL; the RAR decoder inside
  them carries the unRAR restriction: it may not be used to develop a RAR-compatible archiver.
  See https://www.7-zip.org/license.txt.
