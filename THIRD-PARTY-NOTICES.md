# Third-party notices

Filee is MIT-licensed. It uses and redistributes the following third-party software.
Each component remains under its own license.

## Libraries (NuGet)

| Component | License | Project |
|---|---|---|
| Avalonia UI | MIT | https://github.com/AvaloniaUI/Avalonia |
| NWayland (used by Avalonia.Wayland) | MIT | https://github.com/kekekeks/NWayland |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| SharpHook (libuiohook) | MIT (libuiohook: GPL-3.0 with linking exception / LGPL-3.0) | https://github.com/TolikPylypchuk/SharpHook |
| Magick.NET / ImageMagick | Apache-2.0 / ImageMagick License | https://github.com/dlemstra/Magick.NET |
| ImageMagick delegate libraries (libheif, libde265, libwebp, libjpeg-turbo, libpng, libtiff, zlib, …) | Various (LGPL-2.1+, BSD, zlib) | bundled inside Magick.NET |
| PDFsharp | MIT | https://github.com/empira/PDFsharp |
| PDFtoImage | MIT | https://github.com/sungaila/PDFtoImage |
| PDFium (bblanchon.PDFium) | BSD-3-Clause / Apache-2.0 | https://pdfium.googlesource.com/pdfium |
| SkiaSharp | MIT | https://github.com/mono/SkiaSharp |
| Svg.Skia (incl. Svg, ShimSkiaSharp, ExCSS; text shaping in `Filee.Engines/Vector/OutlinedTextPlayback.cs` adapted from it) | MIT | https://github.com/wieslawsoltes/Svg.Skia |
| HarfBuzzSharp | MIT | https://github.com/mono/SkiaSharp |
| SharpCompress (zstd decompression for engine downloads; RAR, 7z and TAR comics) | MIT | https://github.com/adamhathcock/sharpcompress |
| Unhwp | MIT | https://github.com/iyulab/unhwp |
| Markdig | BSD-2-Clause | https://github.com/xoofx/markdig |
| ExcelNumberFormat | MIT | https://github.com/andersnm/ExcelNumberFormat |
| ExcelDataReader | MIT | https://github.com/ExcelDataReader/ExcelDataReader |
| PdfPig (PDF text, layout and pictures) | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| MimeKitLite (e-mail / MIME parsing) | MIT | https://github.com/jstedfast/MimeKit |
| MsgReader (Outlook .msg reading) | MIT, Copyright (c) Kees van Spelde | https://github.com/Sicos1977/MSGReader |
| OpenMcdf (OLE compound files, used by MsgReader) | MPL-2.0 (source: the project page) | https://github.com/openmcdf/openmcdf |
| RtfPipe (RTF → HTML, used by MsgReader) | MIT, Copyright (c) 2018 Eric Domke | https://github.com/erdomke/RtfPipe |
| UTF.Unknown (charset detection, used by MsgReader) | MPL-1.1 (source: the project page) | https://github.com/CharsetDetector/UTF-unknown |
| Microsoft.Maui.Graphics, Microsoft.IO.RecyclableMemoryStream (used by MsgReader) | MIT | https://github.com/dotnet/maui, https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream |
| YamlDotNet (YAML reading and writing) | MIT, Copyright (c) Antoine Aubry and contributors | https://github.com/aaubry/YamlDotNet |
| Tomlyn (TOML reading) | BSD-2-Clause, Copyright (c) Alexandre Mutel | https://github.com/xoofx/Tomlyn |
| ACadSharp (DWG/DXF reading and writing; includes CSMath and CSUtilities) | MIT, Copyright (c) Albert Domenech | https://github.com/DomCR/ACadSharp |
| SharpGLTF.Core (glTF / GLB reading and writing) | MIT, Copyright (c) Vicente Penades | https://github.com/vpenades/SharpGLTF |
| AngleSharp | MIT | https://github.com/AngleSharp/AngleSharp |
| pypandoc-hwpx (Pandoc AST mapping and package layout in `Filee.Engines/Hwp/Hwpx`, incl. the `blank.hwpx` reference document) | MIT, Copyright (c) 2024 pypandoc-hwpx Contributors | https://github.com/msjang/pypandoc-hwpx |
| Microsoft.Extensions.* | MIT | https://github.com/dotnet/runtime |
| cu2qu (fontTools): the cubic-to-quadratic approach followed by `Filee.Engines/Fonts/Cff/CubicToQuadratic.cs` | Apache-2.0, Copyright 2016 Google Inc. | https://github.com/fonttools/fonttools |

libuiohook (the native input hook inside SharpHook) is the fork https://github.com/TolikPylypchuk/libuiohook, licensed
under the GNU LGPL-3.0 (`COPYING.LESSER.md` in its repository). Filee loads SharpHook's unmodified build of it
dynamically; you may replace those libraries with your own build of libuiohook.

Used by the tests only (not redistributed): DocumentFormat.OpenXml (Open XML SDK, MIT, https://github.com/dotnet/Open-XML-SDK) validates the DOCX files Filee writes.

## Engines

Bundled with the installer:

| Component | License | Project |
|---|---|---|
| rhwp | MIT | https://github.com/edwardkim/rhwp |
| 7-Zip (console: `7z.exe`, `7z.dll`) | GNU LGPL-2.1-or-later; parts of `7z.dll`: LGPL with the unRAR license restriction, BSD-3-Clause, BSD-2-Clause | https://www.7-zip.org |
| Microsoft Visual C++ Redistributable (Windows: next to `Filee.exe` and `rhwp.exe`, conda-forge `vc14_runtime`) | Microsoft Visual C++ Redistributable license | https://github.com/conda-forge/vc-feedstock |

The Linux packages contain an rhwp built by Filee's CI from the unmodified source of the same release (tag `v0.8.7`,
commit `1a76570e833917d15817415a53c09ad61ab3203f`, `cargo build --release --locked --bin rhwp` on Ubuntu 22.04), so it
runs on older Linux systems than rhwp's own Linux build. It is under the same MIT license; its `LICENSE` is next to it
(`engines/rhwp`). Windows and macOS use rhwp's release builds.

7-Zip is Copyright (C) 1999-2026 Igor Pavlov; its `License.txt` is installed next to it (`engines/7zip`). The RAR
decompression code in `7z.dll` is under the unRAR license restriction: it may be used to unpack RAR archives, but not
to develop a RAR (WinRAR) compatible archiver or to re-create the RAR compression algorithm. Filee only reads RAR
files. Source code: https://github.com/ip7z/7zip.

Downloaded from their official release pages when the user chooses to install them
(pinned in `src/Filee.Engines/Infrastructure/engines.json`):

| Component | License | Project |
|---|---|---|
| LibreOffice | MPL-2.0 | https://www.libreoffice.org |
| H2Orestart (HWP/HWPX import for LibreOffice) | GPL-3.0 | https://github.com/ebandal/H2Orestart |
| Eclipse Temurin JRE (for H2Orestart) | GPL-2.0 with Classpath Exception | https://adoptium.net |
| Pandoc | GPL-2.0-or-later | https://github.com/jgm/pandoc |
| Ghostscript (conda-forge build) | AGPL-3.0 | https://www.ghostscript.com, https://github.com/conda-forge/ghostscript-feedstock |
| Microsoft Visual C++ Redistributable (for Ghostscript, conda-forge `vc14_runtime`) | Microsoft Visual C++ Redistributable license | https://github.com/conda-forge/vc-feedstock |
| FFmpeg 9.0.2 (gyan.dev "full_build-shared" Windows build; ffmpeg.exe, ffprobe.exe and their libraries, incl. x264, x265, libvpx, LAME, Opus, Vorbis, Theora, OpenCORE AMR) | GPL-3.0-or-later (the build is configured with `--enable-gpl --enable-version3`); FFmpeg itself LGPL-2.1-or-later | https://ffmpeg.org, build: https://www.gyan.dev/ffmpeg/builds/ (source: https://github.com/GyanD/codexffmpeg) |
| calibre (ebook-convert) | GPL-3.0 | https://calibre-ebook.com |
| Tesseract OCR 5.4 (UB Mannheim Windows build, with Leptonica and the MinGW libraries it ships; their notices are in its `doc` folder) | Apache-2.0 (Tesseract); Leptonica BSD-2-Clause; others as listed in the build | https://github.com/tesseract-ocr/tesseract, build: https://github.com/UB-Mannheim/tesseract |
| Tesseract language data (tessdata_fast 4.1.0), downloaded per language when used | Apache-2.0 | https://github.com/tesseract-ocr/tessdata_fast |

These programs run as separate processes. Their source code is available from the linked projects.

## Windows 11 Explorer menu (FileeExplorerMenu.dll)

The DLL is Filee's own C code, compiled with the Zig toolchain (a build tool, not shipped) against the mingw-w64
Windows headers, without a C runtime. Pieces of these projects that can end up in the binary:

| Component | License | Project |
|---|---|---|
| Zig compiler_rt (compiler support routines) | MIT | https://github.com/ziglang/zig |
| mingw-w64 Windows headers and import library definitions | Public domain / ZPL-2.1 | https://www.mingw-w64.org |

## Installer

`Filee-<version>-win-Setup.exe` is Filee's own installer (`src/Filee.Setup`, MIT like the app). It contains the
.NET runtime, Avalonia, SkiaSharp, HarfBuzzSharp, CommunityToolkit.Mvvm and SharpCompress (LZMA) listed in this file,
the x86 branch filter of the LZMA SDK (public domain, Igor Pavlov, https://www.7-zip.org/sdk.html), and the published
app as its payload. Filee 1.x's installer was built with Inno Setup, which is no longer used.

## Fonts and icons

| Component | License | Project |
|---|---|---|
| Noto Sans, Noto Sans Arabic, Noto Sans JP, Noto Sans KR, Noto Sans SC, Noto Sans Devanagari, Noto Sans Thai | SIL Open Font License 1.1 | https://github.com/notofonts |
| Test fonts in `tests/Filee.Engines.Tests/Fonts` (subsets of Noto Sans and Noto Sans KR; not shipped) | SIL Open Font License 1.1 | see `OFL.txt` in that folder |
| Material Design Icons (Pictogrammers) | Apache-2.0 | https://pictogrammers.com |
| Simple Icons 16.35.0 (GitHub Sponsors, Patreon, Ko-fi and PayPal logos in the Sponsor window) | CC0-1.0 | https://simpleicons.org |

The GitHub Sponsors, Patreon, Ko-fi and PayPal names and logos are trademarks of their owners and are used only to link
to each service.
