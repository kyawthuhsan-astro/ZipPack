# Third-party notices

## Magick.NET

`MagickNetCore` uses [Magick.NET](https://github.com/dlemstra/Magick.NET) by Dirk Lemstra (NuGet package
`Magick.NET-Q8-AnyCPU`, with `Magick.NET.Core`), licensed under the **Apache License 2.0**. The package is
referenced unmodified; no Magick.NET source code is copied into this library.
See https://github.com/dlemstra/Magick.NET/blob/main/License.txt.

## ImageMagick

The native `Magick.Native` libraries that ship inside the Magick.NET package (`runtimes/win-x64`,
`runtimes/win-x86`, `runtimes/win-arm64`, …) are builds of [ImageMagick](https://imagemagick.org),
Copyright (C) 1999 ImageMagick Studio LLC, licensed under the **ImageMagick License** (an Apache-2.0-style
licence). See https://imagemagick.org/script/license.php. ImageMagick itself bundles further delegate
libraries (libjpeg-turbo, libpng, libwebp, libheif/aom, libjxl, zlib, …) under their own permissive
licences; Magick.NET lists them in the package's notice files.
