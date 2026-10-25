# Conversion engines

Filee chooses engines automatically. Settings → *Engines* shows their status, lets you change the priority
(higher wins when two engines can do the same conversion), and install or remove the optional engines.

| Engine | How it gets there | Handles |
|---|---|---|
| ImageMagick (Magick.NET) | built in (library) | JPG, PNG, WEBP, TIFF, BMP, GIF, ICO, AVIF, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, MNG, QOI, DDS, PCX, CUR, XPM, XBM, WBMP, PAM, PFM, SGI, Sun Raster, MIFF ↔ each other, and **APNG** (Filee's own reader and writer); HEIC (written with FFmpeg), XCF, camera RAW (LibRaw), DICOM (uncompressed ones by Filee's own reader), **FITS** (Filee's own reader; TIFF at the file's own depth by Filee's own writer), Photo CD, OpenEXR, Radiance HDR, EMF/WMF (Windows) and the photo of an iPhone LIVP read |
| Windows Imaging Component | part of Windows (Windows only) | **JPEG XR** (JXR, WDP, HDP; HDR screenshots of Windows) → the images ImageMagick writes, and PDF, ICNS, HEIC and SVG through PNG; HDR tone-mapped to SDR |
| Vector graphics | built in (Svg.Skia, SkiaSharp, ACadSharp) | **SVG/SVGZ → PDF (vector) and images**, SVG ↔ SVGZ, **SVG → DXF** (→ DWG with the CAD engine), **raster images → SVG (tracing)**, PDF-compatible AI → PDF, **HP-GL / HP-GL/2 plot files (PLT) → SVG**, PDF and images (pen moves, packed PE polylines, arcs and filled polygons; text labels are not drawn), **Tajima DST embroidery → SVG** (stitches as lines, a palette colour per thread; → PNG, JPG, PDF from there) |
| ICNS | built in | images → ICNS (16–1024 px), ICNS → images |
| PDFsharp | built in (library) | images → PDF, merge, split, page ranges, page organising, lossless rotate and crop-box edits, spreadsheet print-studio assembly, **compression** (smaller PDFs, see below), PDFs without their metadata |
| PDFium | built in (library) | PDF → images (multi-page TIFF/GIF supported) |
| Unhwp | built in (library) | HWP/HWPX → TXT, Markdown, HTML (pictures in a `<name>_files` folder next to the Markdown, embedded in the HTML) |
| Text & Markdown (Markdig) | built in (library) | **Markdown → HTML, TXT**; **TXT, LOG, PY, PAC, BAT, COMMAND, LUA, CSS, PS1 → UTF-8 TXT**, preserving source text without running scripts (and → PDF / DOCX through the HWPX writer, rhwp or LibreOffice) |
| Spreadsheets (ExcelDataReader for XLS) | built in (library) | **XLSX, XLS, ODS, CSV, TSV → XLSX, ODS, CSV, TSV** (one CSV / TSV per sheet) |
| Office Open XML | built in | **DOCM / DOTX / DOTM ↔ DOCX, XLSM / XLTX ↔ XLSX, PPTM / POTX / POTM / PPSX ↔ PPTX** (macros removed for macro-free types) |
| Fonts | built in | **TTF, OTF, WOFF, WOFF2, EOT ↔ each other**; CFF (PostScript) outlines become TrueType for TTF and EOT |
| **HWPX writer** | built in | **DOCX (+ DOCM/DOTX/DOTM), XLSX (+ XLSM/XLTX), XLS, ODS, CSV, TSV, PPTX (+ PPTM/POTX/POTM/PPSX), PDF, OFD, HTML, MHTML, EPUB, MOBI/AZW3, FB2, HWPX, TXT, Markdown → HWPX** (and with rhwp → PDF and images); ODT, RTF, reStructuredText, LaTeX → HWPX with Pandoc |
| **DOCX writer** | built in | **HWPX (and HWP through rhwp), PDF, OFD, HTML, MHTML, EPUB, MOBI/AZW3, FB2, Markdown, TXT, XLSX, XLS, ODS, CSV, PPTX (+ variants) → DOCX** |
| PDF text | built in (PdfPig) | **PDF → TXT, Markdown** (reading order, also two columns; Markdown keeps headings, emphasis, links and the pictures; no OCR) |
| PDF slides | built in (PDFium, PdfPig) | **PDF → PPTX**: a picture of each page on its own slide (looks like the PDF, not editable), the page's text in the speaker notes |
| PDF tables | built in (PdfPig) | **PDF → XLSX, ODS, CSV** (#267): each table drawn with lines on its own sheet, numbers and dates as real values, merged cells and column widths kept |
| E-mail | built in (MimeKit, MsgReader) | **EML, Outlook MSG → HTML** (headers + body, inline pictures), **TXT**, **ZIP** (the attachments); **MSG → EML**; **MHTML → HTML** (the saved page in one file, pictures inlined; on to PDF, DOCX, HWPX, TXT, Markdown, EPUB from there) |
| Data | built in (System.Text.Json, YamlDotNet, Tomlyn, System.Xml) | **JSON, JSON Lines, YAML, TOML, XML, CSV ↔ each other** (CSV for tables); **vCard, iCalendar ↔ CSV**; spreadsheets, contacts and events reach JSON / YAML / TOML / XML through CSV |
| **E-books** | built in | **EPUB, MOBI/AZW/AZW3/PRC, FB2, HTMLZ, TXTZ → EPUB, HWPX (→ PDF), TXT, HTML, Markdown, FB2, HTMLZ, TXTZ**; HTML, Markdown, TXT, DOCX → EPUB/FB2/HTMLZ/TXTZ; HTML → TXT; comics CBZ/CBR/CB7/CBT/CBC → PDF, EPUB, CBZ; PDF → CBZ; AZW4 → PDF; **EPUB ↔ KEPUB** (Kobo) |
| CAD (ACadSharp + built-in renderer) | built in (library) | **DWG ↔ DXF**; DWG/DXF → **PDF** (vector), **SVG**, PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF |
| **Subtitles** | built in | **SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC → SRT, ASS, WebVTT, SAMI, LRC, TTML, MicroDVD**, always UTF-8 (Korean CP949 / EUC-KR files read correctly) |
| 3D models (SharpGLTF for glTF) | built in (library) | **GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY ↔ each other**; FBX → each of them; → PNG, JPG, … (an isometric preview) |
| **Geospatial vector data** | built in | **GeoJSON, KML, KMZ, GPX, Shapefile ↔ each other and CSV** (longitude / latitude or WKT); projected Shapefiles (UTM, Web Mercator, Lambert, Albers) are converted to WGS 84; through CSV to XLSX, JSON, … (see below) |
| rhwp | bundled with the installer (`engines/rhwp`, with the Microsoft C++ runtime, see below) | HWP/HWPX → PDF, **HWP → HWPX, HWPX → HWP** |
| Archives (7-Zip) | bundled with the installer (`engines/7zip`, ~2.5 MB) + built-in readers | ZIP (including ANSYS Workbench WBPZ), 7Z, RAR, TAR (+ GZ/BZ2/XZ/Z/7Z/LZ), CAB, ISO, DMG, … **→ folder, ZIP, 7Z, TAR, TAR.GZ/BZ2/XZ**; ALZ, EGG, lzip read in-process; "Compress into one archive" for any files |
| LibreOffice + H2Orestart + Java | **optional download** (~420 MB, 1.3 GB on disk) | older and rare formats only: DOC, XLS, PPT, RTF and OpenDocument → PDF and to each other, output as DOCX/ODT/ODS/ODP; HWP/HWPX → DOCX/ODT; read-only formats (see below) → PDF and their kind's editable formats; FODT, OpenDocument templates (OTT, OTS, OTP) and XLT out; SVG → EMF/WMF/ODG, a PDF page → SVG |
| Pandoc | **optional download** (~42 MB, 240 MB on disk) | Markdown ↔ DOCX/ODT/RTF, DOCX/ODT/HTML/RTF → Markdown, HTML ↔ DOCX/ODT, markup languages (reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML) ↔ Markdown/HTML/DOCX/ODT and each other (and → RTF, EPUB, TXT), Markdown and markup → PPTX slides |
| Ghostscript | **optional download** (~20 MB, 31 MB on disk) | EPS/PS → PDF, PDF → EPS/PS, PostScript-based AI → PDF (images through PDF and PDFium); smaller PDFs when a preset asks for it |
| Tesseract | **optional download** (~50 MB, 160 MB on disk; each language 0.4–10 MB when first used) | **text recognition (OCR)**: scans and pictures → searchable PDF, and their text into DOCX, TXT, Markdown and HWPX ("Recognize text" in the preset, see below) |
| Apple Vision | part of macOS (Mac only) | **text recognition (OCR) on a Mac**, the same step as Tesseract without a download; Tesseract reads the languages Vision doesn't (see "Apple Vision on macOS") |
| FFmpeg | **optional download** (~100 MB, 272 MB on disk) | **all video and audio**: video ↔ video (H.264, HEVC, AV1), video → animated GIF / APNG, a still frame or every frame, audio and subtitle extraction, audio ↔ audio (also APE, WavPack, TTA, DSD, DTS, ALAC, …), GIF, APNG and photos → MP4/WEBM/MOV, Live / Motion Photos → their video, a clip of a video → an iPhone Live Photo or Android Motion Photo (experimental), images → HEIC, clips or audio files joined into one, pictures → a slideshow MP4, copies without metadata (see "Media tools") |
| Calibre | **optional download** (~216 MB, 660 MB on disk) | rare e-book formats: LIT, LRF, CHM, PDB, PML, RB, SNB, TCR, OEB → EPUB; EPUB → MOBI, AZW3, LIT, LRF, PDB, PML, RB, SNB, TCR; e-books → PDF (ranked above the HWPX writer, see below) |

DOCX, XLSX, XLS, ODS and PPTX → PDF need neither Microsoft Office nor LibreOffice: they are read in-process, written as HWPX
and rendered by rhwp. Anything the built-in readers understand (including PDF) is written as DOCX by the DOCX writer.
LibreOffice's and Pandoc's costs are set so the built-in route always wins where one exists.

Except for text that needs shaping. rhwp draws one glyph per character from left to right without OpenType shaping,
so Arabic and Hebrew come out unjoined and in the wrong order, and Indic vowel signs and conjuncts as separate glyphs
with dotted circles (#185 Gujarati, #202 Arabic). Its PDF edges are marked `Unshaped`, and rhwp tells
(`ITextShapingCheck`) whether a source has such text: `Infrastructure/ComplexScripts.cs` scans the text parts of the
file without reading it into the document model (DOCX `word/document.xml`, headers, footers and notes, PPTX slides,
XLSX shared strings and sheets, HWPX sections, OFD pages and templates, EPUB chapters, or a text file with its HTML /
RTF character references) for the Hebrew, Arabic, Syriac, Thaana, N'Ko, Indic, Sinhala, Thai, Lao, Tibetan, Myanmar,
Khmer, Mongolian and other South / Southeast Asian blocks. For such a file `JobQueue` plans again without unshaped
edges: DOCX, TXT, Markdown, HTML, XLSX, PPTX and HWPX → PDF (and → images through PDF) go through LibreOffice even
when Settings → *Engines* doesn't choose it for documents. Without LibreOffice the file fails with "This script needs
LibreOffice to convert correctly" and the script's name instead of becoming a broken PDF. The same scan also finds
text rhwp has no widths for: its width tables cover only ASCII and Latin-1, general punctuation, math, the CJK forms
and Hangul (rhwp#8143), so a letter of any other script — Polish, Turkish, Vietnamese, Greek, Cyrillic, ... — or a
combining mark is drawn but laid out at a fixed half-em. Such a document takes a route without unshaped edges when
one exists and is never refused when none does: the overlapping text stays readable. Only letters inside words
count (an extended Latin letter next to any letter, a letter of another script next to one of its own script), so a
symbol or unit on its own (α, Ω, μm in a Korean report) keeps rhwp, and an HWPX stays with rhwp for this (only text
that needs shaping sends it elsewhere). Documents in French, German or Spanish (Latin-1 letters only), Chinese,
Japanese and Korean keep the built-in route; a binary HWP is not scanned and stays with rhwp.

All built-in document readers are registered in one place, `Hwp/Hwpx/DocumentReaders.cs` (format id → reader, and
which formats need Pandoc). The HWPX writer and the DOCX writer take their input formats from it: adding a reader
there is one line and makes both writers (and every route through them) accept the format.

Every engine is built in, bundled with the installer or downloaded into Filee's own folder, so a conversion behaves
the same on every PC. An external engine is looked for in this order:

1. a copy the user picked in Settings → *Engines*;
2. Filee's own copy (bundled or downloaded);
3. a copy already on the computer: on `PATH`, in Homebrew's and the usual Unix folders, or where the LibreOffice,
   calibre, Ghostscript and Tesseract installers put it. Settings → *Engines* shows it as "Found on this PC", with a note when
   its version is older than the one Filee downloads.

Where Filee has no download of an engine (FFmpeg, Ghostscript and Tesseract on macOS, LibreOffice, Ghostscript and
Tesseract on Linux),
step 3 is how it is found. Microsoft Office
and Hancom Office are never used, even when installed. Running from source, the repository's `engines/` folder
(filled by `build/fetch-engines.ps1`) is used.

## Optional engines

The installer contains only the small engines. On first start Filee asks which of the large ones to download;
they can be installed or removed later in Settings → *Engines*. A conversion that needs a missing engine shows
"Needs LibreOffice …" on its donut slice.

- Downloads are pinned in `src/Filee.Engines/Infrastructure/engines.json` (URL, SHA-256, size). The same file is
  read by `build/fetch-engines.ps1`, so development and the app always use the same versions. A macOS download can
  name the oldest macOS it runs on (`minimumMacOS`); an older Mac isn't offered it.
- `EngineInstaller` verifies the SHA-256 before unpacking and unpacks into a staging folder that replaces the
  engine folder only when complete. Redirects to mirrors (even plain HTTP) are followed, because the hash decides.
- Engines go to `%LOCALAPPDATA%\Filee\engines`: outside the app folder, so updates keep them, and inside Filee's
  install root, so uninstalling removes them.
- LibreOffice and calibre come as MSIs and are unpacked with an administrative install (`msiexec /a`): files only,
  no registry entries, no admin rights. LibreOffice's help, gallery and most dictionaries are removed afterwards
  (~500 MB).
- Ghostscript comes from conda-forge (Artifex publishes only an NSIS installer): a `.conda` package is a zip with a
  zstd tarball, of which only `Library/bin` is unpacked (SharpCompress; `fetch-engines.ps1` uses Windows' `tar.exe`).
  Its fonts and resources are compiled into `gsdll64.dll`. The Microsoft C++ runtime it was built against
  (`vcruntime` component) is copied next to `gswin64c.exe`, so it runs on PCs without the VC++ Redistributable.
- Tesseract is UB Mannheim's Windows build (the one Tesseract's documentation points to), published only as an NSIS
  installer. It is never run: Filee's own 7-Zip unpacks it (`nsis` kind), and the training programs, manuals,
  installer files and bundled language data are removed (`TrimTesseract`, ~80 MB). The languages come from
  `tessdata_fast` 4.1.0, pinned like the engines in `Infrastructure/ocr-languages.json` (`traineddata` kind, one
  folder per language under `engines/ocr-languages`), and are downloaded the first time a preset uses one.

### Microsoft C++ runtime (Windows)

Programs built with Visual C++ import `MSVCP140.dll` / `VCRUNTIME140.dll`, which a fresh Windows does not have (they
come with the Visual C++ Redistributable). Windows looks for a program's DLLs in its own folder first, so Filee puts
the runtime next to every program that needs it and does not ship it (`Infrastructure/AppLocalRuntime.cs`):

| Binary | Runtime |
|---|---|
| `engines/rhwp/rhwp.exe` (bundled) | `vcruntime` component copied next to it by `fetch-engines.ps1` (#248) |
| `unhwp.dll` (NuGet, loaded into Filee.exe and filee-cli.exe) | `vcruntime` component copied next to `Filee.exe` (`build-installer.ps1` → `fetch-engines.ps1 -RuntimeTo`) |
| Ghostscript (download) | `vcruntime` component copied next to `gswin64c.exe` (`EngineInstaller`, `fetch-engines.ps1`) |
| LibreOffice (download) | its own copy, which the administrative install leaves in `System64`, copied into `program/` (`EngineInstaller`, `fetch-engines.ps1`; `LibreOfficeConverter` also repairs a copy installed by an earlier Filee) |
| calibre, Java (downloads) | ship their own next to their programs (`app/bin`, `bin`) |
| 7-Zip, FFmpeg (GyanD), Tesseract (UB Mannheim, MinGW), Pandoc, PDFium, SkiaSharp / HarfBuzz, Magick.Native, the Explorer menu DLL, .NET | import neither (static runtime or MinGW / GHC builds) |

The installer and the portable zip are made from the same publish folder, so both have the DLLs. Filee installed in
Program Files cannot repair its own bundled files, so an older installation gets them by installing the new version.
When a DLL is still missing, Windows does not start the program and reports `0xC0000135` (STATUS_DLL_NOT_FOUND;
`0xC0000139` for an outdated one) as its exit code: `ProcessRunner` and `FfmpegRunner` turn that into
`error.runtime_missing` ("A Microsoft Visual C++ runtime file this engine needs is missing or outdated …", with the
program and the code in brackets) instead of the raw exit code, and `UnhwpConverter` does the same when `unhwp.dll`
cannot be loaded. To check binaries after an engine update, list the DLLs each `.exe` / `.dll` imports (its PE import
table, e.g. `dumpbin /dependents`) and look for `MSVCP140*.dll`, `VCRUNTIME140*.dll` or `CONCRT140.dll`.

To bump a version, change `url`, `sha256` and `size` in engines.json together, run
`pwsh build/fetch-engines.ps1`, test, and update `THIRD-PARTY-NOTICES.md`. For rhwp, also update `RHWP_VERSION`,
`RHWP_COMMIT` and `RUST_TOOLCHAIN` in `build/build-linux-native.py` (see below); CI fails until they match. When the
LibreOffice team retires a version from `download.documentfoundation.org/libreoffice/stable/`, its URL stops working:
keep the pinned version current.

## Linux builds for older glibc

Linux packages run on Ubuntu 22.04 (glibc 2.35). rhwp's Linux release is linked on Ubuntu 24.04 (needs glibc 2.39).
The `linux-native` job of `.github/workflows/portable.yml` builds it from its pinned, unmodified source on Ubuntu 22.04
(x64 and arm64) with `build/build-linux-native.py`:

- `rhwp`: `cargo build --release --locked --bin rhwp` at the tag's commit with its pinned Rust toolchain, in the
  layout of rhwp's release archive.
- `check` (also run on its own): fails when engines.json pins another rhwp version than the script builds.

The Linux package jobs pass the result to `build/build-portable.ps1 -RhwpOverride <dir>`. Local builds without it use
the downloaded rhwp (fine on the machine that builds it). SharpHook's native libraries are packaged as they come
from NuGet: SharpHook 8.0.0 built its X11 back ends against glibc 2.38, 8.0.1 needs glibc 2.34 at most again.

`build/check-elf-glibc.py` lists the newest GLIBC version every ELF file in the package needs and fails above 2.35,
and the packages are unpacked and started on Ubuntu 22.04 (`build/smoke-portable.ps1`: CLI, rhwp conversions, loading
the X11 input hook back end; `build/smoke-launch-app.py`: the app under Xvfb). If a SharpHook or engine update fails
these checks, keep the previous version until upstream builds for an older glibc again, or build the binary from its
pinned source in `build/build-linux-native.py` like rhwp (PR #113 did this for SharpHook 8.0.0's libuiohook back ends).

## Images and vector graphics

- **Trim transparent borders** (preset *Trim transparent borders*, `ImageOptions.TrimTransparent`, #332) crops a PNG,
  WebP, … to the box around its pixels that aren't fully transparent (`ImageEncoder.TrimTransparentBorders`) and saves
  `name_trimmed` in the same format; the source is never overwritten. Only alpha counts: ImageMagick's own trim
  takes the corners' colour, so content touching a corner would lose a solid border. Pictures without transparent
  borders, fully transparent ones and multi-frame animations (each frame alone would shift) come out as they are.
  Formats that can't be transparent (JPG, HEIC, …) have nothing to trim: the preset is greyed out for them, and a
  JPG in a mixed selection fails with that reason instead of being re-encoded.
- **Animations.** An animated GIF, WebP, APNG or MNG keeps every frame and its timing when converted to one of these
  (animated GIF ↔ animated WebP ↔ APNG ↔ MNG, WebP lossy or lossless as the preset says) and becomes a multi-page TIFF;
  other targets get the first frame. Multi-page TIFFs and PDFs stay one image per page in WebP.
- **Playback speed** (preset option *Playback speed*, `ImageOptions.AnimationSpeed`: 0.25×, 0.5×, 0.75×, 1×, 1.5×,
  2×, 4×; shown for GIF, WebP, APNG, MNG and "same as source") rewrites the frame delays of an animation that stays
  animated (`Magick/AnimationTiming.cs`). Delays are divided by the speed in the target's own units (hundredths of a
  second for GIF and MNG, milliseconds for WebP and APNG) and rounded on the running total, so the whole animation
  keeps its new length. Players show a frame of 10 ms or less for 100 ms, so no delay goes below 20 ms (50 frames a
  second): a frame that would start sooner than that after the one before is dropped and its time goes to that one,
  which leaves frames out evenly. Source delays of 10 ms or less count as the 100 ms players show. Loop count and
  disposal stay; 1× (also every preset saved before the option) leaves the timing untouched. Video → GIF / APNG
  (FFmpeg) puts `setpts=PTS/<speed>` before the `fps` filter, so the frame rate limits stay. The speed changes only in
  the step that writes the output (`JobQueue`), never twice on a longer route.
- **APNG** (`Magick/Apng.cs`) is Filee's own code, written from the APNG specification: ImageMagick reads only an
  APNG's default image and writes APNG through an external FFmpeg. Every frame is an ordinary PNG decoded or encoded by
  ImageMagick; Filee handles the chunks around them.
  - Reading composes each frame onto the canvas as a player shows it: `fcTL` regions and offsets, blend `SOURCE` / `OVER`,
    dispose `NONE` / `BACKGROUND` / `PREVIOUS`, delays (a denominator of 0 means hundredths) and the loop count. A
    default image that comes before the first `fcTL` is not part of the animation and is skipped. Animated PNGs saved
    as `.png` are recognised by their `acTL` chunk and stay animated as GIF, WebP or APNG; other targets get the first
    frame.
  - Writing gives every frame the same pixel format (8-bit RGB, or RGBA when any frame is transparent or smaller than
    the canvas): the first frame is the default image (`IDAT`, so viewers without APNG support show it), the others are
    `fdAT` chunks holding only the rectangle that changed (dispose `NONE`, blend `SOURCE`); a frame identical to the one
    before lengthens its delay instead. Checked with FFmpeg's APNG demuxer.
  - APNG ↔ video needs FFmpeg: video → APNG uses its `apng` encoder and muxer (24-bit RGB, looping, the GIF's frame rate
    and width limits), APNG → MP4/WEBM/MOV its APNG demuxer. Animated WebP and GIF still reach video through GIF, a
    still picture through PNG.
- **More raster formats** through ImageMagick's own coders, each checked against `MagickNET.SupportedFormats` and
  converted there and back in `ImageFormatTests`: QOI, MNG (animated), DDS (DXT block compression, which is lossy; of
  a texture with mipmaps the largest image is read), PCX, CUR (at most 256 × 256, larger pictures are scaled down), XPM,
  XBM and WBMP (1-bit), PAM, PFM, SGI (`.sgi`, `.rgb`, `.rgba`, `.bw`, always read as SGI; ImageMagick would take
  `.rgb` for headerless pixels), Sun Raster (`.ras`, `.sun`) and MIFF. XBM, WBMP, PFM, PCX and Sun Raster are written
  without transparency (flattened onto the preset's background): PFM has none, and the alpha planes ImageMagick can
  put into PCX and Sun Raster are read by hardly any other program. Not offered: PICT, because ImageMagick can't read
  back direct-colour PICTs wider than about 130 pixels that it wrote itself.
- **Read only**: Photo CD (the 768 × 512 base image), OpenEXR and Radiance HDR. The bundled Magick.NET is the 8-bit
  (Q8) build without HDRI, so high-dynamic-range values are clipped to the displayable range and stored with 8 bits per
  channel; there is no tone mapping. Writing EXR or HDR from 8-bit pictures would only make larger files, so they are
  not targets.
- **JPEG XR** (`.jxr`, `.wdp`, `.hdp`, read only, Windows only; `Wic/`). ImageMagick has no JPEG XR coder in any build,
  so the picture is decoded by Windows' own codec (WIC's JPEG XR decoder, in every Windows 10 and 11) and handed to
  ImageMagick for the preset's options and the output format. Elsewhere the engine reports "not available on this
  platform". 8- and 16-bit pictures are converted to 8-bit sRGB by WIC. Fixed-point and float ones are linear scRGB
  (1.0 = SDR white at 80 nits), like the 64-bit half-float screenshots Windows saves on an HDR display (Xbox Game Bar,
  Win + Alt + PrtScn, Snipping Tool), and are tone-mapped (`Wic/HdrToneMap.cs`) instead of clipped:
  - the reference white is the luminance 99 % of the pixels stay below, between 1.0 and 4.0 (80–320 nits). Windows
    draws SDR content at the display's "SDR content brightness", so the white of a desktop screenshot lands there and
    its SDR parts come out as they were drawn; small highlights don't darken the picture;
  - values up to 80 % of that white are kept; above, an extended Reinhard shoulder takes everything up to the brightest
    pixel into the last 20 %, on the largest of R, G and B so colours keep their hue. A picture with nothing brighter
    than white is left as it is;
  - colours outside the sRGB gamut are clipped, then the sRGB transfer curve and 8 bits. An alpha channel that is
    opaque everywhere (HDR screenshots have one) is dropped.
- **DICOM** (`.dcm`, `.dicom`, read only). The Q8 build reads only 8-bit DICOM, while CT, MR and X-ray scanners store
  12 or 16 bits, so `Magick/Dicom.cs` (written from the DICOM standard, PS3.5 and PS3.3 C.7.6.3 / C.11.2) reads
  uncompressed files itself: implicit and explicit VR little endian, explicit VR big endian and deflated files, with or
  without the 128-byte preamble; 8 or 16 bits allocated, signed or unsigned; MONOCHROME1 (inverted) and MONOCHROME2,
  RGB and YBR_FULL (interleaved or planar); every frame of a multi-frame file. Grey values go through the rescale
  slope / intercept and the file's first window (centre / width) like a viewer's default view, or are stretched over
  the file's range when there is no window, to 8 bits. Compressed pixel data (JPEG, JPEG-LS, JPEG 2000, RLE) is left to
  ImageMagick, which manages only 8-bit images; others fail with a message asking for an uncompressed export.
- **FITS** (`.fits`, `.fts`, read only; `.fit` is left to Garmin's FIT). Astronomy pictures from telescope cameras,
  capture and stacking programs. `Magick/Fits.cs` (written from the FITS 4.0 standard) reads the first HDU with a
  picture, the primary one or the first IMAGE extension: 2880-byte blocks of 80-character cards, big-endian data,
  BITPIX 8, 16, 32, 64, -32 and -64, physical values BZERO + BSCALE × stored (BLANK integers become undefined), a
  cube of three planes as red, green and blue (other cubes give their first plane). The Bayer pattern comes from
  `BAYERPAT` or `COLORTYP` (RGGB, BGGR, GRBG, GBRG) at the first stored pixel, shifted by `XBAYROFF` / `YBAYROFF`.
  `ROWORDER = 'BOTTOM-UP'` turns the picture upright; without it rows are taken top-down, as capture programs write
  them. Tile-compressed files (fpack, a BINTABLE with `ZIMAGE = T`) fail with a message asking to `funpack` them.
  - **PNG, JPG and every other target** (`FitsRendering`): stretched automatically like a capture program's preview.
    The values are scaled to 0–1 over their range; per channel, the shadows are clipped 2.8 normalized MADs
    (1.4826 × the median absolute deviation) below the median and a midtones transfer function takes the median to
    0.2. A colour camera's mosaic is debayered first (bilinear: each missing colour is the average of the neighbours of
    that colour) unless the preset's *Debayer FITS from a colour camera* is off (`ImageOptions.FitsDebayer`); its
    channels are stretched one by one, so the sky comes out neutral. 8 bits per channel, then ImageMagick as usual.
  - **TIFF** (`TiffWriter`, as the Q8 build stores 8 bits): *Linear* (the data as it is, the default) or *Stretched*
    (`ImageOptions.FitsTiff`), at the file's own depth: 8-bit and 16-bit data stay so when they are whole numbers in
    range (unsigned 16-bit through BZERO 32768 included); 32-bit integers, floating point and signed or scaled values
    that don't fit become 32-bit floating point. Never debayered, so another program can still debayer the mosaic.
    Uncompressed, LZW or Deflate as the preset's TIFF compression says (integers with the horizontal predictor);
    turning and mirroring apply, resizing and the other picture options don't. FITS → TIFF costs a little more than
    other edges, so other formats are reached through a stretched picture, never through the linear TIFF.
- **Fujifilm HEIF** (`.hif`) is read as HEIC.
- **Rotate and flip** happen after the EXIF orientation is applied, in the one step of a route that turns images
  (`IImageTransformer`). There is no lossless JPEG rotation: a turned JPEG is re-encoded, at its own estimated quality
  when that is lower than the preset's, so a phone photo saved at 75 doesn't come back bigger at 95.
- **Layered files.** PSD/PSB convert from Photoshop's composite (ImageMagick merges the layers when a file was saved
  without "maximize compatibility"). GIMP XCF layers are composited at their offsets with their blend modes; hidden
  layers are left out. ImageMagick reads 8-bit XCF without zlib tile compression (GIMP 2.10+ "better but slower
  compression" files give a clear error).
- **LIVP** (an iPhone Live Photo as iCloud downloads it: a ZIP of the photo, HEIC or JPEG, and its MOV) is read as its
  photo, taken out of the package in memory (`LivePhotoSource.ReadStill`, at most 64 MB); its video is FFmpeg's (see
  Live / Motion Photos there).
- **Live / Motion Photos from a video** (JPG and HEIC targets) are FFmpeg's: the clip and the photo's frame come from
  it, ImageMagick or the HEIC writer only encode that frame (see *Making Live / Motion Photos* there).
- **Camera RAW** (DNG, CR2, CR3, NEF, ARW, …, and `.raw`) is decoded by LibRaw with the camera's white balance,
  sRGB output and AHD demosaicing, then turned upright from the EXIF orientation.
- **EMF/WMF** are rendered through GDI+ (Windows only) at the preset's render DPI (at least 150), keeping their
  physical size in PDF output.
- **SVG → PDF** replays the drawing into Skia's PDF backend: paths, gradients and clips stay vector. Text is written
  as glyph outlines (shaped with HarfBuzz), because SkiaSharp's PDF backend has no font subsetter and would embed
  every font whole (13 MB for one line of Korean). SVG → images is rendered at its final size: the preset's DPI, at
  least 1024 px on the long edge, or exactly the preset's resize target. External files next to the SVG are
  loaded; nothing is fetched from the network and scripts don't run.
- **ICNS** files are written with every size iconutil makes (16–1024 px, PNG), the picture centred on a transparent
  square; reading takes the largest image (PNG, JPEG 2000 or the old RLE + mask entries).
- **Illustrator** files saved with "Create PDF compatible file" (the default since Illustrator 9) are PDFs and need
  nothing extra; PostScript-based ones need Ghostscript.
- EPS and PS → PNG go straight through Ghostscript's `pngalpha` device, so the background stays transparent;
  JPG, WebP, … and AI reach images through PDF (Ghostscript → PDFium), and SVG reaches EPS through PDF.

## Tracing (raster images → SVG)

Built in (`Vector/Tracing`), no Potrace or other program: Filee's own code, written from published descriptions of the
steps (no code of Potrace, AutoTrace or VTracer is used). Every raster format ImageMagick reads (PNG, JPG, BMP, GIF,
WEBP, TIFF, HEIC, PSD, RAW, DICOM, …) is traced, and other raster images through PNG (ICNS, APNG). Only raster images are traced:
PDF, EPS, PS, AI, EMF/WMF, CDR, documents and videos are never redrawn from a rendering, so without a real vector
route they have no SVG target (PDF → SVG, for example, needs LibreOffice). The first frame or page is used, turned upright, in sRGB. The SVG has the picture's pixel size
(`width`/`height`/`viewBox` in pixels), so it lines up with the original.

- **Black and white** (*Line art to SVG*: logos, signatures, scans): pixels darker than Otsu's threshold are ink
  (transparent pixels count as white paper). On a transparent background the opaque pixels are the ink, or only their
  darker part when the opaque area itself is light and dark. One black `<path>` on a transparent background.
- **Colour** (*Trace to SVG*: illustrations, icons): k-means reduces the picture to the preset's number of colours
  (2–64; run on the mean colours of 32 768 colour boxes weighted by their pixel counts, so it sees every pixel). One-pixel
  bands of in-between colours along edges (anti-aliasing) join the colour on either side; regions smaller than a few
  pixels (more on large pictures) join the neighbour they share most border with. Colours are stacked largest first,
  and each colour's shape is the union of itself and every colour above it: lower shapes run under upper ones, so no
  background shows through between neighbouring shapes. Transparent areas stay transparent. One `<path>` per colour.
- **Outlines**: each shape's boundary is followed along the pixel edges (inside always on the right, so holes come out
  reversed and fill correctly with the non-zero rule; pixels touching only at a corner stay one shape). Outlines
  smaller than the speck size are dropped (and small holes filled).
- **Curves**: the staircase is replaced by points on the edge it approximates (midpoints of the steps); corners are
  kept where two long pixel runs meet, at the ends of thin lines, where the direction turns sharply within two
  pixels, or where a gentler turn happens all at once (unlike a small circle). Points between corners are smoothed;
  stretches that stay within half a pixel of a line without bending become lines; the rest becomes cubic Béziers
  fitted by least squares (Philip J. Schneider, "An Algorithm for Automatically Fitting Digitized Curves", Graphics
  Gems, 1990), meeting lines and each other with matching tangents.
- **Options** (Settings → Presets): mode, colours (colour mode) and *Detail* 0–100. Low detail drops larger specks,
  smooths more and allows the curves 1.15 px of deviation; high detail keeps every pixel's shape (0.25 px). The
  default 50 drops specks under 6 px (black and white) or merges regions under 17 px (colour), at 0.7 px; speck and
  region sizes grow with pictures larger than 1000 px (up to four times).
- **Size and speed**: pictures over 6 megapixels or 4096 px on a side are reduced before tracing; the SVG keeps the
  original size and says so in a comment. A 2000 × 2000 illustration traces in 1–3 s at 16 colours and well under a
  second in black and white; a noisy 2000 × 2000 photo at 16 colours takes about 4 s (64 colours about 5 s, and a
  20 MB SVG). Photos are not what tracing is for: they come out as posterized patches.
- **Routes**: tracing is the only raster → SVG route and costs 40 (`VectorConverter.TraceCost`), more than any route
  from an image to a format SVG leads to, so it is never a step on the way to PDF, PNG, EPS, …; a test checks every
  pair of formats. Its edges are marked `ImageSourcesOnly`: the planner uses them only when the file being
  converted is a raster image (`FormatCategory.Image`), so a vector file rendered to PNG can't be traced; a test
  checks this for every pair of formats too. Through SVG, pictures reach DXF and DWG.

## SVG → DXF / DWG

`Vector/SvgDxfWriter.cs` writes the drawing's outlines with ACadSharp (DXF in the preset's version like the CAD engine; DWG is
DXF → DWG by the CAD engine). The SVG is loaded with Svg.Skia as for PDF output, so groups, transforms, `<use>`,
nested `<svg>`, CSS and text are resolved the same way.

- Circles and ellipses become CIRCLE and ELLIPSE (also rotated, scaled or skewed: the transformed axes come from the
  singular values of the transform). Paths, lines, polylines, polygons and rectangles (rounded too) become
  LWPOLYLINEs, closed where the shape is closed, or a LINE for one straight segment. Bézier curves and arcs are divided
  into straight pieces within 0.01 mm of the curve: every CAD and CAM program reads polylines, while SPLINE support
  varies. Text becomes its glyph outlines.
- Units: SVG user units are CSS pixels, 96 per inch, so 1 px = 25.4 / 96 mm and the DXF is in millimetres
  (`$INSUNITS` 4, metric): a drawing 100 mm wide in the SVG is 100 mm wide in CAD. The y axis is flipped (SVG points
  down, DXF up) with the viewport's bottom-left corner at (0, 0).
- Colours are kept as true colours (the stroke's, else the fill's; black for gradients). Fills, stroke widths,
  clipping, masks, images and filters are not represented; a shape both filled and stroked is written once. An SVG
  with nothing to outline fails with a message.

## HWPX writer

Filee writes OWPML (HWPX) itself, so saving as HWPX needs no Hancom Office and shows no approval dialog.
Readers turn the source into a small document model (`Hwp/Hwpx/HwpxModel.cs`) and `HwpxWriter` writes the package:

- **DOCX** is read directly (`Hwp/Hwpx/Docx/DocxReader*.cs`) so the layout survives:
  - page size, orientation and margins per section; columns, also when they change mid-page;
  - headers and footers (odd/even pages, hidden on the first page) with page numbers and page counts;
  - paragraph formatting (alignment, indents, spacing, line spacing, tab stops, keep with next);
  - character formatting (fonts incl. theme fonts, size, colour, highlight and shading, bold/italic/underline/strike, super/subscript, spacing);
  - lists with Word's numbering formats (1., 가., ①, i., bullets);
  - symbols of Wingdings, Wingdings 2, Wingdings 3, Webdings and Symbol (Insert → Symbol, bullets, text typed in
    those fonts) as the Unicode characters they show: check boxes ☐ ☑ ☒, check marks and crosses, arrows, stars,
    hands, phone and mail signs, Greek letters and math signs (`Hwp/Hwpx/SymbolFonts.cs`, #350; PPTX bullets and
    text too). Codes without a table entry stay as before (an unknown bullet is "●");
  - tables with merged cells, column widths, borders and shading from the table style and cells, repeated header rows;
  - pictures inline and floating, text boxes, rectangles/ellipses/lines with solid or gradient fills, groups;
  - footnotes, endnotes, bookmarks, hyperlinks (web and within the document, e.g. a table of contents);
  - equations (Word's equation editor, Office Math) as 한글 equations, see *Equations* below;
  - Word's document grid ("lines"), which 한글 does not have, as an "at least" line spacing.
- **XLSX, XLS, ODS, CSV and TSV** (`Office/Sheets`, see [Spreadsheets](#spreadsheets)): every visible sheet becomes a section with one table. Cells show what Excel
  shows (number formats via ExcelNumberFormat, dates, percentages, cached formula results), with fonts, fills,
  borders, alignment and merged cells; an XLSX's rows to repeat at the top (Print Titles, #407), else frozen top
  rows, repeat on every page, and rows above the print titles come once before them. The page is A4 with narrow margins,
  landscape when the columns are much wider than portrait, and the table is scaled to the page width like
  Excel's "Fit all columns on one page". CSV is read with its delimiter (comma, semicolon, tab) and encoding
  (UTF-8 / UTF-16 with BOM; else CP949 when it looks Korean or on Korean Windows, as Korean Excel saves it; else the PC's code page). Not converted: charts, pictures, conditional
  formatting, print areas and page breaks.
- **PPTX** (`Hwp/Hwpx/Pptx`): one page per slide, sized like the slide, with every object floating at its place:
  placeholders inherit position and text style from the layout and master, text with bullets and numbering,
  shapes (rectangles, rounded rectangles, ellipses, lines) with solid or gradient fills and outlines, pictures,
  tables with PowerPoint's default table style, groups, master decorations and the slide background. Not
  converted: charts, SmartArt, rotation, effects (shadows, glow), picture cropping, animations, notes and hidden
  slides.
- **TXT** needs no reader (one paragraph per line; UTF-8, UTF-16, CP949 when the bytes look Korean or, on Korean
  Windows, are valid CP949, else the PC's code page: GBK on Chinese Windows, Shift-JIS on Japanese, Windows-1252 on
  Western, as subtitles are read; a `charset=` in the text isn't followed).
- **Markdown** is parsed with Markdig (`MarkdownReader.cs`): headings, emphasis, code, lists (1. / a. / i. with
  start numbers), task lists, quotes, tables with spans, links, images, footnotes and math. No Pandoc needed.
- **PDF** is read with PdfPig (see *PDF reader* below); DOCM / DOTX / DOTM, XLSM / XLTX and PPTM / POTX / POTM / PPSX
  are read like DOCX, XLSX and PPTX.
- **HTML** is parsed with AngleSharp (`HtmlReader.cs`, `HtmlCss.cs`): headings, paragraphs, bold / italic /
  underline / strike / sub / sup / code / mark, the basic inline CSS (colour, background, font weight, style and
  size, text-align, text-indent, page breaks) and simple `tag` / `.class` rules of style sheets, nested lists with
  start numbers and types, tables with spans / header rows / borders, pictures (local files and data: URIs; remote
  pictures are skipped), links and anchors, quotes, `pre`, `hr` and figures. Scripts, styles, forms and `nav` are
  skipped. The encoding comes from the BOM, the XML declaration or `<meta charset>`, else UTF-8 or the system
  code page. No Pandoc needed.
- **MHTML** (`.mhtml`, `.mht`) is turned into its standalone page first (see *E-mail* below) and that page read like
  HTML; it refers to nothing but data: URIs, so no file outside the job's scratch folder is looked at.
- **ODT, RTF** are parsed by Pandoc into its JSON AST (`PandocAstReader.cs`, following
  [pypandoc-hwpx](https://github.com/msjang/pypandoc-hwpx)).
- Markdown, HTML and Pandoc keep structure only, so page setup comes from the built-in template (A4).

Element order and attribute values follow files saved by 한글 where the schema and 한글 disagree, for example:

- `hp:default` repeats paragraph margins and fixed line spacing doubled (`hp:case` holds the real values);
- page width/height are always those of the portrait sheet, `landscape="NARROWLY"` turns it;
- 한글 keeps a hanging paragraph's first line at `left` and indents the other lines;
- object offsets are unsigned: objects reaching into the margin are anchored to the paper instead;
- layout caches (table row heights, inline tab widths) are estimated, because simpler readers use them as-is.

Every generated file is checked in the tests with three independent readers: rhwp (renders it), Unhwp (reads the
text) and LibreOffice + H2Orestart (opens it), see `tests/Filee.Engines.Tests/DocxToHwpxTests.cs` and
`OfficeTests.cs`.

Not converted: Word charts, SmartArt, free-form shapes, tracked changes and comments.

### Equations

Word equations (`m:oMath`, `m:oMathPara`) become 한글 equation objects (`hp:equation`) with an equation script
(`Hwp/Hwpx/Docx/OmmlReader.cs` reads them into a small math tree, `Hwp/Hwpx/EquationScript.cs` writes the script),
so DOCX → HWPX and HWP keep editable equations and DOCX → PDF draws them with rhwp (#143):

- fractions (`{a} over {b}`, also without bar or written linear `a/b`), sub- and superscripts and scripts before the
  base, square roots and roots with an index, sums, products, unions and integrals with their limits, brackets that
  grow (`LEFT ( … RIGHT )`), matrices (`pmatrix`, `bmatrix`, `dmatrix`), cases, equation arrays (`pile`), function
  names and `lim` with what goes under it, accents (hat, bar, vec, dot, tilde …), over- and underlines;
- Greek letters, operators and arrows as 한글's commands (`omega`, `TIMES`, `LEQ`, `INF`, `rarrow` …), upright text
  (`m:sty="p"`, normal text) in `rm`, bold in `bold`; a display equation alone in its paragraph is centred;
- variables are written letter by letter when they read like a command (`x _{i n}`, not `in` = ∈);
- boxes, phantoms and group characters (braces above or below) keep only their content; other characters are
  written as they are.

The box an equation is stored with (`hp:sz`) is measured like rhwp lays the script out (estimated character
widths, script scale 0.7, fraction and root spacing, from rhwp's `equation/layout.rs`): rhwp stretches an equation to
its stored width, so a wrong box would distort it. 한글 lays the script out again when the equation is edited.
The model's other writers (HTML, EPUB, Markdown, TXT …) write a linear form instead, `(a+b)/(2c)`, `x_i^2`, `√(x)`,
which is also the equation's alternative text in HWPX.

## Spreadsheets

`SpreadsheetConverter` (engine id `spreadsheet`) converts between XLSX, XLS (and XLT templates, read only), ODS, CSV and TSV (also
`.tab`) in-process; every format goes through one in-memory workbook (`Office/Sheets/Workbook.cs`) whose cells keep
the text the spreadsheet shows, the value behind it and its Excel number format.

| Format | Read | Written |
|---|---|---|
| XLSX | own reader: values, number formats, fonts, fills, borders, alignment, merges, widths, heights, hidden rows / columns / sheets, frozen rows, print titles (rows) | every sheet, numbers and dates as numbers with their formats, booleans, styles, merges, widths, heights, hidden rows / columns, frozen rows |
| XLS (Excel 97–2003, also Excel 5/95) | ExcelDataReader (MIT): values, number formats, merges, horizontal / vertical alignment; widths, heights and hidden rows / columns from the BIFF records (`XlsLayout.cs`) | no (LibreOffice) |
| ODS | own reader (`content.xml`, `styles.xml`, `settings.xml`): repeated rows / columns (the empty repeats up to the end of the sheet are skipped), spans, every value type, the text Calc shows (`text:p`), data styles as Excel codes, cell styles, widths, heights, hidden rows / columns / sheets, frozen and header rows | every sheet, typed cells (float, percentage, currency, date, time, boolean) with data styles and the shown text, styles, merges, widths, heights, hidden rows / columns, frozen rows |
| CSV / TSV | delimiter (CSV: comma, semicolon or tab; TSV: tab) and encoding (UTF-8 / UTF-16 with BOM, CP949 when it looks Korean or on Korean Windows, else the PC's code page) | UTF-8 with BOM, CRLF, one file per sheet |

- Number formats: Excel codes and OpenDocument data styles are converted both ways (`OdsNumberStyles.cs`) for
  digits, decimals, grouping, thousands scaling, percent, scientific, currency symbols, literal text, colours,
  positive / negative / zero sections, dates, times and durations. Codes with conditions (`[>100]`), fractions or
  text between digits are written as plain numbers (General).
- Column widths use Excel's unit (a digit of Calibri 11 = 7 px); in ODS a character is 5.25 pt. LibreOffice
  measures with its own fonts, so a width can differ by up to a fifth after LibreOffice opens the file.
- XLS cells keep the default font, fill and borders (ExcelDataReader does not expose them); XLS output still
  needs LibreOffice. Password-protected XLS and ODS files stop with a clear message.
- Not converted: formulas (their cached results are kept), charts, pictures, comments, conditional formatting,
  data validation, print areas and page breaks.

The XLSX donut also offers **PDF + Edit** (`tool-sheet-studio`). Unlike an ordinary routed XLSX → PDF conversion,
this interactive tool always requires LibreOffice regardless of engine priority: it preserves the original workbook,
makes one disposable copy per selected sheet region, applies that region's print settings, and asks LibreOffice to
render it. PDFsharp combines the resulting PDFs. The live preview follows this exact studio export path and uses
PDFium/PDFtoImage for lazy thumbnails and the selected large page; the ordinary donut's PDF can differ because its
built-in XLSX route reads the workbook into Filee's document model.

## Fonts

Fonts are converted in-process (`Filee.Engines/Fonts`); zlib and Brotli come with .NET. The input format is
recognised by content, so a mislabelled file still converts.

- **WOFF 1.0**: every table zlib-compressed (stored as-is when that isn't smaller). Lossless both ways.
- **WOFF2**: one Brotli stream at the highest quality with the glyf/loca transform and, where it applies, the hmtx
  transform, so files come out the size of Google's reference encoder's. Reading handles both transforms, the
  overlap bitmap, untransformed tables and the first font of a WOFF2 collection. Like other encoders, Filee drops
  the DSIG signature and sets bit 11 of head.flags. Brotli is slow on big fonts (about 10 s per MB, so about a
  minute for a 4.6 MB CJK font); the job shows progress and can be cancelled.
- **EOT**: written as version 0x00020001 (names and OS/2 fields in the header, no MicroType Express compression, no
  XOR obfuscation, no URL restriction). Uncompressed EOT files are read (XOR-obfuscated ones too); MTX-compressed
  ones fail with a clear message.
- **TTF → OTF** keeps the TrueType outlines: TrueType-flavoured OpenType is valid OpenType and what systems and
  browsers expect; cubic outlines would gain nothing.
- **OTF with CFF outlines → TTF** (and → EOT, because Internet Explorer renders only TrueType outlines in EOT)
  converts the glyphs: Type 2 charstrings with subroutines, flex, seac accents and CID-keyed fonts are drawn, curves
  are approximated with quadratic splines within 1/1000 em (the approach of fontTools' cu2qu) and contours are
  reversed to TrueType's direction. glyf, loca, maxp 1.0 and post (format 2 with the CFF glyph names, format 3 for
  CID fonts) are built; head, hhea, hmtx and, from VORG, vmtx are updated; CFF, VORG and DSIG are dropped; every
  other table (cmap, name, OS/2, GSUB, GPOS, kern, ...) is kept. PostScript hints are not converted: the font gets a
  gasp table (smoothing at every size) and a prep that switches on dropout control instead.
- Not supported: CFF2 (variable) outlines to TTF or EOT (to WOFF, WOFF2 and OTF they convert), writing font
  collections, and WOFF/WOFF2 extended metadata (dropped).

`tests/Filee.Engines.Tests/FontTests.cs` checks every conversion with small OFL fonts in
`tests/Filee.Engines.Tests/Fonts` (see `OFL.txt` there): round trips keep every table byte for byte, a WOFF2 made
by Google's encoder decodes to the source font, and converted CFF glyphs are compared with the originals in Skia.

## DOCX writer

The counterpart of the HWPX writer: `Hwp/Hwpx/Docx/DocxWriter*.cs` writes WordprocessingML by hand from the same
document model, so every reader in `DocumentReaders` can produce an editable Word file without Word, LibreOffice or
Pandoc. It writes:

- sections with page size, orientation, margins (header and footer distances), columns, page number restarts;
  columns that change mid-page become continuous section breaks, as Word does it;
- headers and footers (odd/even pages, "different first page") with PAGE / NUMPAGES fields; a section without its
  own header gets an empty one, since Word would otherwise repeat the previous section's;
- paragraphs: alignment, indents (first line / hanging), spacing, line spacing (the model's percent of the font
  size is converted back to Word's multiple, the inverse of DocxReader), keep with next / keep lines / widow
  control, tab stops with leaders, page and column breaks;
- runs: Latin and East Asian fonts, size, bold, italic, underline, strike, colour, highlight (Word's named colours)
  or shading, super/subscript, letter spacing;
- headings as Word's built-in "heading 1".."heading 9" styles (outline levels, so the navigation pane and tables of
  contents work), lists as real numbering definitions (1., a., 가., ①, bullets, start numbers, nine levels);
- tables with column widths, horizontal and vertical merges, borders, fills, cell alignment, row heights and
  repeated header rows;
- pictures inline and floating (anchored to page, margin, column or paragraph, with wrapping), text boxes and
  rectangles / rounded rectangles / ellipses / lines as Word 2010 shapes (wps) with solid or gradient fills and
  outlines, hyperlinks (web and to bookmarks), bookmarks, footnotes and endnotes.

Parts, relationships and content types are written per part (pictures and links in headers and notes are related
from their own part), children in schema order. The tests validate every file with the Open XML SDK schema
validator, read it back with DocxReader (DOCX → model → DOCX → model keeps the layout) and convert it with
LibreOffice as an independent reader, see `tests/Filee.Engines.Tests/DocxWriterTests.cs`.

Its edges cost 8, less than Pandoc's and LibreOffice's direct conversions, so Markdown → DOCX, PDF → DOCX, ... use
it. DOCX → DOCX is no edge; the Word variants are the Office Open XML engine's job.

## PDF reader

`Pdf/PdfDocumentReader*.cs` reads PDFs with PdfPig (Apache-2.0) into the document model, for PDF → DOCX, HWPX and
(with `PdfTextConverter`) TXT and Markdown:

- words with their positions, fonts, sizes and colours → lines → paragraphs: a line continues the paragraph above
  when it follows at about one line pitch, unless it starts with a bullet or number, is indented as a first line,
  or the previous line ended although the next word would have fitted; hyphenated line ends are joined, and no
  space is added between Chinese or Japanese lines;
- tab stops: a line split at wide gaps ("1.  RFP Published   08/14/2026") stays one paragraph with tabs and left tab
  stops where its parts start, unless a column gutter lies between them; a gap counts as a tab after a list number,
  when it is wider than 6 em, or when a row right above or below has a tab at the same place (a form's value column);
- line breaks: in a paragraph narrower than its column (an address block), a line that ended although the next
  line's first word would have fitted keeps its line break;
- reading order: gutters between columns are found where no narrow paragraph crosses; paragraphs that span the
  columns (titles, figures, closing text) split the page into bands, read column by column;
- text that needs shaping (`PdfDocumentReader.ComplexText.cs`): an `/ActualText` of marked content replaces the
  glyphs it covers on every page (LibreOffice writes one for each cluster whose glyphs don't map to its characters
  one to one: conjuncts, vowel signs drawn before their consonant, ligatures). On a page with Arabic, Hebrew, Indic,
  Thai, ... text, words are built from the glyphs in content-stream order instead of by position, because marks
  have no width and overlap their letter: a mark joins the letter before it, a vowel sign drawn before its consonant
  (Word's PDFs keep that visual order) joins the consonant after it, and the dotted circles of PDFs made by rhwp
  (mapped to U+0002 or to a vowel sign, wider than any mark) are left out. Right-to-left words are read from the
  right, with numbers and Latin words in them left to right, a line's words likewise; a paragraph of mostly
  right-to-left lines is marked right to left (`HParaFormat.RightToLeft`), its alignment and indents measured from
  the right, and DOCX gets `w:bidi` on it and `w:rtl` on its Arabic or Hebrew runs (HWPX has no right-to-left
  paragraphs). Fonts keep their family names (`ArialUnicodeMS` → Arial Unicode MS, `NirmalaUI` → Nirmala UI,
  `NotoSansGujarati` → Noto Sans Gujarati). Not recovered: a cluster drawn as one glyph whose ToUnicode entry is
  wrong, in a PDF without ActualText (some Word PDFs), and the word spacing of rhwp's broken layouts;
- headings from font sizes (short paragraphs clearly larger than the body text, the largest size is level 1), bold
  and italic from the fonts, colours, links from link annotations, alignment, indents, spacing and line pitch;
- pictures: JPEG streams are kept as they are, others are decoded to PNG, and what PdfPig cannot decode is cut out
  of the rendered page; a picture covering the page behind text (the scan under an OCR layer) is skipped;
- ruled tables (`PdfDocumentReader.Tables.cs`): tables drawn with lines become real tables in their place in the
  reading order. The rules come from the page's vector paths: stroked straight lines and rectangle edges, and thin
  filled rectangles (up to 4 pt, the way Word and many generators draw borders). Rules on one line (within 1.5 pt)
  that overlap or leave a gap of at most 2.5 pt are joined; horizontal and vertical rules that meet or cross form a
  grid, and grid lines closer than 3 pt are one edge. The edges give the columns and rows (the column widths and
  minimum row heights of the result); where a rule covers less than half of the edge between two grid units, the
  units are one merged cell (column and row spans). A table without side or bottom rules still reaches as far as
  most of its rules do. Words go into the cell under their centre and are read like the rest of the page
  (paragraphs, line breaks, alignment within the cell, fonts, sizes, bold / italic, colours); each cell gets the
  borders that are drawn around it (weight, colour, dashed or solid), shading from a filled rectangle under it, and
  a vertical alignment from where its text sits. Only clear tables are rebuilt: at least 2 rows and 2 columns,
  every merged cell a rectangle, no rule through the middle of a word, and text in at least two cells. A box around
  a note, underlines, separators and empty grids (a chart's frame) stay as they were. Not rebuilt: tables without
  rules (aligned by white space only, or with only horizontal rules), nested tables (their text stays in the outer
  cell), rotated pages; a table running over several pages becomes one table per page. PDF → TXT keeps the cell
  text in reading order;
- page size per page and margins shared by the document; what sits at the top or bottom edge only because it is a
  page is left out: running headers and footers that repeat on most pages (digits ignored, so "Page 3" matches
  "Page 4"), running heads that change with the chapter (the same text at the same height on three pages or more,
  no larger than the body text, so a heading that starts several pages stays), bare page numbers ("12", "- 12 -",
  "Page 3 of 10", "第 3 页", roman "iv") and the same picture (a logo) at the same place on most pages;
- a page without text (a scan, a page of drawings, a turned page) becomes the rendered page (PDFium, 150 dpi JPEG)
  as a picture behind the text of an empty page, so nothing is lost; there is no OCR. PDF → TXT includes the
  invisible OCR text layer of scanned PDFs that have one.

Password-protected PDFs fail with a clear message. Not converted: tables without rules (their cells come out as
paragraphs in reading order), other vector drawings on text pages, form fields and annotations other than links.

**PDF → Markdown** is one step (`PdfTextConverter`, a `DirectOnly` edge, so PDF → HTML, EPUB, ... never start going
through a Markdown file): the document is written by `MarkdownWriter` as for e-books, with headings (`#`, not bold
inside), bold / italic and links. Indents are the PDF's layout, not block quotes, and the PDF's information title
(often a file name) gives no front matter. A Markdown file can't hold pictures, so `Text/MarkdownPictures.cs` writes
them into a folder next to it, reserved through the output allocator like any output (`report.md` →
`report_files/img0001.jpg`, `report_files (2)` when that folder is taken), and links them relatively, so editors
show them and the pair can be moved together; JPEGs are the PDF's own, a scanned page is the picture of the page.
The preset option *Leave out pictures* (`DocumentOptions.LeaveOutPictures`, Documents card, shown for Markdown)
writes the text only, read like PDF → TXT (the invisible OCR layer of scans included) and without a folder. The same
option applies to every Markdown writer: e-books (`EbookConverter`), HWP / HWPX (`UnhwpConverter`) and Pandoc (a
Lua filter drops the pictures, figures keep their captions).

## OFD reader

OFD (GB/T 33190-2016) is China's fixed-layout format: fully digitalized e-invoices (数电票), contracts and official
documents. `Ofd/OfdReader*.cs` reads it into the document model with no external engine, so OFD → DOCX and HWPX are
one step, → PDF two (rhwp), and TXT, Markdown, HTML, EPUB follow from there. OFD is read only (`Writable: false`) and
sits on the office donut (settings schema 27 teaches existing donuts `.ofd`).

- **Package** (`Ofd/OfdPackage.cs`): OFD.xml → the first document (Document.xml) → its pages, template pages and
  resource files (PublicRes, DocumentRes, a page's PageRes, and the unnamed `Res.xml` WPS writes next to them).
  Parts are looked up by name inside the ZIP and never written out under their own names; references are resolved
  against the referring part (or the package root for "/…"), "." and ".." applied, and anything that leaves the
  package, a URL or a drive path finds nothing. XML is read without DTDs; a part may unpack to 64 MB, a picture to
  32 MB (larger ones are left out), the whole package to 1 GB; at most 100,000 parts, 10,000 pages, 2,000,000
  drawing objects, page blocks nested 32 deep. DeltaX / DeltaY "g n d" repeats never yield more values than the
  text has characters.
- **Encryption**: a package with an `Encryptions.xml` (GM/T 0099) or encryption entries in OFD.xml is refused with a
  clear message; nothing is decrypted. Parts that are not XML (encrypted or damaged) fail the same way.
- **Text**: TextObjects with their Boundary, CTM (scale, also of the font size), Size in millimetres, TextCode X / Y
  (missing ones continue where the previous code ended) and DeltaX advances; DeltaY alone moving the characters is
  vertical text. A DeltaX list shorter than the text goes on with its last value while the characters are as wide
  (a title with one advance for all its characters); a step back with DeltaX or down with DeltaY inside one TextCode
  starts a new line, kept with a line break (the four lines of a VAT invoice's 密码区 cipher text are one TextCode).
  Fonts from the resource files, with common Chinese names written in English (宋体 → SimSun, 黑体 → SimHei,
  楷体 → KaiTi, 仿宋 → FangSong, 微软雅黑 → Microsoft YaHei, …) so Word and LibreOffice find them on any system; bold
  from `Weight` ≥ 600 or the font, italic, colour from the object (FillColor element or attribute; RGB, grey, CMYK,
  decimal or #hex channels), its DrawParam or the layer's DrawParam (tax invoices colour the template that way),
  following `Relative` chains. Invisible objects (`Visible="false"`) are left out.
- **Templates**: the template pages a page names are read first, so an invoice's labels (发票号码：, 名称：) and its
  values come out together. In template content, single characters stacked in one column (购买方信息, 备注) or a
  pair spread over a line (合　计) become one word, once the page's own text is there: a pair with a value between
  (共 4 页 of a sales list) stays apart.
- **Annotations** (Annots): the text and pictures of each annotation's appearance, placed from its boundary (a stamp
  drawn as a picture, a note). Hidden annotations and their paths (highlights, underlines) are left out.
- **Seals** (`OfdReader.Seals.cs`): an e-invoice's or a contract's seal is stored in its signature
  (`Signs/Sign_n/SignedValue.dat`, GM/T 0031 SES_Signature). The seal picture in it is shown where the signature's
  StampAnnot puts it, floating in front of the text. PNG, JPEG, GIF and BMP are kept; a seal that is a small OFD
  package of its own (the red oval of 全国统一发票监制章 with its characters set round the edge) is drawn with
  SkiaSharp at 12 pixels per mm on a transparent PNG: text per character with its CTM rotation, stroked and filled
  paths (lines, Béziers, arcs) and pictures. The DER is walked with every length checked against what is left,
  nesting and the element count capped; a SignedValue is read up to 4 MB, a seal package unpacks to 16 MB, at most
  64 signatures with 16 stamps each. The signature is not verified: what shows is the picture the file carries.
- **Layout** (`OfdReader.Layout.cs`): fragments on one baseline form a line, in reading order top to bottom and
  left to right; a line that ran to the right edge from at least half the measure continues in the next one
  (justified, first-line indent kept, no space between Chinese lines). Wide gaps (1.5 em) become tabs with left
  tab stops where each part starts, so an invoice's item rows keep their columns; a line under such a row whose
  parts start in its columns and hold no amounts is the wrapped end of its cells and follows after a line break in
  the same paragraph. Centred and right-aligned lines, left indents and vertical distances (space before) follow
  the page; page size per page (a new section when it changes), margins from what is drawn. Line distances are
  exact where the largest text fits ("at least" grows each line to the font's own height in Word and LibreOffice,
  ~1.4 em for KaiTi, and a full 210 × 140 mm invoice ran onto a second page); a one-line paragraph closer to the one
  above than 1.2 sizes gets the distance between them. A block that starts above the end of the one before (the
  invoice number beside the title) is pushed down, and the next gaps take that back, so the frame stays at its
  place. A table gets its distance from the block above through a 1 pt paragraph, and is `Tight` in HWPX: no 5 mm
  under it and no empty line in the paragraph that holds it. The first block's distance from the top margin is an
  empty line of that height (rhwp draws no space before at the top of a page).
- **Ruled tables** (`OfdReader.Tables.cs`): the approach of the PDF reader in millimetres. PathObjects
  (AbbreviatedData M/S/L/Q/B/A/C, with Boundary, CTM, line width and stroke colour from the object or its
  DrawParams) give stroked straight rules and thin filled rectangles; joined rules that meet form grids, and a grid
  of at least 2 × 2 units with rectangular merged cells and text in two cells becomes a table with the page's
  column widths and row heights, borders in the drawn colour and shading. Text goes into the cell of its first
  character (widths without DeltaX are estimated) and is laid out like the page, with exact line spacing so the
  rows keep their heights. The frame of an e-invoice becomes one table: buyer and seller blocks, the item list
  (one cell with tabbed rows: the invoice draws no rules between item columns), totals and remarks. A cell with one
  paragraph about as far from its top as from its bottom is centred; any other keeps its text's distance from the
  top as the cell's top padding (space before a cell's first paragraph is not drawn by every renderer), and only
  lines at about single spacing (1.6 sizes) are one paragraph. So the columns of a sales list (销货清单: rules only
  between the columns, each column one cell with all its rows) keep their rows level, the 小计 at the bottom of 序号
  beside its amount.
- **Pictures**: ImageObjects (MultiMedia of type Image) with the box from their CTM. PNG, JPEG, GIF and BMP are
  kept as they are (the format comes from the bytes, not the name), TIFF and JPEG 2000 are converted to PNG
  (ImageMagick, at most 100 megapixels), anything else (JBIG2) is left out. A picture alone in its band of the page
  becomes a paragraph; one beside or over text (a QR code next to the title, a seal on a table) floats at its place
  on the paper, in front of the text.

Not converted: signature checks (a seal shows whether or not its signature is valid), seal pictures in SVG,
embedded fonts (the font name is kept), composite objects and other vector drawings (the ⊗ before an invoice's
amount in words), attachments, more than the first document of a package, tables without rules (their text stays
in tabbed rows), clipping. Two text blocks side by side without rules share their lines. Renderers place text
differently inside an exact line (Word and LibreOffice at its foot, 한글 and rhwp at its head), so in the rhwp PDF
text sits up to 0.2 sizes higher than on the page. LibreOffice adds cell borders to row heights: the DOCX of a
full 140 mm invoice opened there puts its last line on a second page (rhwp's PDF keeps it on one).

## PDF → XLSX

`Pdf/PdfTablesConverter.cs` puts the tables of a PDF into a workbook (#267). The tables are the ruled ones PDF → DOCX
finds (`PdfDocumentReader.Tables.cs`, above): drawn with lines, with their merged cells, column widths and row
heights. Text outside the tables is left out, as the XLSX preset's hint says; tables aligned by white space only are
not found.

- **One sheet per table** ("Table 1", "Table 2" …). A table that goes on at the top of the next page with the same
  columns (widths within 5 %), right after one that ended its page, with no text between them, joins it on one
  sheet; a header row repeated on the new page is left out.
- **Numbers and dates** become real values with a number format that shows them as the PDF did: `1,234.50`
  (`#,##0.00`), `(120)` (negative), `$9.99` / `€1,000.00` / `₩` `£` `¥` `₹` (the symbol in the format), `12.5%`
  (`0.0%`), and dates with the year first, `2026-10-23`, `2026/1/5`, `2026. 10. 23.` (Korean style). Everything else
  stays text: a leading zero (codes, phone numbers), more than 15 digits, a decimal comma (`1.234,50`, which can't be
  told from thousands), day- or month-first dates.
- Every cell gets thin borders (the table was drawn with lines), its shading, vertical alignment and bold when all
  its text is bold; text wraps. Merged areas keep their borders on every covered cell.
- A PDF with no such table, or a scan (no text), fails with `error.pdf_no_tables` instead of writing an empty
  workbook. The same tables can be written as ODS, or as CSV (one file per sheet). These edges are direct only: an image
  made into a PDF has no ruled tables, so no picture reaches a spreadsheet through PDF.

## PDF → PPTX

`Pdf/PdfSlidesConverter.cs` turns a PDF into a PowerPoint deck of pictures: every page is rendered (PDFium, as for
PDF → images) and becomes one picture on its own slide. The slides look exactly like the PDF but are **not
editable**: there are no text boxes to change, the PDF's layout is not rebuilt. PDF → DOCX is the editable route.

- **Slide size** is the first page's own size (an A4 portrait PDF gives A4 portrait slides, a PDF exported from a
  16:9 deck gives 16:9 slides), within PowerPoint's limits of 1 to 56 inches a side. Every page, also one of another
  size or orientation (pages turned in the PDF are shown turned), is fitted onto the slide without cropping and
  centred on white.
- **Resolution** is the preset's render DPI, at least 150 and at most 600; a page larger than 25 megapixels at that
  resolution (a poster) is rendered at a lower one. The preset's page range picks the pages.
- **Pictures** are JPEG at the preset's quality (90 by default, as the "PPTX (slides)" preset sets it). Pages are
  rendered on white, as PDF viewers show them, so they have no transparency that would need PNG, and JPEG keeps a
  long deck at a size that is easy to send (an A4 text page at 150 dpi is a few hundred KB).
- **Speaker notes** hold the page's text, read like PDF → TXT (reading order, the invisible OCR layer of scans
  included), so it can still be searched and copied. Pages without text get no notes; when PdfPig cannot read a PDF
  that PDFium renders, the deck is written without notes.
- The package (`Pdf/PictureDeckWriter.cs`) is written by hand: presentation, one slide master with a blank layout,
  a theme, the slides with their pictures, a notes master and notes slides, and the document properties. The tests
  validate it with the Open XML SDK and read it back with Filee's own PPTX reader.
- The edge is `DirectOnly`: the route planner uses it only for a PDF converted to PPTX. A Word document or an image is
  never turned into picture slides through PDF, and a picture deck is never a step towards ODP, PPSX or another
  format (a test checks that every other pair of formats keeps its route).

Password-protected PDFs fail with the same message as PDF → DOCX.

## PDF compression

"Compress PDF" (and any PDF → PDF preset with *Compress* set, also with a page range or one file per page) makes a
PDF smaller while it stays a PDF, for e-mail attachments and upload limits. `Pdf/PdfCompressor.cs` does it with
PDFsharp and Magick.NET; nothing has to be installed.

| Level | Pictures scaled down to | JPEG quality |
|---|---|---|
| Smallest (`Screen`) | 72 dpi | 55 |
| Balanced (`Ebook`, the preset's) | 150 dpi | 65 |
| High quality (`Print`) | 300 dpi | 85 |

- **Pictures**: `Pdf/PdfPictureUses.cs` follows the content streams of the pages, of the forms they draw and of
  annotation appearances (`q`/`Q`/`cm`/`Do`) to find the largest size each picture is drawn at. A picture drawn at more
  than 1.5 × the level's resolution is scaled down to it (as Ghostscript does); a picture whose drawing can't be
  followed is taken as large as the page holds it, so it is never scaled down too far. Photos are saved as JPEG at the
  level's quality, pictures with up to 256 colours (charts, screenshots, logos) losslessly (Flate with PNG
  prediction). A JPEG that keeps its size is encoded again only when that saves at least 10 %. A picture is replaced
  only when the result is smaller.
- **Which pictures**: 8 bits per component, gray, RGB (also ICC-based and calibrated) and CMYK, stored as JPEG, Flate
  (with or without PNG prediction), RunLength or uncompressed. CMYK JPEGs, 1-bit pictures and masks, indexed
  (palette) pictures, spot colours, JPEG 2000, JBIG2, CCITT and pictures with a colour-key mask stay as they are.
- **Transparency**: a soft mask is scaled with its picture and kept lossless, so transparent parts stay transparent.
- **Duplicates**: a picture stored more than once with the same data (a logo embedded again on every page) is kept once.
- **Everything else**: every stream is deflated at the highest level when that's smaller; page thumbnails and other
  applications' private data (`PieceInfo`, such as Illustrator's editing data) are removed, as are unused objects.
  When the preset doesn't keep metadata, the XMP metadata of pages and pictures goes too. The document's own XMP is
  written afresh from its document information by PDFsharp, as for every PDF it saves.
- **Unchanged**: text, fonts, vector drawings, links, bookmarks and form fields. The whole file is compressed as it
  is; a page range or single pages are first copied into new PDFs, as without compression (which leaves bookmarks and
  form fields behind), and so is a PDF locked only against changes.
- **Never bigger**: when nothing gets smaller (a PDF of text only, one compressed before), the PDF is written as it was
  and the toast (and `filee convert`) says that it couldn't be made smaller. Password-protected PDFs fail with the same
  message as the other PDF conversions.
- **Ghostscript** (optional, *Also try Ghostscript* in the preset): when it is installed, the PDF is also run through
  `pdfwrite` with `-dPDFSETTINGS=/screen`, `/ebook` or `/printer` (the same 72, 150 and 300 dpi) and
  `-dCompatibilityLevel=1.6`, and the smallest of the two results is kept; when Ghostscript fails, the built-in result
  stands. It is off by default: Ghostscript rewrites the whole file (fonts are subset again, structure and form fields
  may change), so it is the user's choice. It also writes object streams, which PDFsharp can't, and its JPEGs are
  somewhat smaller at the Balanced level.
- **Not done**: object streams (PDFsharp writes none), font subsetting or removal of unused glyphs, converting
  colour to gray, flattening transparency or form fields, recompressing JBIG2/CCITT scans.

## Media tools

Everyday presets, on the back faces of the images, PDF, video and audio donuts where they fit ("Video from images" is
in the preset library only). Video and audio need FFmpeg.

- **Compress image / video / audio** re-encode smaller (quality 75, never above a JPEG's own and PNG at zlib level 9;
  H.264 at the Small quality, at most 1080p; AAC at the Small quality). A copy in the same format that isn't smaller is
  replaced by the source, with the same notice as Compress PDF (`OutputRule.OnlyIfSmaller`); under "Save as ZIP" the
  source goes into the ZIP instead.
- **Extract audio** copies AAC or ALAC into M4A without an encode (`MediaOptions.CopyAudioWhenPossible`); other sound
  is encoded to AAC.
- **Merge videos** copies the clips with the concat demuxer only when all share the codec setup (ffprobe's extradata
  hash), size, pixel format, frame rate, turn and kind of sound; otherwise every clip is fitted into the first clip's
  frame and encoded, a clip without sound silent for its length. **Merge audio** always decodes and joins the sound
  into one M4A, so no encoder padding or gap is left between files. Both are in `Media/MediaJoining.cs`.
- **Video from images**: each picture is fitted into 1920 × 1080 as a JPEG, then shown for the preset's seconds with
  crossfades (`xfade`) of 1 s, at most half a picture's time, or cuts. At most `MediaOptions.MaxSlides` (300)
  pictures, not counting downloads that failed; the queue refuses more before any is converted. A filter graph too
  long for a command line goes into a `-/filter_complex` file.
- **Remove metadata** keeps the format. Images lose EXIF, XMP, IPTC, comments and the ICC profile, and a JPEG keeps
  its own quality. Video and audio are copied without global and per-stream tags, chapters and data tracks; an MKV's
  attachments (subtitle fonts) stay, with their file name and type. PDFs lose their document information and every
  XMP packet (`PdfSharpConverter.StripMetadata`), and PDFsharp writes a new one holding only its own name and the save
  date; pages, links, bookmarks and form fields stay.

## PDF watermark, page numbers and password

A preset that writes PDF can finish every PDF it makes (`PdfOptions.Watermark`, `PageNumbers`, `Password`;
`Pdf/PdfFinishing.cs`, PDFsharp only). `JobQueue` runs it (`IPdfFinisher`) on each PDF a file became, whichever engine
wrote it (PDFsharp, rhwp, LibreOffice, Ghostscript …), and once on a merged PDF, so its pages are numbered through.

- **Watermark**: the text in light grey (22 % opacity), bold, along the page's diagonal over the content, at 70 % of
  the diagonal and at most a quarter of the short side high.
- **Page numbers**: "3 / 10", 9 pt dark grey, centred 10 mm above the bottom edge.
- Both go on the page as it is shown: centred in its crop box (the media box when it has none), the right way up
  for its `/Rotate`. The page is drawn on unturned and its `/Rotate` put back.
- Text is drawn as glyph outlines (`Vector/GlyphOutlines.cs`, HarfBuzz shaping, shared with SVG text in PDFs). Any
  script works with a font the PC has for it (Skia's font fallback, else Arial or the default face), and no font is
  embedded, so the PDF grows by a few kilobytes. The text can't be selected or searched.
- **Password**: opens the PDF (user and owner password are the same), AES-256 (`SetEncryptionToV5`). It is saved with
  the preset in the settings file, but left out of a preset export and of the copy history keeps to convert failed
  files again: a retry takes it from the preset again, and fails (`error.pdf_password_gone`) when the preset no
  longer has one. The editor takes at most 127 UTF-8 bytes, all that AES-256 readers use, and turns the box off while
  PDF/A is ticked, since PDF/A forbids encryption.

## Office Open XML variants

`Office/OoxmlConverter.cs` converts between the variants of a family (Word, Excel, PowerPoint) by copying the
package with the main part's content type rewritten. Going to a macro-free type also removes the VBA project, its
signatures and data, Word's key map customisations and Excel 4.0 macro sheets (with their sheet entries), and every
relationship and content type entry pointing to them. The built-in readers take the variants directly (DOCM → HWPX,
XLSM → CSV, PPSX → DOCX ...).

## E-mail

`Email/EmlConverter.cs` reads .eml files with MimeKitLite (MIT), which decodes quoted-printable and base64, encoded
headers (RFC 2047 / 2231) and charsets including EUC-KR / CP949 and ISO-2022-KR; raw 8-bit header text that is not
UTF-8 is read as CP949, as older Korean mail programs wrote it.

Outlook .msg files are read with MsgReader (MIT, `Email/OutlookMessage.cs`) and rebuilt as a MIME message with
MimeKit: sender, To / Cc / Bcc, subject, sent date, Message-ID, the text and HTML bodies (MsgReader takes the HTML out
of RTF-only messages), attachments, pictures the HTML shows (`cid:`, kept with the body) and attached Outlook items
(as attached .eml messages). That message then goes the EML way below, so MSG → HTML / TXT / ZIP are direct edges
(cost 10; through EML would be 20 and parse twice) and **MSG → EML** writes the rebuilt message. Attachments that only
link to a file elsewhere (`ATTACH_BY_REFERENCE`) are not followed. MsgReader brings OpenMcdf (MPL-2.0, the compound
file reader), RtfPipe (MIT) and UTF.Unknown (MPL-1.1, charset detection for old ANSI messages).

- **EML → HTML**: a standalone page with From / To / Cc / Date / Subject / Attachments and the HTML body (its style
  sheets kept, scripts removed) or the text body; pictures sent inline (`cid:`) become data URIs.
- **EML → TXT**: the same headers and the text body (or the HTML body as text).
- **EML → ZIP**: the attachments (attached messages as .eml) with safe, unique file names.

### MHTML web archives

A web page saved as "Webpage, Single File" (`.mhtml` from Chrome and Edge, `.mht` from Word and Internet Explorer)
is a MIME message like an e-mail: the page and its pictures, style sheets and frames as parts of a
multipart/related body (RFC 2557). `Email/MhtmlArchive.cs` reads it with the same MimeKit parser and writes the page
as **one HTML file** (`MHTML → HTML`, engine `email`); the HWPX and DOCX writers read archives directly through the
same code (`DocumentReaders`), so MHTML → DOCX / HWPX is one step and → PDF two (rhwp), and TXT, Markdown, EPUB and
the other HTML targets go on from the page.

- **The page** is the root of multipart/related (its `start` parameter, else the first part), else the first
  text/html part. Its charset parameter decodes it; without one (Chrome writes none) or with a wrong "us-ascii" over
  8-bit text, the BOM, `<meta charset>` or UTF-8 decide, as for HTML files. Quoted-printable and base64 are undone
  by MimeKit.
- **References** are matched against the parts' Content-Location, as written or resolved against the page's own
  location or its `<base href>` (Chrome: `https://…`; Word: `file:///C:/…/page_files/image001.png` named relatively
  from the page), and `cid:` against Content-ID (Chrome names its style sheets `cid:css-…@mhtml.blink`). These are
  compared as text only: nothing is downloaded, and no Content-Location is ever opened or written as a file.
- **Pictures** of the archive (`img src`, `srcset`, `<picture>` sources, the `background` attribute and CSS `url()`)
  become data URIs; raster only (PNG, JPEG, GIF, WebP, BMP, AVIF, also when saved as application/octet-stream), not
  SVG. Linked style sheets the archive holds are inlined as `<style>` (with their `@import`s, four levels deep);
  `url()`s that point elsewhere (web fonts, remote backgrounds) become `none`, and `<` in CSS is escaped so a style
  sheet cannot end its element.
- **Safety**: the result goes through the e-mail sanitizer (`EmlConverter.SafeHtml`): scripts, frames, forms,
  `<link>`, `<base>`, `<meta>`, inline SVG / MathML, event handlers and `javascript:` links are removed, the
  `Content-Security-Policy` meta allows only inline styles and data: pictures. Links stay when they are http(s) or
  mailto (made absolute against the page), anchors into the page itself become `#id`.
- **Limits**: archives over 256 MB are refused; a picture over 32 MB (decoded) is left out, as is any picture once
  the page's data URIs reach 192 MB (a picture used many times counts each time); 8 MB of style sheet text per page.

Tests (`tests/Filee.Engines.Tests/MhtmlTests.cs`) write archives in code: a Chrome-style one (quoted-printable page
with the charset only in its meta tag, a `cid:` style sheet, pictures by absolute and relative URL and from CSS, one
saved as octet-stream, and scripts, frames, remote pictures, fonts and style sheets that must go), a Word `.mht`
(windows-1252, `file:///C:/` locations, VML fallbacks), a `cid:`-only page in EUC-KR, a picture on disk named by a
`file:` URI that must not be read, and the routes to HTML, TXT, DOCX, HWPX and PDF.

## Data (JSON, YAML, TOML, XML, CSV, vCard, iCalendar)

`Data/DataConverter.cs` (engine id `data`) reads every format into one tree (`Data/DataTree.cs`: objects with their
keys in file order, arrays, text, true / false, integers of any size, floats, TOML dates and null) and writes it
again, so structure and types carry over as far as the target can hold them. Output is UTF-8 without BOM with LF
line ends, indented by two spaces (CSV: like the spreadsheet engine, UTF-8 with BOM and CRLF for Excel). There are no
options: the output is always pretty-printed.

| Format | Library | Read | Written / lost |
|---|---|---|---|
| JSON (`.json`) | System.Text.Json | comments and trailing commas allowed (JSONC) | non-ASCII text as it is; NaN / infinities as text ("NaN") |
| HAR (`.har`, read only, #328) | System.Text.Json (`Data/HarData.cs`) | the HTTP Archive a browser's developer tools save: as JSON for JSON, YAML, TOML, XML and JSON Lines; as a table for CSV (and spreadsheets, Markdown, PDF through it), one row per request of `log.entries`: started, method, URL, status, time (ms), size (bytes), type. Pages, timings and headers stay in the JSON. A file without `log.entries` fails as no HAR | — (not written) |
| JSON Lines (`.jsonl`, `.ndjson`) | System.Text.Json | one value per line, blank lines skipped, read as an array | one compact line per record (the array, or the array inside wrappers, see CSV) |
| YAML (`.yaml`, `.yml`) | YamlDotNet (MIT) | YAML 1.2 core schema: unquoted `true`, `null` / `~`, numbers (`0x`, `0o`, `.inf`, `.nan`) are typed, the rest and anything quoted is text (`yes` / `no` stay text); explicit `!!str` / `!!int` … tags; anchors and aliases resolved (copied), merge keys (`<<`) applied; several documents (`---`) become an array; comments dropped; alias "bombs" (more than 5 million values) and nesting deeper than 256 refused | block style; text that would read back as another type (`"true"`, `"007"`, `""`, YAML 1.1's `yes` / `on`, dates) is quoted; multi-line text as a literal block (`\|`) |
| TOML (`.toml`) | Tomlyn (BSD-2-Clause, TOML 1.1) | dates and times keep their TOML spelling (text in JSON / YAML) | written by Filee: plain keys, then `[tables]` and `[[arrays of tables]]`; **null values are left out** (TOML has none); a root that is not an object goes under `items` (`[[items]]`); integers beyond 64 bits as text |
| XML (`.xml`) | System.Xml | root element = an object with one key; attributes as `@name`, text next to attributes or children as `#text`, repeated elements as an array (**one child is no array**), `<a/>` → null, `<a></a>` → empty text, CDATA as text; all values are text (XML has no types); comments, processing instructions and the DTD dropped, external entities never loaded; the encoding from the declaration (EUC-KR …) | a root that is not one object with one key goes into `<root>`, items of nested arrays into `<item>`; names that are not XML names are escaped (`first name` → `first_x0020_name`, unescaped when read back); `x:name` keeps its namespace when `@xmlns:x` declares it; control characters XML 1.0 can't hold are removed |
| CSV (`.csv`) | Filee (`Office/Sheets/CsvFormat.cs`) | first row = keys, one object per row; plain numbers (not `007`) and `true` / `false` typed, the rest text; delimiter and encoding detected as for spreadsheets | only tables: an array of objects (columns = every key, first seen first; an object inside a row becomes `parent.child` columns, one level only), an object of arrays (columns), an array of arrays (rows) or one flat object (one row); objects that only wrap the array (`{"users": […]}`, `<rows><row/>…</rows>`) are looked through. Anything else stops with a message saying what is not a table |

**`.csv` and `.xml` ownership.** CSV stays a spreadsheet format: the spreadsheets donut, XLSX / ODS routes and
"Merge into PDF" are unchanged. The data engine adds `csv ↔ json / jsonl / yaml / toml / xml` edges, and vCard and
iCalendar each convert to and from CSV. Routes combine: XLSX → CSV → JSON, YAML → CSV → XLSX, vCard → CSV → JSON,
and ICS → CSV → JSON. The spreadsheets donut gets "JSON". `.xml` had no owner before; XML-based formats Filee
converts have their own extensions (`.svg`, `.fb2`, `.dbk`, `.opml` …), so a plain `.xml` file is treated as data
and opens the data donut.

**vCard** (`Data/VCard.cs`, own parser): vCard 2.1, 3.0 and 4.0 → CSV with the columns Full name, Given name, Family
name, Organization, Title, Email, Phone, Address, Birthday, Note (one row per contact; several values joined with
`; `, each with its kind in brackets: `010-1234-5678 (mobile); 02-123-4567 (work)`). Folded lines, quoted-printable
with soft line breaks, `CHARSET` parameters, escapes, groups (`item1.EMAIL`), 2.1's bare types (`TEL;CELL`) and 4.0's
quoted type lists and `tel:` URIs are read; a value without `CHARSET` is UTF-8 when valid, else CP949 (address books
of older Korean phones); UTF-16 files with a BOM are read too. Pictures and nested cards (2.1 `AGENT`) are skipped,
a missing full name is made from the name parts (홍길동, "Ada Lovelace"). CSV → vCard reads those columns (also
`Name`, `Company`, `Mobile`, `E-mail` … and Korean / Chinese headers such as 이름, 전화번호, 이메일, 姓名, 电话) and writes
vCard 3.0 in UTF-8, lines folded at 75 bytes; an address goes into the street field.

**iCalendar** (`Data/ICalendar.cs`, own parser): VEVENTs and VTODOs ↔ CSV; one component per row, with these 14 columns
in order: `Type`, `Title`, `Start`, `End`, `All day`, `Time zone`, `Location`, `Description`, `Organizer`,
`Attendees`, `RRULE`, `EXDATE`, `Recurrence ID`, `UID`. Times use ISO 8601, retaining the original offset;
all-day values are dates only, and a DTSTART TZID is kept in its own `Time zone` column. If a TZID cannot be
resolved, the local time stays floating without an offset. A VTIMEZONE in the same calendar without RRULE takes
precedence; RRULE-based zones are resolved through the system time-zone database instead, so historical years are
not guaranteed to match. CSV → ICS writes the normalized columns as VEVENTs or VTODOs. A numeric offset without a
`Time zone` is written in UTC. Each VTIMEZONE is built from the system rule in force at that zone's earliest timed
Start (or End when no Start); offsets in other years, including past years, may therefore be lost. The original
VTIMEZONE definition is not preserved, and only these columns are supported rather than arbitrary calendar data.
VALARM, X-properties, VJOURNAL, VFREEBUSY, binary attachments and other unsupported properties are lost.

## CAD drawings (DWG, DXF)

Built in, no AutoCAD or ODA software (`Filee.Engines/Cad`). [ACadSharp](https://github.com/DomCR/ACadSharp) reads
and writes the files; Filee's own renderer draws them with SkiaSharp (one drawing routine for PDF, SVG and images).

- **Reading**: DWG from R14 (AC1014) to the AutoCAD 2018 format (AC1032, still current in AutoCAD 2026); DXF R12 to
  2018, ASCII and binary. DWG R13 and older fail with "version not supported". Korean AutoCAD writes pre-2007 DXF with
  the code page name `ANSI_949`, which ACadSharp 3.8 does not map; Filee rewrites that header in memory so the text
  is decoded as CP949 (`CadFile.OpenDxf`, covered by a test that fails once ACadSharp handles it).
- **Writing** (DWG ↔ DXF, and DWG → DWG / DXF → DXF in another version): **AutoCAD 2018 (AC1032)** by default — the
  current DWG format (AutoCAD 2018 and later, DWG TrueView, BricsCAD, ODA tools); 2018 DXF stores text as UTF-8.
  A preset can choose an older version (#489, `CadOptions.Version`): 2013, 2010, 2007, 2004, 2000, or the source's
  own version (older than 2000 becomes 2000). ACadSharp 3.8 can't write a 2007 DWG: chosen, it is written as 2004,
  which AutoCAD 2007 opens; kept from a 2007 source, as 2010. Before 2007, text is stored in the drawing's code page
  (`$DWGCODEPAGE`), so `CadCodePages` picks the one that holds the most of the drawing's text (its own on a tie;
  Korean for Hangul, Japanese for Kana, Big5 before GBK for Chinese, East Asian pages only for East Asian text) and
  writes any character it can't hold as `\U+XXXX` (one `?` for a character outside the BMP). Names count and are
  escaped too: layers, blocks, text and dimension styles, linetypes, layouts and attribute tags, so a Korean layer in a
  drawing with only English text gets `ANSI_949`. Text inside table cells (2004 and later) isn't escaped. In a DWG, the
  header's code page index is ACadSharp's (`KSC5601` / `GB2312`), the DXF says `ANSI_949`; both read back the same. Korean is written as `ANSI_949`, as AutoCAD names it (ACadSharp
  needs `KCS5601` to encode it, so the DXF header is renamed after writing). Objects the chosen version doesn't
  have are left out with a note: tables and arc dimensions before 2004, multileaders and PDF underlays before 2007,
  meshes before 2010. ACadSharp's DWG writer is younger than its reader
  and its DXF writer, so DXF is the safer format to hand on. Objects ACadSharp does not model (proxy and AEC objects,
  some 3D and underlay data) are not written; degenerate dimensions it cannot measure are left out of DWG output
  instead of failing the file.
- **What is drawn**: model space seen from the top (Z is dropped). If model space has nothing visible, the active
  layout (paper space) is drawn instead (for a PDF, every layout, see below). The page is fitted to the extents of
  what is actually drawn — the header's EXTMIN/EXTMAX are ignored, so drawings far from the origin still fill it.
  - PDF: an ISO A3 sheet (A4 or Letter when the preset asks for it), landscape when the drawing is wider than tall,
    10 mm margins (or the preset's margin). Vector paths, text as text with embedded fonts.
  - PDF of a drawing with paper-space layouts (#172): **one page per layout** with something on it, in tab order,
    each fitted like the one-page PDF to the layout's drawing and viewport frames. Inside each viewport model space
    is drawn at the viewport's scale (height ÷ view height) around its view centre, clipped to the frame, under the
    layout's own drawing (title block, notes). Viewports that are off are skipped. When no layout has anything of its
    own besides viewports (a drawing made in model space, whose Layout1 only holds the viewport AutoCAD added), the
    PDF stays one page of model space. Not followed: twisted views (drawn untwisted), views from another direction
    than the top (drawn from the top), layers frozen per viewport, non-rectangular viewports and the layout's plot
    settings (paper size, plot scale). Images and SVG stay one picture of model space.
  - SVG: the drawing's aspect ratio with a 420 mm (A3) long edge at 96 px/inch, white background, text as `<text>`.
  - Images: the same page rendered at the preset's render resolution (150 dpi → 2480 px on the long edge, at most
    12 000 px / 80 MP), then the preset's image options (resize, grayscale, quality, DPI) as for other images.
- **Entities**: LINE, XLINE/RAY (clipped to the page), LWPOLYLINE and POLYLINE (bulges, constant and tapered widths,
  spline-fit and 3D), CIRCLE, ARC, ELLIPSE (also partial), SPLINE (NURBS from control points, knots and weights, or
  a smooth curve through fit points), TEXT, ATTRIB and MTEXT (justification, rotation, width factor, oblique,
  mirroring; MTEXT word wrap, height/colour/font/width codes, stacked fractions shown as `1/2`, `%%d` `%%c` `%%p`,
  `\U+` and `\M+` escapes), INSERT (base point, scale, rotation, MINSERT arrays, nesting, attributes), DIMENSION and
  TABLE (their anonymous block), LEADER, MULTILEADER (lines, arrows, text or block content), HATCH, SOLID/TRACE,
  3DFACE edges, POINT, MLINE, polyface and polygon meshes (edges).
- **Hatches**: solid fills, gradients as a linear gradient, patterns as their real pattern lines clipped to the
  boundary; a pattern too dense to draw (over 20 000 segments) becomes a light tint of its colour.
- **Colours and lines**: ACI index table and true colour, BYLAYER / BYBLOCK (inside blocks, entities on layer "0"
  follow the insert's layer). White and near-white (ACI 7, which AutoCAD shows white on black) draw black on the white
  page. Line weights are plotted in millimetres ("Default" as a thin 0.18 mm line); line types become dash patterns
  scaled by LTSCALE, the entity's scale and the block scale (shapes and text inside complex line types are omitted).
- **Layers**: off, frozen and non-plotting layers (including Defpoints) are not drawn, like a plot. A frozen layer
  hides a whole block reference; an OFF one only its layer-0 contents.
- **Fonts**: drawings name SHX fonts, which are not available, so text uses the text style's TrueType font when it is
  installed (arial.ttf, malgun.ttf ...) and otherwise a sans-serif face (Noto Sans, Arial, Liberation Sans ...).
  Characters the font lacks fall back per character to a CJK font (Noto Sans KR/CJK, Malgun Gothic, Apple SD Gothic
  Neo, ...). These come from the fonts SkiaSharp finds on the system; without any, SkiaSharp's default typeface is
  used and the geometry is unaffected.
- **Not drawn** (counted per type in the log): 3DSOLID, REGION and BODY (ACIS), IMAGE, OLE objects, PDF/DWF/DGN
  underlays, WIPEOUT (its masking is not applied), SHAPE, TOLERANCE, proxy entities. MTEXT columns, tabs, underline
  and overline are ignored.
- **Limits**: 1 000 000 drawn primitives, block nesting depth 16, 10 000 MINSERT cells, 2 000 000 hatch pattern
  segments in total; beyond that the drawing is cut short with a warning in the log. Damaged or truncated files fail
  with "The file is not a valid DWG/DXF drawing or is damaged: …".

## 3D models

`Filee.Engines/Models3D` converts GLB, glTF, STL, OBJ, 3MF and PLY into each other, and FBX into any of them, in
managed code: every reader
fills one in-memory model (`Model3D`: parts with positions, normals, UVs, vertex colours and triangles; materials
with a base colour and a base colour texture) and every writer takes it, so each pair is one step. glTF and GLB are
read and written with [SharpGLTF](https://github.com/vpenades/SharpGLTF) (MIT); STL, OBJ/MTL, PLY, 3MF and the FBX
reader are Filee's own code. No native library (Assimp, the FBX SDK) and no download.

- **Units**: STL, OBJ and PLY have none; they are read and written as **millimetres**, as slicers and CAD programs
  treat them. 3MF has a `unit` attribute (micron to metre) and is written in millimetres. glTF is metres. So a 20 mm
  STL cube is 0.02 m in GLB and 20 mm again in STL. FBX has `UnitScaleFactor` (centimetres per unit, 1 when
  missing). There is no preset option for another unit.
- **Axes** follow Blender's defaults: STL, 3MF and PLY are Z up, OBJ and glTF Y up, so a part standing on a printer's
  bed stands upright in a glTF viewer and comes back on its base. FBX says which axis is up (`UpAxis`, `CoordAxis`
  and their signs in GlobalSettings; the front axis follows, as FBX is right-handed), so 3ds Max's Z-up files stand
  up too.
- **Structure**: node hierarchies, 3MF components and build items are flattened: every mesh instance becomes a part in
  world coordinates (one glTF node / OBJ object / 3MF object per part). Polygons are triangulated (ear clipping, so
  concave faces are right). OBJ negative indices, `\` line continuations and 0–255 vertex colours are understood.
- **Vertices**: binary STL is streamed in blocks and vertices are merged by position (a 1,000,000-triangle STL
  converts to GLB in a few seconds with about 150 MB of memory). STL normals are not kept: GLB written from STL has
  none, which glTF viewers must shade flat; STL output gets facet normals from the winding. 3MF output welds vertices
  by position, so meshes split for normals or UVs stay closed for slicers.
- **Colours**: vertex colours are kept as sRGB and converted to linear for glTF `COLOR_0`; material colours are glTF's
  linear base colour, OBJ's `Kd` as it is and 3MF's sRGB `displaycolor`. glTF materials are written metallic-roughness,
  not metallic, roughness 0.8.

What each format keeps:

| Format | Read | Written |
|---|---|---|
| GLB / glTF | default scene (else all root nodes), triangles, strips and fans, `POSITION`, `NORMAL`, `TEXCOORD_n` of the base colour texture, `COLOR_0`, base colour factor and texture (metallic-roughness or specular-glossiness), double-sided; `.gltf` with embedded (data URI) or separate `.bin` and picture files | one node and mesh per part, 16- or 32-bit indices, PNG/JPEG textures (others are converted to PNG); **`.gltf` is one self-contained file** (its buffer is a base64 data URI, about a third larger than GLB) |
| STL | binary (also with a header starting with "solid") and ASCII (one part per `solid`) | binary, one solid |
| OBJ + MTL | `o` / `g` / `usemtl` as parts, `v` (+ r g b), `vt`, `vn`, faces of any size; `mtllib` with `Kd`, `d` / `Tr`, `map_Kd` (options skipped) next to the file | **the `.mtl` and the textures are written next to the `.obj`** (`name.obj`, `name.mtl`, `name_texture.png`, named like the output); vertex colours as `v x y z r g b` |
| 3MF | objects, components (also in other model parts, as Bambu Studio writes them), build transforms, base materials, colour groups (materials extension); `support` / `other` objects skipped | one object per part, base materials from material colours, a colour group from vertex colours |
| FBX (read only) | binary 6.x–7.x (64-bit records from 7.5, zlib-compressed arrays) and ASCII; mesh geometry with polygons of any size, the first normal, UV, colour and material layer (by polygon vertex, control point or polygon, or all the same; direct or indexed); the model hierarchy from the connections with translation, rotation (every Euler order, pre- / post-rotation, pivots and offsets), scaling and geometric transforms; property templates from Definitions; material `DiffuseColor` and `Opacity`; the diffuse texture embedded (Video `Content`) or named next to the file; animation, skins, blend shapes, NURBS, cameras and lights skipped (the rest pose is read) | — (not written) |
| PLY | ASCII, binary little / big endian; vertex position, normal, UV (`s`/`t`, `u`/`v`, `texture_u`/`texture_v`), colours of any type; face lists with per-corner `texcoord` and face colours; `comment TextureFile`; other elements skipped | binary little endian, one mesh; material colours become vertex colours |

Lost on the way (no place in the target): **STL** keeps only the triangles (no colours, UVs, normals or materials);
**PLY** has no materials (their colours become vertex colours, textures are dropped); **OBJ** has no standard vertex
colours (the common `v x y z r g b` extension is written and read; alpha is lost); **3MF** output has no normals, UVs or
textures; and every format drops what Filee's model has no place for: animations, skins, morph targets, cameras,
lights, metallic / roughness / normal / emissive maps, and lines and points. Draco- and meshopt-compressed glTF fail with
a clear message, as do damaged files ("cut short", an index that does not exist, no triangles).

**Files a model refers to** (OBJ's `.mtl` and textures, glTF's separate buffers and pictures, PLY's `TextureFile`,
FBX's texture names) are read only from the model's folder or a folder below it. A path that climbs out (`../`) or
points anywhere else is looked up by its name in the model's folder instead, so a downloaded model cannot pull a
picture from elsewhere on the disk into its output.

**3D → pictures**: a small software rasteriser (`MeshRenderer`) draws an isometric view from the front right, above,
with one light and flat shading per triangle, vertex colours or the texture interpolated across it, and a z-buffer,
at 2048 px and scaled down to 1024 px for smooth edges, on a transparent background. The preset's image options
(resize, background for JPG, quality) apply as for other pictures.

Tests (`tests/Filee.Engines.Tests/ModelTests.cs`) build a cube, a sphere and a fan of coloured triangles in code and
convert each through every format pair, checking triangle counts, bounds, signed volume (winding), normals, UVs,
colours and materials where both formats keep them; hand-written STL, OBJ, PLY, 3MF and glTF files cover the reading
details, and a generated 1,000,000-triangle STL the speed. `FbxTests.cs` writes FBX files in code (`FbxBuilder`:
binary 7.4 and 7.5, plain and compressed arrays, ASCII): a cube under a moved parent, turned and scaled, with a
templated material and an embedded or neighbouring texture, Z-up files in metres, rotation orders, concave polygons and
materials by polygon, and every cut or flipped byte of a file, which must read or fail with a clear message.

## Geospatial data (GeoJSON, KML / KMZ, GPX, Shapefile, CSV)

`Filee.Engines/Geo` converts geospatial **vector** data between GeoJSON, KML, KMZ, GPX, Shapefile and CSV in managed
code, with no GDAL and no download. Every reader fills one in-memory model (`GeoModel`: features, each with an optional
geometry and its attributes in file order) and every writer takes it, so each pair is one step; through CSV the data
also reaches the spreadsheet and data engines (Shapefile → XLSX, GeoJSON → JSON, KML → HWPX, …).

- **Always WGS 84.** Whatever the source's coordinate system, results are longitude / latitude in degrees (and a height
  where there is one). A Shapefile in UTM, Web Mercator or Lambert becomes a GeoJSON that opens in the right place.
  Keeping the original system is not an option yet.
- **One file in, one set out.** A Shapefile is read from its `.shp`; the `.dbf` (attributes), `.prj` (coordinate system)
  and `.cpg` (text encoding) are found next to it in any letter case, the `.shx` is not needed. A Shapefile is written
  as `name.shp`, `.shx`, `.dbf`, `.prj` and `.cpg`; the engine returns all of them. The file names always match: when
  the `.shp` name is taken, the next free name is used for the whole set, and a leftover file of the same name (a lone
  `.dbf`) is never written over.
- **Save as ZIP.** A geospatial preset can pack its result into one ZIP (Settings → Presets → *Save the result as a ZIP*,
  or Settings → General to do it for every geospatial result, or `--zip` on the command line): a Shapefile's five files
  travel together, and the ZIP, named by the preset's file name pattern and conflict policy, is the only output. The
  step that writes the files runs in the job's work folder and `JobQueue` packs it afterwards, so the option works for
  any engine and any number of steps.
- **Same format again** (`shp → shp`, `geojson → geojson`) rewrites the file in WGS 84, which is how a projected
  Shapefile is reprojected, and splits mixed geometries (below).

What each format keeps:

| Format | Read | Written |
|---|---|---|
| GeoJSON | FeatureCollection, Feature or a bare geometry; UTF-8, UTF-16 or an old code page; comments and trailing commas; `id` (as the attribute `id`); the 2008 `crs` member for the systems below; attributes as text, numbers, true / false, null (lists and objects become their JSON text); Z | one feature per line, polygons in RFC 7946 winding (outer counter-clockwise), at most ten decimals |
| KML / KMZ | every Placemark wherever it sits (Document, Folders), `name`, `description`, `ExtendedData` (`Data`, `SimpleData`); Point, LineString, LinearRing, Polygon with holes, MultiGeometry (as Multi* when the parts are alike, else a collection), `gx:Track`, `gx:MultiTrack`; altitude; KMZ: `doc.kml`, else the first `.kml` inside | one Document with a Placemark per feature, `name` / `description` from the attributes of that name, all other attributes as `ExtendedData`, `MultiGeometry` for several parts; KMZ is a ZIP with `doc.kml` |
| GPX | 1.0 and 1.1; waypoints (Point), routes (LineString), tracks (LineString, or MultiLineString for several segments); `name`, `cmt`, `desc`, `src`, `sym`, `type`, `number`, `time`; `ele` as the height; `gpx_type` says which of the three a feature was | points as waypoints, lines as one track (several lines are its segments) or, with `gpx_type` = route, as routes; polygon outlines as tracks; only the fields on the left survive |
| Shapefile | Point, MultiPoint, PolyLine, Polygon and their Z types; rings sorted into polygons and holes by direction and containment; `.dbf` text, numbers, true / false, dates; deleted rows skipped; MultiPatch is refused | one Shapefile per kind of geometry present; Z types when any height is present; WGS 84 `.prj`; `.dbf` in UTF-8 with a `.cpg` |
| CSV | a `wkt` / `geom` / `geometry` / `the_geom` column (WKT or EWKT), or longitude and latitude columns (`lon`, `lng`, `long`, `longitude`, `x`; `lat`, `latitude`, `y`; a decimal comma is fine), and `elevation` / `alt` / `z`; other columns become attributes, typed; rows without a usable location are kept without a geometry | `longitude`, `latitude` (+ `elevation`) when every geometry is a point, else a `wkt` column; then the attributes. A dataset that already has a longitude or latitude attribute gets `wkt` so the file reads back unchanged |

**Coordinate systems.** Geographic systems (degrees) and the projections most projected data uses, read from a `.prj`
(ESRI or OGC WKT 1), a GeoJSON `crs` name or an EPSG code: Transverse Mercator (UTM, Gauss-Krüger, state plane, via
Krüger's series), Mercator and Web Mercator, Lambert Conformal Conic (1 and 2 parallels), Albers;
metres, feet and US survey feet. They agree with PROJ to well under a millimetre: `GeoCrsTests` checks fifteen systems against reference
positions computed with pyproj. Anything else (Lambert azimuthal, stereographic, oblique Mercator, WKT 2, geocentric,
local) is **refused with its name** (`error.geo_unsupported_crs`) rather than read as degrees. So is a projected system
whose geographic part has a prime meridian other than Greenwich or an angular unit other than degrees (ESRI's NTF Paris
Lambert zones count their longitudes from Paris in grads): the projection's formulas assume neither, and the data would
land about 170 km from where it is. Coordinates outside the Earth with no usable system (`error.geo_unknown_crs`, a CSV or
GeoJSON in metres, a Shapefile without `.prj`) are refused too.

**Datums are not shifted.** WGS 84, NAD83, ETRS89, GDA, JGD, Korea 2000, CGCS2000, RGF93, SIRGAS and every other datum on
the GRS 80 / WGS 84 ellipsoid agree within a metre or two. A datum on another ellipsoid (Bessel, Airy, Clarke,
International: Tokyo / Korean 1985, DHDN, OSGB36, ED50, NAD27, …) is converted as if it were WGS 84, which can be
tens to hundreds of metres off, and the file carries the note `notice.geo_datum_assumed`.

**Shapefile writing.**

- A Shapefile holds one kind of geometry. Mixed data becomes `name_points`, `name_lines` and `name_polygons` (each a
  full set) with the note `notice.geo_shapefile_split`. A collection is split into its members, each a record with the
  attributes repeated; features without geometry go into the first file as null shapes.
- Polygons are written clockwise, holes counter-clockwise, rings closed; Multi* geometries are one record.
- `.dbf` columns: whole numbers as `N`, other numbers as `N` with the decimals they need (15 at most, 24 wide), true /
  false as `L`, everything else as text (`C`, up to 254 bytes; longer text is cut between characters,
  `notice.geo_text_cut`). A column that mixes numbers and text is text. Field names are cut to 10 bytes, made of letters,
  digits and underscores, and made unique (`population_2020`, `population_2021` → `population`, `populati_1`,
  `notice.geo_field_names`). More than 255 columns keep the first 255 (`notice.geo_columns_dropped`). Features without
  attributes get a record-number field `FID`, since a `.dbf` needs one.
- Reading takes the text encoding from the `.cpg`, else the language driver byte of the `.dbf`, else UTF-8 if the text is
  valid UTF-8, else Korean CP949, else Windows-1252. (Chinese GBK and Korean CP949 can't be told apart without a `.cpg`.)
- The files are limited to 2 GB (`error.geo_too_large`); whole files are read into memory, a few at a time.

**Safety.** XML (KML, GPX) is read without DTDs or external entities, so a file can't read other files or expand into
gigabytes. The tree is built from `XmlReader` in `GeoXml.Load` rather than by `XDocument.Load` (whose time is quadratic
in the depth: 48 seconds for 100,000 nested levels), and elements nested more than 256 levels are refused while reading;
MultiGeometry may nest 32 levels. A document is limited to about 67 million characters and 3 million elements (a file
of tiny elements costs about 25 bytes of memory per character: 40 million characters in a 150 KB KMZ took 1.1 GB before
the element limit, and now stops at about 260 MB). A KMZ whose `.kml` is declared larger than 256 MB is refused before it is unpacked, and the unpacked bytes are
counted as they are read (`LimitedStream`), since a ZIP header can understate its size. Counts and indexes in binary
Shapefiles are checked against the record before anything is allocated, the attribute table is read only as far as there
are shapes, rings are sorted into polygons smallest outer ring first with the cancellation token honoured, nesting of
JSON, WKT and WKT definitions is limited, and every failure is a reason the user can act on: a damaged file, a system
Filee can't convert, a CSV with no coordinates (`error.geo_*` in the language files). Nothing is written when a
conversion fails.

Tests: `GeoFormatTests`, `GeoShapefileTests`, `GeoCrsTests`, `GeoHostileInputTests`, `GeoZipTests` and
`GeoRobustnessTests` in `tests/Filee.Engines.Tests`. The Shapefiles in `tests/Filee.Engines.Tests/Geo` were written by
GDAL (synthetic data; `Geo/make-test-geo.py` recreates them), so Filee's reader and writer are not only checked against
each other. Everything else is written in the tests: `GeoBuilders.cs` has GeoJSON, KML and GPX text and a Shapefile and
dBASE builder that follows the ESRI specification for the cases GDAL won't write, and the Shapefiles Filee writes are
parsed in the tests from the specification, not read back with the engine. `GeoRobustnessTests` reads every file
truncated at several lengths and with random bytes changed; `GeoHostileInputTests` keeps each input that once crashed the
process, took minutes or used gigabytes.

Not here yet: raster data (GeoTIFF, bands), GeoPackage, zipped Shapefiles as input, datum shifts, more projections.

## HWPX reader

`Hwp/Hwpx/HwpxReader*.cs` is the inverse of the writer: it reads an HWPX package into the same document model, so
HWPX (and HWP, which rhwp turns into HWPX first) can be written as DOCX, HTML or EPUB without Hancom Office or
LibreOffice. It reads files saved by 한글, by rhwp's `export-hwpx` and by Filee itself:

- elements are matched by local name (any prefix or namespace version); `hp:switch` takes the `hp:case` for
  HwpUnitChar / 2016 paragraphs, else `hp:default`, whose lengths 한글 doubles (as are margins and tab stops
  outside a switch in older files); every `…IDRef` is resolved through `header.xml`;
- sections in spine order with page size (`landscape="NARROWLY"` turns the portrait sheet), margins, gutter,
  columns (also changing mid-section), hidden first header/footer, start page (`startNum`, `hp:newNum`);
- headers and footers (both / odd / even pages) with page numbers and counts; an automatic page number
  (`hp:pageNum`, e.g. "- 1 -" at the bottom centre) becomes a footer or header paragraph;
- paragraph shapes (alignment, indents with 한글's hanging-indent convention, spacing, line spacing kinds, tab stops,
  keep options, page break before) and complete character shapes (fonts per language, size, bold/italic,
  underline, strikeout, colour, shade, highlighter `hp:markpen`, super/subscript, letter spacing);
- outline paragraphs (개요) as headings, numbered with the section's outline numbering when it shows numbers;
  numberings and bullets as lists; tabs, line breaks, non-breaking/fixed-width spaces, soft hyphens; tracked
  deletions are left out;
- hyperlinks (HYPERLINK fields, also across paragraphs; `?name` = bookmark), bookmarks, other fields as their
  text, footnotes and endnotes (without 한글's number), equations as their script text;
- tables with spans, column widths, row heights, header rows, borders and fills (`borderFill`), cell padding,
  vertical alignment, captions and nested tables; a table splits the paragraph that holds it;
- pictures from `BinData` (inline, or floating with `hp:pos` offsets, alignment and wrapping), rectangles,
  ellipses and lines with outline and solid / gradient fill, text boxes (`hp:drawText`), groups.

Not kept (the model has no place for them; skipped and counted by the `Read(path, media, skipped)` overload):
OLE objects, charts, video, form controls, text art (its text is kept), arcs / polygons / curves without text,
master pages, memos, character ratio, relative size and offset, paragraph borders, picture cropping and rotation.
Password-protected (encrypted) HWPX and DRM-wrapped files stop with a clear error.

## E-books

The built-in e-book engine (`src/Filee.Engines/Ebooks`) reads books into the same document model as the HWPX
writer, so every e-book also reaches HWPX, PDF and images. The document model is written back out by
`XhtmlWriter` (EPUB chapters, HTMLZ, single-page HTML), `PlainTextWriter`, `MarkdownWriter` and `Fb2Writer`.

- **EPUB 2 / 3**: container.xml → OPF → spine; each chapter is read by the HTML reader and starts a new page, links
  between chapters become links to bookmarks, pictures come from the package, title / authors / language / cover
  from the metadata.
- **EPUB output** is EPUB 3 with an NCX for older readers: `mimetype` first and stored, a navigation document from
  the headings, chapters split at level-1 headings and page breaks, one style sheet, pictures in formats every
  reader shows (others become JPEG / PNG), a cover page for covers the content does not show.
- **MOBI / AZW / AZW3 / PRC** (`Ebooks/Mobi`, written from the MobileRead wiki's format description): PalmDB
  records, MOBI header and EXTH metadata, PalmDOC (LZ77) and HUFF/CDIC compression, MOBI 6 `filepos` links and
  `recindex` pictures, KF8 text rebuilt from the skeleton and fragment indexes with `kindle:pos` / `kindle:embed` /
  `kindle:flow` references resolved, and plain PalmDOC (TEXtREAd) books. **AZW4** (Print Replica) gives back its PDF.
- **FB2**: nested sections become headings, poems / epigraphs / citations / tables are kept, note links become
  footnotes, pictures come from the base64 binaries; the XML declaration's encoding (often windows-1251) is used.
  FB2 output nests sections by heading level.
- **HTMLZ / TXTZ** (calibre's zipped formats) are read and written, with their `metadata.opf`.
- **KEPUB** (Kobo, `Ebooks/Kepub.cs`, written from the format's public descriptions): EPUB → KEPUB copies the book
  and changes only the XHTML documents of the spine. Every sentence becomes `<span class="koboSpan"
  id="kobo.P.S">` (P counts the block elements of the chapter, S the sentences in it; a sentence ends at . ! ? …
  followed by a space, or at the full-width 。！？), every picture gets a span of its own, and the body's content
  is wrapped in `<div id="book-columns"><div id="book-inner">`. Scripts, styles, SVG and MathML are left alone; a
  chapter that isn't well-formed XML (after HTML entities such as `&nbsp;` became character references) is copied
  unchanged. Files are named `name.kepub.epub`, which tells Kobo devices to use their KEPUB reader. Other formats
  reach KEPUB through EPUB (Markdown → EPUB → KEPUB); KEPUB → EPUB removes the markup again, and a KEPUB is read
  like any EPUB.
- **Comics**: CBZ, CBR, CB7, CBT and CBC (a ZIP of CBZ files) are opened with SharpCompress; pages are sorted
  naturally ("page2" before "page10"). → PDF has one page per picture sized like the picture (JPEGs are embedded
  as they are), → EPUB is fixed-layout, → CBZ repacks. PDF → CBZ renders the pages with PDFium at the preset DPI.
- Books with DRM (Adobe, Apple, Kindle) are refused with a clear message; Filee does not remove DRM. KFX and
  Topaz books are recognised and refused too.

## Calibre

calibre's `ebook-convert` (GPL-3.0) is an optional download for the formats Filee does not read or write itself:
LIT, LRF, CHM, PDB, PML, RB, SNB, TCR and OEB → EPUB, and EPUB → MOBI, AZW3, LIT, LRF, PDB, PML, RB, SNB and TCR.
EPUB is the hub, so for example FB2 → AZW3 is FB2 → EPUB (built in) → AZW3 (calibre). Its edges cost 20, so
built-in routes always win where they exist.

calibre also makes PDFs of EPUB, MOBI, AZW3, AZW, PRC, FB2, HTMLZ, TXTZ and the formats only it reads (one
`ebook-convert` run: A4 with `--paper-size a4`, page numbers with `--pdf-page-numbers`, calibre's margins and font
sizes; the built-in route ignores the preset's PDF options too). For long books it is much faster than the built-in
route, HWPX writer → rhwp (a 640-page novel: about a minute against some 48 minutes, #127), but the built-in route
stays the default so results don't change under users. The PDF edges cost 23: in the default engine order the
built-in route costs 31 (HWPX writer 10 + 8, rhwp 10 + 3) and calibre 23 + 9, so the built-in route wins by one; once
the user moves calibre above the HWPX writer in Settings → *Engines*, calibre costs at most 31 and the built-in route
at least 32. LIT, LRF and the other formats only calibre reads go straight to PDF instead of calibre → EPUB → HWPX
writer → rhwp. When rhwp runs out of time and calibre is installed, the error says to rank calibre higher; Filee
never switches engines by itself.

- The pinned `calibre-64bit-<version>.msi` from download.calibre-ebook.com is unpacked like LibreOffice; the folder
  with `ebook-convert.exe` becomes `engines/calibre`.
- On macOS the pinned calibre needs macOS 14 (`minimumMacOS` in engines.json). Filee runs on macOS 13, where the
  download isn't offered; Settings → *Engines* points to calibre 7.26, the last version for macOS 13, as the user's
  own copy.
- Each run gets its own configuration, cache and temp folders in the job's work directory
  (`CALIBRE_CONFIG_DIRECTORY`, ...), so a calibre the user installed is never read or changed; messages are
  English (`CALIBRE_OVERRIDE_LANG`) and progress comes from its "34% ..." lines.

## HWP ↔ HWPX

rhwp converts between the two 한글 formats without Hancom Office (`export-hwpx` and `convert`). Anything → HWP goes
through the HWPX writer and then rhwp.

For HWP / HWPX → PDF, rhwp renders a copy of the HWPX (an HWP is exported with `export-hwpx` first) when Filee has to
change something for it (`Hwp/RhwpInput.cs`): fonts that are installed, the laid-out size of boxes sized relative to
the column, spaced equation commands, and, in an HWPX exported from an HWP (rhwp's `META-INF/rhwp-hwp5-origin`
marker), the stored line of a cell paragraph that holds only a floating table moved up to the paragraph's top, so the
table is not drawn over the paragraphs below it (#49). Paragraphs without stored lines (Word equations written by the
HWPX writer) get room for their equations: rhwp puts a line's baseline at 85 % of its tallest equation and the
equation's own baseline on it, so a fraction, matrix or sum hangs below the line; that overhang is added as space
after the paragraph (#143).

An HWPX with text that needs shaping (Arabic, Hindi, Thai, ...) goes to LibreOffice with H2Orestart instead, like the
documents above; an HWP is not scanned and stays with rhwp.

rhwp renders text slowly: about 1 s per KB of section XML, some 4 s per page of a novel (rhwp 0.8.7 on an
i7-1185G7, pictures cost little). So each rhwp run's time limit follows the document's text: 2 minutes + 30 minutes
per MB of unpacked `Contents/section*.xml` (an HWP counts three times its file size, its text being deflated), at most
60 minutes, after which a rhwp that hangs is stopped. The progress estimate assumes 15 minutes per MB. Cancelling
stops rhwp at once. A time-out fails with "rhwp couldn't finish this long document in time" (with calibre installed,
it suggests ranking calibre above the HWPX writer for e-books). The other engines keep fixed limits: LibreOffice 3
minutes, Ghostscript 5, Pandoc 2 and calibre 10 (it made the 640-page novel's PDF in a minute).

## Archives

The archive engine (`Archives/ArchiveConverter.cs`, id `archive`) uses Filee's own copy of the 7-Zip console program
(`7z.exe` + `7z.dll`, taken from the official x64 MSI with `msiexec /a` by `build/fetch-engines.ps1 -Only 7zip` and
bundled with the installer), never a 7-Zip installed on the PC. It runs as a separate process (LGPL).

| Format | Read | Write |
|---|---|---|
| ZIP, JAR, WBPZ | 7-Zip | ZIP in-process (System.IO.Compression): UTF-8 names with the language-encoding flag, times, empty folders |
| 7Z | 7-Zip | 7-Zip |
| TAR | 7-Zip | in-process (System.Formats.Tar, PAX: UTF-8 names, times) |
| TAR.GZ | 7-Zip, in two stages | in-process (PAX TAR + GZipStream) |
| TAR.BZ2, TAR.XZ | 7-Zip, in two stages | TAR in-process, compressed by 7-Zip |
| TAR.7Z, TAR.Z | 7-Zip, in two stages | — |
| LZ, TAR.LZ (lzip) | Filee: each member rewritten as a .lzma file for 7-Zip's LZMA decoder, CRC and size checked | — |
| GZ, BZ2, XZ | 7-Zip | 7-Zip; one file only (from another single-file format, or "Compress into one archive" with one file) |
| LZMA, Z | 7-Zip | — |
| RAR 4/5, CAB, CPIO, DEB, RPM, DMG, ISO, IMG, LHA/LZH, ARJ | 7-Zip (an RPM's payload is unpacked until the files appear) | — |
| ALZ (ALZip) | Filee: store, deflate and ALZip's bzip2 variant | — |
| EGG (ALZip) | Filee: store, deflate, bzip2, LZMA (decoded by 7-Zip); several blocks per file | — |

- **Extract** (target `folder`): the archive is unpacked into a hidden folder next to the result and moved into place
  when complete. When the archive holds exactly one top folder, its content becomes the result folder (`photos.zip`
  with `photos/…` gives `photos/…`, not `photos/photos/…`). Name conflicts follow the preset: `name (2)`, overwrite
  (files are merged into the existing folder) or skip.
- **Archive → archive** unpacks into the job's temp folder and packs everything into the target. `Preset.Archive.Level`
  maps to Store / Fastest / Optimal / SmallestSize in-process and to 7-Zip's `-mx0/1/5/9`.
- **Compress into one archive** (`Archive.CombineIntoOne`, the "One ZIP" preset): `JobQueue` hands all dropped files,
  of any type and unconverted, to `IFileCombiner` (implemented by this engine). The archive is named after the first
  file with the output pattern; files with the same name become `name (2).ext`. ZIP, TAR and TAR.GZ need no 7-Zip.
- **Safety**: every archive is listed first. Encrypted entries or archives stop with "password-protected"; entries
  (or hard links) with `..`, a drive or a leading slash stop the job ("points outside the target folder", nothing is
  written); symbolic links are never unpacked and any link left afterwards is deleted; archives with more than
  1,000,000 entries or more data than the drive has free are refused; Filee's own readers also stop when data unpacks
  to more than the entry declares, and check CRCs.
- **Not supported**: encrypted archives, split (multi-volume) ALZ and EGG, solid EGG, EGG entries compressed with
  ESTsoft's own AZO method, LZO / TAR.LZO (lzop; 7-Zip has no reader either), and writing RAR, ALZ or EGG.
- The ALZ and EGG readers are Filee's own code, written from the format descriptions of unalz (zlib licence), the
  `unalz` Rust crate, EggDotNet and unegg (MIT). ALZip's bzip2 has no `BZh` header, starts every block with `DLZ\x01`
  instead of the block magic, CRC and randomised bit, and ends with `DLZ\x02` (`Archives/Bzip2Decoder.cs`).
- Tests (`ArchiveTests.cs`) build ALZ, EGG and lzip files in code (`ArchiveBuilders.cs`, with bzip2 and LZMA streams
  written by 7-Zip) and skip the 7-Zip parts when `engines/7zip` is missing.

## LibreOffice

- Each parallel worker gets its own profile under `%APPDATA%\Filee\libreoffice-profiles` (LibreOffice silently
  fails when two conversions share a profile).
- HWP/HWPX import comes from the H2Orestart extension, which is written in Java; Filee points LibreOffice at the
  downloaded JRE (`-env:UNO_JAVA_JFW_JREHOME`).
- When LibreOffice can't load an Office Open XML or OpenDocument file ("source file could not be loaded"), Filee
  converts once more from a copy in the job folder with the same parts in a plainly written ZIP (`PackageRepacker`):
  LibreOffice 26.2 refuses some valid containers, e.g. uncompressed entries with data descriptors (Tencent Docs, #141).
- It is the only reader of rarer formats, converted to PDF and to the editable formats of their kind: DOT, Works /
  WPS Writer, WordPerfect, Lotus Word Pro, AbiWord, Apple Pages, StarWriter, ClarisWorks / AppleWorks (`.cwk`),
  MacWrite (`.mcw`), T602 (`.602`) and Pocket Word (`.psw`) (→ DOCX, ODT, FODT, OTT, DOC, RTF, TXT, HTML); WPS
  Spreadsheets (ET), Apple Numbers, StarCalc and flat ODS (`.fods`) (→ XLSX, ODS, OTS, XLS, XLT, CSV, HTML); Apple
  Keynote, WPS Presentation (DPS), StarImpress / StarDraw and flat ODP (`.fodp`) (→ PPTX, ODP, OTP, PPT);
  Publisher, CorelDRAW, Visio, CGM, ODG and flat ODG (`.fodg`) (→ ODG, SVG, PNG, EMF, WMF). The `--convert-to`
  filter follows the application that opens the file (checked against LibreOffice's filter registry): Publisher,
  CorelDRAW and Visio open in Draw, CGM in Impress, and "SDA" files in Draw (.sda) or Impress (.sdd). ClarisWorks
  files can also be spreadsheets or drawings; Filee treats them as text documents, the common case. SVG, PNG, EMF
  and WMF export the first page.
- Written only by LibreOffice: FODT (`OpenDocument Text Flat XML`), the OpenDocument templates OTT / OTS / OTP
  (`writer8_template`, `calc8_template`, `impress8_template`) and XLT (`MS Excel 97 Vorlage/Template`). XLT is read
  by the built-in spreadsheet engine like XLS.
- SVG opens in Draw only for EMF, WMF and ODG; the vector engine keeps SVG → PDF and images. PDF opens in Draw
  (`draw_pdf_import`) only for SVG and ODG: Draw's SVG export draws every page of a PDF on top of each other, so Filee
  first copies the first page of the preset's page range into a PDF of its own (PDFsharp). EPS and PS reach SVG
  through Ghostscript's PDF; Draw would only show their preview picture.
- Checked with LibreOffice 26.2.6 (`LibreOfficeFormatTests`): FODT, FODS, FODP, FODG, T602 and SVG inputs, the
  template and FODT outputs, XLT read back by the spreadsheet engine, EMF / WMF headers and the one-page PDF → SVG.
  ClarisWorks, MacWrite and Pocket Word are binary formats without sample files in the repository: only their
  filters (`ClarisWorks`, `MacWrite`, `PocketWord File`, all opening in Writer) were checked in the registry.

## Pandoc

`Office/PandocConverter.cs` runs Pandoc for the markup languages Filee doesn't read itself: reStructuredText,
LaTeX, Org (`.org`), AsciiDoc (`.adoc`, `.asciidoc`), Typst (`.typ`), MediaWiki (`.mediawiki`, `.wiki`),
DocBook (`.dbk`, `.docbook`; written as DocBook 5), Jupyter notebooks (`.ipynb`), Textile (`.textile`), man pages
(`.man`) and OPML (`.opml`). Pandoc 3.12 reads and writes all of them.

- Each converts to and from Markdown, HTML, DOCX and ODT and into each other, and to RTF, EPUB and TXT. Through
  Pandoc's JSON AST they are also read by the HWPX writer, so they reach HWPX, HWP and PDF (HWPX writer → rhwp).
  There is no PDF through Pandoc itself (that would need a TeX or Typst engine).
- Edges into a markup language cost 11, one more than into Markdown, so a route with a Pandoc step in the middle
  (an e-mail's HTML → ? → RTF) stays on Markdown.
- Markdown, reStructuredText, Org, AsciiDoc, Typst, MediaWiki, Textile and notebooks → **PPTX** with Pandoc's
  PowerPoint writer (a slide per heading). These edges cost 20: with LibreOffice's cheap PPTX → PDF, Markdown → PPTX
  → PDF would otherwise beat Markdown → HWPX → PDF.
- Extensions that mean other things are not claimed: man pages only as `.man` (`.1` … `.9` are also rotated logs,
  `app.log.1`), DocBook as `.dbk` / `.docbook`, never every `.xml`.
- DocBook, man pages, OPML, Typst, HTML and LaTeX are written with `--standalone` (a root element, a `.TH` header,
  a preamble), the text formats extract pictures into `<name>_files` next to the result (Markdown with *Leave out
  pictures* gets none: a Lua filter drops them).
- Pictures are linked relative to the result (`<name>_files/media/…`; Pandoc runs in the result's folder, with
  `--resource-path` pointing at the source's for pictures the source links to), so both can be moved together.
  EMF and WMF pictures, common in Word documents, become PNG (ImageMagick draws metafiles on Windows only; elsewhere
  they stay).
- Markdown is GitHub-flavoured and kept free of HTML, which many Markdown editors show as code (#391). A Lua filter
  lays every table out as a pipe table: a merged cell keeps its text in its first position and leaves the cells it
  covered empty, the paragraphs and list items of a cell are joined with `<br>`, and a table without a header row
  gets its first row as the header. Pictures lose their Word size, which only an `<img>` tag could hold.

## Subtitles

`Subtitles/SubtitleConverter.cs` (engine id `subtitle`) converts text subtitles in managed code, no FFmpeg needed.
Every reader produces one cue model (`SubtitleModel.cs`: start, end and text with line breaks and SRT's own tags
`<i>`, `<b>`, `<u>`, `<font color>`), every writer takes it, so any format converts to any other and to itself (an
SRT or SAMI file in CP949 comes out as UTF-8).

| Format | Read | Written |
|---|---|---|
| SRT | numbers optional, blank lines inside cues and missing between them, `,` or `.` before the milliseconds, `{\an8}` codes dropped | UTF-8 with BOM, CRLF, tags kept |
| ASS / SSA | `Dialogue:` lines in the order of the `Format:` line; `\i \b \u \c`, bold/italic/underline styles, `\N`; drawings (`\p1`) and comments left out, shadow layers merged | ASS v4.00+ for 1920 × 1080, one "Default" style, override codes (SSA is read only) |
| WebVTT | NOTE / STYLE / REGION blocks skipped, cue settings ignored, `<i> <b> <u>`, the colour classes (`<c.yellow>`), entities; voices and timestamps dropped | UTF-8, the basic colour classes, text escaped |
| SAMI (SMI) | hand-made HTML: unquoted attributes, unclosed `<SYNC>` / `<P>`, `&nbsp` with or without `;`; of several languages (KRCC, ENCC, …) the one with the most lines | one class from the text's language (KRCC for Korean), `&nbsp;` where the screen goes blank, UTF-8 with BOM |
| LRC | `[mm:ss.xx]` (several per line), `[ti:]`, `[offset:]`, enhanced word times dropped; a line lasts until the next one | plain text, lines joined, an empty time line where the screen goes blank |
| TTML / DFXP | TTML 1 and the 2006 DFXP namespace; times relative to their parents; clock, frames (`ttp:frameRate`), ticks and offset times; spans and referenced styles | TTML 1 with a bottom region and tts: spans |
| MicroDVD (.sub) | `{frame}{frame}text` with the rate of a `{1}{1}25` first line (else 23.976), `\|` between lines, `{y:i}` / `{c:$BBGGRR}`; VobSub `.sub` (pictures) and SubViewer fail with a clear message | 23.976 fps, first line states it, plain text, `{Y:i}` for all-italic cues |
| SCC | CEA-608 caption channel 1: pop-on, paint-on and roll-up captions, row preambles, italics, special and extended characters, doubled control codes, drop-frame time codes | — |

- **Encodings**: a byte order mark (UTF-8, UTF-16) or valid UTF-8 decide first, then a `charset=` / `encoding=` the
  file declares. Otherwise CP949 when the bytes are CP949 pairs, most of them Hangul syllables of KS X 1001 and hardly
  any with a lead byte above C8 (Chinese GBK text uses those for many common characters), then the PC's code page on
  Chinese or Japanese Windows, else the Western one (Windows-1252 on Korean Windows). Everything is written as UTF-8.
- Cues without an end (LRC, the last SAMI line, an unterminated SCC caption) last 4 seconds; empty cues are dropped.
- Not kept: positions and alignment, fonts and sizes, karaoke timing, ASS animations, WebVTT regions and voices.
- Tests (`SubtitleTests.cs`) write every input in code, CP949 and GBK bytes included.

## DjVu

Not supported: DjVuLibre (GPL) has no pinned, redistributable Windows build in an archive the installer can unpack.
The official Windows release is an NSIS installer (`DjVuLibre-3.5.29_DjView-4.12_Setup.exe` on SourceForge), which
would have to be run (a system install) or unpacked with 7-Zip; MSYS2's packages are `.pkg.tar.zst` archives with
five dependency packages. A DjVu engine needs a zip of `ddjvu.exe` / `djvutxt.exe` and their DLLs published for
Filee (with the GPL source offer), then an `engines.json` component and an optional package.

## FFmpeg

`Media/FfmpegConverter.cs` converts every video format to every video format, to animated GIF or APNG, to a still PNG
frame or a folder of every frame, to every audio format (the sound track) and to SRT, ASS and WebVTT (the subtitles);
every audio format to every audio format; and GIF, APNG and PNG to MP4, WEBM and MOV.
Same-format edges exist too, so "MP4 720p" works on MP4 files and "MP3 128k" on MP3 files.
Raw H.265 and H.264 camera streams (`.h265`, `.265`, `.hevc`, `.h264`, `.264`, read only, #301) are Annex B elementary
streams without a container: FFmpeg reports them as format `hevc` / `h264` (`MediaInfo.RawStream`). They are always
encoded, never copied, because their frames carry no timestamps (a copy into MP4 had non-monotonic DTS). A stream
without timing information is read at FFmpeg's default 25 fps, so a camera that recorded at another rate plays faster
or slower; its frames are all kept.
CRI USM (`.usm`, read only, #485), the video of many games' cutscenes, is read by FFmpeg's `usm` demuxer and goes to
every video and audio target like the other read-only containers. Encrypted USM files (common in recent games) can't
be read and fail with FFmpeg's error.
A Video CD's `MPEGAV/AVSEQ01.DAT` (MPEG-1 in a RIFF/CDXA wrapper) counts as MPEG: `.dat` is an extension of that
format, and FFmpeg reads what the file holds, so a `.dat` with no video in it fails instead of making a file.

- The download is the gyan.dev **9.0.2 "full_build-shared"** Windows build (GPL-3.0, run as a separate program).
  It is smaller than the "essentials" build (100 vs 115 MB download, 272 vs 329 MB on disk, because ffmpeg.exe and
  ffprobe.exe share their libraries) and has a superset of its encoders. It links only Windows system DLLs; GPU
  and Vulkan libraries are loaded only if present. Being over 60 MB, it is offered but not ticked on first run.
- The Cut a clip window's preview plays its sound through `ffplay` when it sits next to Filee's FFmpeg (the gyan.dev
  build has it); without it, the preview plays silently. Nothing else uses ffplay.
- Container and codecs per target (`Media/MediaEncoding.cs`, every encoder checked with `ffmpeg -encoders`):

  | Target | Written as |
  |---|---|
  | MP4, M4V, MOV, MKV, FLV, TS, M2TS, 3GP, 3G2 | H.264 (x264, preset medium, yuv420p) + AAC; MP4/M4V/MOV/3GP/3G2 with the index at the front (`+faststart`) |
  | WEBM | VP9 (constant quality, speed 4, row multithreading) + Opus |
  | AVI | MPEG-4 part 2 tagged XVID + CBR MP3 |
  | WMV | WMV 8 (wmv2) + WMA 2 |
  | MPG | MPEG-2 program stream: MPEG-2 video + MP2 |
  | VOB | DVD-Video: 720×480 at 29.97 fps (NTSC) or 720×576 at 25 fps (PAL, for 25/50 fps sources), letterboxed into 4:3 or 16:9, AC-3 at 48 kHz, DVD mux rate |
  | OGV | Theora + Vorbis |
  | DV | DV25 with the same NTSC/PAL frames as VOB, 48 kHz stereo PCM |
  | MXF | OP1a with long-GOP MPEG-2 + 48 kHz PCM |
  | GIF | two passes: palettegen over the whole clip, then paletteuse; 15/12/10 fps and at most 640/480/320 px wide (High/Balanced/Small); *Playback speed* adds `setpts=PTS/<speed>` before the frame rate |
  | APNG | one pass with the `apng` encoder (24-bit RGB, mixed prediction) and muxer, looping; the same frame rates, widths and playback speed as GIF |
  | MP4, M4V, MKV with *Video codec* HEVC · MOV with HEVC | x265 (preset medium, `hvc1` tag in MP4/M4V/MOV) + AAC |
  | MP4, M4V, MKV, WEBM with *Video codec* AV1 | SVT-AV1 (preset 8), or libaom-av1 (cpu-used 6, row multithreading) with an FFmpeg that lacks SVT-AV1 |
  | MP3 · M4A/M4B · AAC · OGG · OPUS · WEBA · MKA · WMA · MP2 · AC3 · E-AC3 · AMR | LAME VBR (V0/V2/V5) or CBR · AAC, or ALAC with *Audio codec* ALAC · AAC (ADTS) · Vorbis · Opus · Opus · Opus · WMA 2 · MP2 · AC-3 · E-AC-3 · AMR-NB 12.2k (8 kHz mono) |
  | WAV · W64 · AIFF · AU · CAF · VOC · FLAC · WV · TTA | 16-bit PCM (WAV and W64: 8/16/24/32-bit or 32-bit float; WAV switches to RF64 over 4 GB, Sony Wave64 has no limit) · FLAC · WavPack (lossless) · TTA |
  | SRT · ASS · VTT (from video) | the video's first text subtitle track, copied when it already is that format, else converted by FFmpeg |

  Read only (FFmpeg's decoders): APE (Monkey's Audio), Musepack (MPC), TAK, DSF and DFF (DSD), DTS, Speex (SPX) and the
  formats above. No encoder for them is in the build, or (DTS) only an experimental one.

- **Rewrapping instead of re-encoding.** When the target container takes the source's codecs as they are
  (`MediaEncoding.Remuxable`: H.264/HEVC + AAC, ALAC, AC-3, E-AC-3 or MP3 for MP4/M4V/MOV, also ProRes and PCM for
  MOV; H.264/HEVC/VP9/AV1 with most audio for MKV; VP8/VP9/AV1 + Opus/Vorbis for WebM; H.264/HEVC for TS/M2TS),
  the streams are copied (`-c copy`). An iPhone MOV becomes an MP4 in seconds, frame for frame, with 10-bit HDR
  (HLG, Dolby Vision's HLG base), rotation and colour tags kept. HEVC gets the `hvc1` tag Apple players need. Video
  is copied only in a pixel format players decode (4:2:0, 8 or 10 bits; ProRes as it is) and when no scaling down
  is needed; audio only when no bitrate or other channel count is set. Quality *Small* always re-encodes (the user
  asked for a smaller file); *High* and *Balanced* only set how a stream that has to be encoded is encoded. A
  stream that can't be copied is encoded as below, the other one is still copied.
- Preset options (`MediaOptions`): *Quality* picks CRF 18/23/28 (x264), 22/26/30 (x265), 26/32/40 (SVT-AV1 and
  libaom), 24/32/38 (VP9), quantizer 2/4/7 (MPEG-4, WMV, MPEG-2), Theora 8/6/4 and the audio bitrates (e.g. AAC and
  E-AC-3 256/192/128 kbit/s per stereo pair); *MaxHeight* scales down only, keeps the aspect ratio and even sizes;
  *AudioBitrateKbps* overrides the audio bitrate (snapped to the MP3/MP2/AC-3 bitrate tables; the editor offers 32 to
  320 kbit/s); *RemoveAudio* drops the sound; *AudioChannels* (0 source, 1 mono, 2 stereo) applies to every audio
  target but AMR; *AudioSampleRate* (8 to 48 kHz) and *WavFormat* (bit depth) apply to WAV and W64.
- *VideoCodec* (Default, HEVC, AV1) and *AudioCodec* (Default, ALAC) pick another codec where the target takes it
  (`MediaCodecs` in Filee.Core: HEVC for MP4/M4V/MKV/MOV, AV1 for MP4/M4V/MKV/WEBM, ALAC for M4A/M4B); for any other
  target the usual codec is written. The presets "MP4 HEVC" (on the video donut), "MP4 AV1" and "ALAC" use them. A
  video already in the chosen codec is copied (the rules above, with AV1 also into MP4); HEVC and AV1 keep 10 bits
  (`yuv420p10le`, Main 10) for 10-bit and HDR sources and write their colour tags (BT.2020, PQ or HLG), 8-bit 4:2:0
  for the rest. The encoders are looked up with `ffmpeg -encoders` once per FFmpeg, so a copy the user picked without
  x265 or AV1 fails with a clear message instead of an FFmpeg error. x265 and SVT-AV1 log only errors
  (`-x265-params log-level=error`, `SVT_LOG=1`).
- **Hardware encoding** (*Hardware acceleration* in the video card, `MediaOptions.HardwareEncoding`; off by default and
  for older presets, so nobody's results change without asking). H.264, HEVC and AV1 encodes (not VP9 and the
  MPEG-style codecs, not stream copies, not pictures → video) go to the graphics chip (`Media/HardwareVideo.cs`,
  `Media/HardwareProbe.cs`):

  | System | Tried in this order |
  |---|---|
  | Windows | `*_nvenc` (NVIDIA), `*_qsv` (Intel Quick Sync), `*_amf` (AMD) |
  | macOS | `*_videotoolbox` with constant quality (Apple Silicon), then with a bitrate (Intel Macs reject `-q:v`); none for AV1 (VideoToolbox has no AV1 encoder) |
  | Linux | `*_nvenc`, then `*_vaapi` on `/dev/dri/renderD128` (`-vaapi_device`, frames uploaded with `format=nv12,hwupload`) |

  AV1 encodes only on recent chips: NVIDIA RTX 40 and newer (`av1_nvenc`), Intel Arc and Core Ultra (Meteor Lake) and
  newer (`av1_qsv`), AMD RDNA 3 and newer (`av1_amf`), and VA-API drivers that offer it; on older ones the test
  encode fails and SVT-AV1 encodes as before. FFmpeg lists encoders the computer can't run (NVENC without an NVIDIA
  card), so only the ones `ffmpeg -encoders` lists are tried, each first with a test encode with the real options
  (0.1 s of `testsrc` at 256 × 144 to the null muxer, 15 s limit). The first that succeeds is used; results are kept
  per FFmpeg executable, encoder, rate control and bit depth until Filee restarts, and written to the log
  ("Hardware encoder av1_qsv (Quality, 8-bit) works" / "is not usable: …"). Quality: *High/Balanced/Small* become
  quantizer 19/24/29 for H.264 and 23/27/31 for HEVC (one above x264's and x265's CRF: hardware encoders spend bits
  less wisely) as NVENC `-rc vbr -cq N -b:v 0 -preset p5`, Quick Sync `-global_quality N -preset medium` (ICQ), AMF
  `-rc cqp -qp_p N -qp_i N−2` (`-quality balanced`) and VA-API `-rc_mode CQP -qp N`; VideoToolbox gets `-q:v`
  70/60/50, or on Intel Macs `-b:v` from 0.14/0.09/0.05 bits per pixel per frame (× 0.65 for HEVC). AV1 has other
  scales: NVENC's `-cq` is AV1's 0–63 quantizer and gets 28/34/42 (two above SVT-AV1's CRF 26/32/40); AMF and VA-API
  take the 0–255 quantizer index, four times that (112/136/168: AMF `-qp_p`/`-qp_b` N, `-qp_i` N−8; VA-API
  `-rc_mode CQP -global_quality N`, as `av1_vaapi` has no `-qp`); Quick Sync's ICQ is 1–51 for AV1 too and gets
  HEVC's 23/27/31. 8-bit video is NV12 (H.264 High, HEVC Main, AV1 Main); 10-bit and HDR sources stay P010 (HEVC Main
  10, 10-bit AV1 Main) with their colour tags when the encoder passes a 10-bit test encode, otherwise x265 / SVT-AV1
  encode them. AV1 gets no `-profile:v` (every encoder picks Main; `av1_nvenc` has no such option) and no tag (`av01`
  in MP4/M4V, as written to MKV and WebM). HEVC keeps the `hvc1` tag in MP4/M4V/MOV. GOP length and B-frames are
  left to each encoder's defaults, as for H.264 and HEVC. When a hardware encode fails anyway (a picture too large for
  the chip, a driver error), the partial output is deleted and the file is encoded again with x264/x265/SVT-AV1; that
  is logged, not shown as a failure.
- DSD (DSF, DFF) decodes at 2.8 MHz or more: lossless and PCM targets get 88.2 kHz, Vorbis and WMA 44.1 kHz, the
  other lossy encoders their nearest supported rate.
- Subtitle extraction takes the first text subtitle track (SubRip, ASS/SSA, WebVTT, MP4 timed text, CEA-608, …).
  A video without subtitles fails with "The video has no subtitle track."; one with only picture subtitles (Blu-ray
  PGS, DVD, DVB) says they can't be turned into text without OCR. Video → SAMI, LRC, TTML, … goes through SRT and the
  subtitle engine (ASS and WebVTT extraction cost 11 so SRT is the hub).
- The first video stream (cover pictures excluded) is converted. Video to video keeps every audio track (languages,
  commentary), each copied where the target takes its codec or encoded on its own, with its language tag; FLV and DV
  hold one and keep the first. Video to audio takes the first. Files without sound still convert to video. Title, artist
  and other tags and chapters are kept, album art too for MP3, M4A, M4B and FLAC. Portrait phone videos are turned
  upright. Odd sizes are evened out, frame rates that MPEG-2/MXF/MPEG-4 part 2 cannot store are converted, and channels
  WMA or AMR cannot hold are mixed down.
- Progress comes from `-progress` (position ÷ duration from ffprobe); the GIF palette pass reports per-frame stats of
  a discarded copy of its frames. Cancelling kills the process tree, waits (up to 10 seconds) until it has exited and
  deletes the partial output. There is no time limit, but FFmpeg is stopped when it reports nothing for 10 minutes.
  At most two conversions run at a time (FFmpeg's encoders use every core themselves).
- Video → PNG writes one still frame (a tenth into the video, where the picture has usually faded in); the image
  engines turn it into JPG, WEBP, PDF, …. The GIF edge costs more, so these routes never go through an animated GIF
  of the whole video.
- **Every frame** (*Save every frame of a video* on a PNG or JPG preset, `MediaOptions.SaveEveryFrame`; the presets
  "Extract frames (PNG)" and "Extract frames (JPG)", on no donut): `Media/FrameSequenceWriter.cs` writes
  `frame_000001.png`, `frame_000002.png`, … into one folder, named by the output rule with `_frames` added
  (`clip_frames`, `clip_frames (2)`; skipped, or filled over like an extracted archive, by the conflict rule).
  - Routes: the frame edges (`ConversionEdge.FrameSequence`, video → PNG and video → JPG) are taken only when
    `Preset.SavesFramesOf` the file (the option is set, the target is PNG or JPG and the file is a video), and then as
    the whole route (`RoutePlanner.Plan(from, to, frames: true)`). Without the option video → JPG stays the still frame
    above, which the image engines turn, resize and so on; other files and targets ignore the option.
  - *Every Nth frame* (1–1000) keeps frames 1, N + 1, 2N + 1, … (`select`, `-fps_mode vfr`). Otherwise every decoded
    frame is written once (`-fps_mode passthrough`): a variable frame rate phone video gives as many pictures as it
    has frames, not one per 1/30 s with the slow parts repeated. The pictures are numbered, not named by time.
  - Only the first video stream is read (`-map 0:V:0`). Pixels are made square and portrait videos are turned
    upright, as for the still frame; nothing else of the image options is applied (no resizing, rotating or
    metadata). JPG uses the preset's quality (100 → `-q:v 2`, 90 → 5, 1 → 31). HDR video (PQ, HLG) is not
    tone-mapped, so its frames look flat and grey; 10-bit video becomes 16-bit PNG.
  - Before anything is written the pictures are estimated (duration × frame rate ÷ interval). More than 100,000
    (`MediaOptions.MaxFrames`, about an hour at 30 fps), or more than the free space of the output drive (estimated at
    1.5 bytes a pixel for PNG, 3 for 10-bit video, 0.25 for JPG), fails with a reason in the user's language
    (`ConversionRefusedException`); a file that doesn't say its length or frame rate is not checked. The editor shows
    how many pictures a minute of 30 fps video gives at the chosen interval.
  - The frames go into a hidden folder next to the output and are moved into place when FFmpeg has finished, so a
    cancelled or failed run leaves no half-filled folder.
- **Live / Motion Photos** (*Extract Live/Motion Photo video* on an MP4 or MOV preset, `MediaOptions.ExtractMotionPhoto`;
  the preset "Extract Live Photo video", on no donut): the video saved with a phone photo becomes the output, with its
  sound, instead of a video of the still picture.
  - Routes: the motion edges (`ConversionEdge.MotionPhoto`, JPG, HEIC, AVIF and LIVP → MP4 and MOV,
    `MediaOptions.MotionPhotoSources`) are taken only when `Preset.ExtractsMotionOf` the file, and then as the whole
    route (`RoutePlanner.Plan(from, to, motionPhoto: true)`). A LIVP always gives its video; GIF, PNG, WebP and other
    pictures ignore the option and become a video of the picture as below.
  - Where the video is (`Media/MotionPhoto.cs`, `Media/LivePhotoSource.cs`), always from the photo's own metadata,
    never by searching its bytes for an MP4 signature: Motion Photo 1.0 JPEGs (Pixel, Samsung, …) give the length of
    the appended video in the XMP container directory (`GCamera:MotionPhoto`, the last `Container:Item`, attributes or
    elements); older Google "MicroVideo" JPEGs an offset back from the end (`GCamera:MicroVideoOffset`); HEIC and AVIF
    hold the video in a top-level `mpvd` box. The video must start with `ftyp` and hold a `moov`, else the metadata is
    stale (an editor rewrote the picture) and nothing is guessed; it ends with its last whole box, which leaves
    Samsung's SEF trailer out. An iPhone photo (JPG or HEIC) takes the MOV next to it with the same name, only when
    both carry the same content identifier (tag 17 of Apple's maker note, `com.apple.quicktime.content.identifier`);
    a LIVP (the ZIP iCloud downloads give) takes its one MOV, whose identifier is only checked for the log.
  - The video is copied into the work directory (at most 512 MB; JPEG metadata is read only from the first 8 MB, XMP
    without DTDs; LIVP entries are copied to names of Filee's own, never extracted by their names), converted there
    like any video (copied as it is into MP4/MOV when its codecs fit) and moved into place when finished: a failed or
    cancelled run never touches a file "overwrite" would replace, and the output never replaces the iPhone MOV it was
    read from (IMG_0001.HEIC → `IMG_0001 (2).mov`). A photo without a video fails with "This photo has no video
    Filee can read"; why goes to the log.
  - Not read: vendor formats without this metadata (Huawei's "LIVE_" trailer, for one).
- **Making Live / Motion Photos** (experimental until confirmed on phones; *A video becomes* on a JPG or HEIC preset,
  `MediaOptions.LivePhoto`; the presets "Live Photo (iPhone)" and "Motion Photo (Android)", JPG, on the back of the
  video donut, added in settings schema 31): a short clip of a video becomes a moving photo instead of one still
  frame.
  - Routes: the live edges (`ConversionEdge.LivePhoto`, every video → JPG and HEIC) are taken only when
    `Preset.MakesLivePhotoOf` the file (an iPhone Live Photo as JPG or HEIC, a Motion Photo as JPG,
    `MediaOptions.LivePhotoTargets`), and then as the whole route (`RoutePlanner.Plan(from, to, livePhoto: true)`); "Save
    every frame" is ignored meanwhile. Other files and targets convert as usual.
  - The clip (`Media/LivePhotoWriter.cs`): *Clip start*, *Clip length* (0.5 to 5 s, 3 by default, shorter when the
    video ends sooner; a start past the end is refused) and *Photo at* (seconds into the clip, 1.5 by default, the last
    frame at most). FFmpeg encodes it at 30 fps, upright (the phone's rotation is applied to the pixels), scaled to at
    most 1440 px on the shorter side (or the preset's maximum height), 8-bit 4:2:0 H.264 (HEVC when the preset asks,
    tagged `hvc1`), the sound as AAC (mono or stereo, 44.1 or 48 kHz) unless the preset removes it. HDR (PQ or HLG) is
    tone-mapped to SDR BT.709 with zscale; an FFmpeg without zscale refuses HDR with a message. The photo is the same
    frame at the video's full size, picked by the same 30 fps timing (`select` on the frame number the clip really has,
    counted from its `stsz`), encoded at the preset's image quality.
  - **iPhone Live Photo**: a JPG (or HEIC) and a MOV with the same name and one content identifier (a new upper-case
    UUID), the pair Photos imports as a Live Photo. The photo carries it in Apple's maker note (Exif `MakerNote`,
    "Apple iOS" big-endian, tag 17, ASCII), HEIC through the HEIC writer's Exif item. The MOV (QuickTime, `qt  ` brand)
    gets, as an iPhone writes them: `com.apple.quicktime.content.identifier` in a movie-level `meta` (plain container,
    `mdta` handler, `keys`, `ilst` with a UTF-8 `data` item), and a timed metadata track for the still: track flags 0xF,
    `tref/cdsc` to the video track, a `mhlr/meta` handler, `gmhd`, an alias data reference, a `mebx` sample entry whose
    key 1 is `mdta` + `com.apple.quicktime.still-image-time` typed as a signed 8-bit integer (well-known type 65), one
    9-byte sample (the item, value -1) of one tick at the movie's timescale, placed by an empty edit at the photo's
    time (`AppleLivePhoto.TagMovie`). FFmpeg leaves the movie header at the end, so only that header is rewritten: the
    sample goes into a small `mdat` where it was, and no chunk offset moves. Not written: the other timed metadata of
    an iPhone (live-photo-info, the still's transform, video orientation) and its camera keys, which Photos does not
    need to pair the files.
  - **Android Motion Photo**: one `NAME.MP.jpg` (the name a Pixel gives them): the JPEG, then the clip as MP4 appended,
    ending the file. Its XMP APP1 (after the JFIF APP0 and an Exif APP1) is laid out as a Pixel's:
    `GCamera:MotionPhoto` 1, `MotionPhotoVersion` 1, `MotionPhotoPresentationTimestampUs` (the photo's time in the
    clip), and a `Container:Directory` of the primary JPEG and the video (`video/mp4`, `MotionPhoto`, its length,
    `Padding` 0); the older `GCamera:MicroVideo` properties (the same length as `MicroVideoOffset`) are written too for
    older Google Photos (`MotionPhoto.WriteAsync`). Samsung's own SEF trailer is not written.
  - Expected to work, to be confirmed on devices: Photos on iPhone, iPad and Mac with both files imported together
    (iCloud Photos, AirDrop of both, Image Capture or Photos on a Mac; a single file sent through a messenger loses the
    pairing), Google Photos on Android, iPhone and the web, and galleries that read Google's Motion Photo XMP (Samsung
    Gallery on recent One UI is likely but untested). Not expected: galleries that only know their maker's own format.
  - Everything is written in the work directory and moved into place at the end, first next to its target under a
    hidden name, then renamed over it: a failed or cancelled run never touches an existing file. The pair keeps one
    name: when the MOV's name is taken (another file, or the video being converted, which is never written over) both
    take the next number (`clip (2).jpg` + `clip (2).mov`); a MOV is only written over together with the photo
    "overwrite" replaces; "skip" skips the pair when the photo exists. Filee's own extraction (above) reads both kinds
    back.
- Pictures → MP4/WEBM/MOV go through a full-size PNG from the image engines (HEIC → PNG → MOV): a 5-second still at
  30 fps, one quality step above the preset (identical frames cost almost nothing; x264 `-tune stillimage`). The
  PNG's ICC profile is read (`-flags2 +icc_profiles`), so primaries and transfer are tagged as the photo's (Display
  P3 stays P3); RGB → BT.709 YUV in TV range; transparency is premultiplied onto black. The size is kept (made even)
  unless it exceeds H.264/VP9 level 6.2 (8192 × 4352 pixels, 16384 per side) or the preset's maximum height. The
  image engines' still picture → GIF and → APNG edges cost 12 (`ImageEncoder.Edge`) and PNG → video 11, so a photo
  takes the PNG route (21 < 22) while animated GIF/WebP/MNG, multi-page TIFF and PDF keep the GIF route (20): a moving
  video or a slideshow of the pages. APNG → video costs 11, so an APNG goes straight to video and nothing else detours
  through it.
- **Images → HEIC** (`Heic/HeicWriter.cs`): ImageMagick has no HEVC encoder and FFmpeg no HEIF muxer, so the work is
  split. ImageMagick reads any image it knows and applies the preset (orientation, resize, metadata; transparency is
  flattened onto the background colour, HEIC alpha needs an auxiliary picture Filee does not write) and writes an
  8-bit PNG. FFmpeg pads it to whole 512 × 512 tiles with its own edge pixels (`fillborders`), converts to BT.601
  full-range 4:2:0 like JPEG and cameras, cuts the tiles (`untile`) and has x265 encode each as an IDR picture of
  the Main profile (`-force_key_frames expr:1 -forced-idr 1`, `open-gop=0`; with `keyint=1` x265 would signal the
  "Main Intra" range extensions profile, which the `heic` brand does not allow and Windows decodes wrongly). Pictures
  up to 512 × 512 are one coded picture. Quality 1–100 maps to CRF 50 − 0.4 × quality (90 → 14, 80 → 18).
  `HevcStream` splits the Annex B output and builds the hvcC record; `HeifFile` writes ftyp `heic`/`mif1`, a grid
  item cropping the tiles to the picture size, hidden tile items, `ispe`/`pixi`, the ICC profile as `colr prof` (or
  `nclx` sRGB, BT.601 full range when there is none) and Exif (orientation reset to 1, thumbnail removed) as a `cdsc`
  item. Checked with libheif (Magick.NET), FFmpeg's HEIF demuxer and Windows' HEIF/HEVC extensions; a smooth 2000 ×
  1500 picture is a quarter the size of a JPG at the same PSNR.

## Text recognition (OCR)

"Recognize text" (`PdfOptions.Ocr`, issue #288) in a preset for PDF, DOCX, TXT, Markdown or HWPX reads the text of
scans and pictures with Tesseract (`Ocr/TesseractConverter.cs`), in the language the preset names
(`PdfOptions.OcrLanguage`, a Tesseract code; English by default). The built-in "Searchable PDF" preset turns it on.

- The route planner puts an OCR edge first (`ConversionEdge.Ocr`, `RoutePlanner.Plan(ocr: true)`): picture or PDF →
  searchable PDF, then the cheapest ordinary route from that PDF. A picture Tesseract can't open (HEIC, PSD, …)
  becomes a PNG first. OCR edges are never part of an ordinary route.
- A searchable PDF keeps the page as it looks and puts the recognized words on it as invisible text. PDF pages that
  already have text (visible or an OCR layer) are copied as they are. A scan with a stamp still counts as a scan: a
  picture over at least half the page whose only text is visible and covers under 1% of it (a page number or Bates
  number the scanner software added). Only the others are rendered at 300 dpi
  (PDFium) and recognized page by page: a page is rendered when one of at most four Tesseract runs (fewer on PCs with
  fewer cores) is free, each with one thread (`OMP_THREAD_LIMIT=1`), and its picture is deleted once its PDF exists,
  so a long scan never has more than four pictures in the work folder. A PDF whose
  pages all have text is copied with a note. The new document keeps the pages and the document information, not
  bookmarks or form fields.
- DOCX, Markdown and HWPX read the OCR layer as the page's text (`PdfDocumentReader.ReadWithOcrLayer`): the words
  become paragraphs, the scan behind them is left out, and Tesseract's placeholder font is dropped. Without OCR an
  OCR layer stays invisible there and the page stays a picture, as before. TXT always reads it.
- PDFs merged into one ("Merge PDF" with OCR) are recognized before they are merged.
- Pictures without a resolution are read at 300 dpi; Tesseract would otherwise assume 70 dpi and make a huge page.
- The language data is copied into the job's folder and passed with `--tessdata-dir`, so a system Tesseract reads
  only Filee's pinned files; `-c tessedit_create_pdf=1` replaces the `pdf` config file that lives in Tesseract's own
  tessdata folder.

### Apple Vision on macOS

On a Mac the same step runs with Apple Vision (`Ocr/VisionConverter.cs`, `VNRecognizeTextRequest` at the accurate
level and the newest revision the Mac has), part of macOS, so nothing is downloaded. It has the same OCR edges as
Tesseract and is registered before it, so the route planner takes it; on Windows and Linux it is unavailable
("Not available on this platform").

- The preset's language (a Tesseract code) is mapped to Vision's identifiers (`VisionLanguages`) and asked for with
  English next to it; if Vision refuses the pair it reads with the language alone. Which languages Vision reads comes
  from the Mac itself (`supportedRecognitionLanguagesAndReturnError:`, more with newer macOS versions); a language it
  doesn't read (Hindi, Nepali, Greek, …) goes to Tesseract when that is installed, and is refused otherwise with
  "Apple Vision can't read this language. Install Tesseract …". The preset editor shows no download size for the
  languages Vision reads, and its hint names Apple Vision.
- PDFs go page by page exactly like Tesseract's (`TesseractConverter.RecognizePdfAsync`, at most two pages at a time).
  A JPEG picture goes into the PDF as it is, other pictures (and a JPEG that EXIF turns) as an upright PNG; every page
  of a TIFF becomes a page. The page size comes from the picture's resolution, 300 dpi when it has none.
- Each page is the picture with the recognized words as invisible text (`OcrPdfWriter`, render mode 3): one font
  size per line (its height), the baseline at the line's bottom, each word stretched (`Tz`) to where the next one
  starts with its space, so search, selection and copying follow the picture. Vision gives a box per word
  (`boundingBoxForRange:error:`); without one the words are spread over the line by their length.
- The text is set in a TrueType font with only empty glyphs, built in code (`GlyphlessFont`): every UTF-16 unit is
  one character code (`Identity-H`), shown as glyph 1, and the `ToUnicode` map gives it back unchanged, so any
  script works without a font in the file. `PdfDocumentReader` and `PagesWithText` read the layer like Tesseract's.
- Vision is called through the Objective-C runtime (`Ocr/AppleVision.cs`), without a bridging library. A `CGRect`
  result needs `objc_msgSend_stret` on Intel Macs; objects made with `alloc`/`init` are released, the rest go with an
  autorelease pool per page. A cancelled job cancels the running request.
- Tests: `VisionLanguageTests`, `OcrTextLayerTests` and `VisionConverterTests` (a stand-in recognizer) run everywhere;
  `AppleVisionTests` read real pictures on macOS (CI's Apple silicon and Intel jobs).
