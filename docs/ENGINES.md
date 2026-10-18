# Conversion engines

Filee chooses engines automatically. Settings → *Engines* shows their status, lets you change the priority
(higher wins when two engines can do the same conversion), and install or remove the optional engines.

| Engine | How it gets there | Handles |
|---|---|---|
| ImageMagick (Magick.NET) | built in (library) | JPG, PNG, WEBP, TIFF, BMP, GIF, ICO, AVIF, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, MNG, QOI, DDS, PCX, CUR, XPM, XBM, WBMP, PAM, PFM, SGI, Sun Raster, MIFF ↔ each other, and **APNG** (Filee's own reader and writer); HEIC (written with FFmpeg), XCF, camera RAW (LibRaw), DICOM (uncompressed ones by Filee's own reader), Photo CD, OpenEXR, Radiance HDR and EMF/WMF (Windows) read |
| Vector graphics | built in (Svg.Skia, SkiaSharp, ACadSharp) | **SVG/SVGZ → PDF (vector) and images**, SVG ↔ SVGZ, **SVG → DXF** (→ DWG with the CAD engine), **raster images → SVG (tracing)**, PDF-compatible AI → PDF |
| ICNS | built in | images → ICNS (16–1024 px), ICNS → images |
| PDFsharp | built in (library) | images → PDF, merge, split, page ranges |
| PDFium | built in (library) | PDF → images (multi-page TIFF/GIF supported) |
| Unhwp | built in (library) | HWP/HWPX → TXT, Markdown, HTML |
| Markdown (Markdig) | built in (library) | **Markdown → HTML, TXT** (and → PDF / DOCX through the HWPX writer, rhwp or LibreOffice) |
| Spreadsheets (ExcelDataReader for XLS) | built in (library) | **XLSX, XLS, ODS, CSV, TSV → XLSX, ODS, CSV, TSV** (one CSV / TSV per sheet) |
| Office Open XML | built in | **DOCM / DOTX / DOTM ↔ DOCX, XLSM / XLTX ↔ XLSX, PPTM / POTX / PPSX ↔ PPTX** (macros removed for macro-free types) |
| Fonts | built in | **TTF, OTF, WOFF, WOFF2, EOT ↔ each other**; CFF (PostScript) outlines become TrueType for TTF and EOT |
| **HWPX writer** | built in | **DOCX (+ DOCM/DOTX/DOTM), XLSX (+ XLSM/XLTX), XLS, ODS, CSV, TSV, PPTX (+ PPTM/POTX/PPSX), PDF, HTML, EPUB, MOBI/AZW3, FB2, HWPX, TXT, Markdown → HWPX** (and with rhwp → PDF and images); ODT, RTF, reStructuredText, LaTeX → HWPX with Pandoc |
| **DOCX writer** | built in | **HWPX (and HWP through rhwp), PDF, HTML, EPUB, MOBI/AZW3, FB2, Markdown, TXT, XLSX, XLS, ODS, CSV, PPTX (+ variants) → DOCX** |
| PDF text | built in (PdfPig) | **PDF → TXT** (reading order, also two columns; no OCR) |
| E-mail | built in (MimeKit, MsgReader) | **EML, Outlook MSG → HTML** (headers + body, inline pictures), **TXT**, **ZIP** (the attachments); **MSG → EML** |
| Data | built in (System.Text.Json, YamlDotNet, Tomlyn, System.Xml) | **JSON, JSON Lines, YAML, TOML, XML, CSV ↔ each other** (CSV for tables); **vCard ↔ CSV**; spreadsheets and contacts reach JSON / YAML / TOML / XML through CSV |
| **E-books** | built in | **EPUB, MOBI/AZW/AZW3/PRC, FB2, HTMLZ, TXTZ → EPUB, HWPX (→ PDF), TXT, HTML, Markdown, FB2, HTMLZ, TXTZ**; HTML, Markdown, TXT, DOCX → EPUB/FB2/HTMLZ/TXTZ; HTML → TXT; comics CBZ/CBR/CB7/CBT/CBC → PDF, EPUB, CBZ; PDF → CBZ; AZW4 → PDF; **EPUB ↔ KEPUB** (Kobo) |
| CAD (ACadSharp + built-in renderer) | built in (library) | **DWG ↔ DXF**; DWG/DXF → **PDF** (vector), **SVG**, PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF |
| **Subtitles** | built in | **SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC → SRT, ASS, WebVTT, SAMI, LRC, TTML, MicroDVD**, always UTF-8 (Korean CP949 / EUC-KR files read correctly) |
| 3D models (SharpGLTF for glTF) | built in (library) | **GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY ↔ each other**; → PNG, JPG, … (an isometric preview) |
| rhwp | bundled with the installer (`engines/rhwp`) | HWP/HWPX → PDF, **HWP → HWPX, HWPX → HWP** |
| Archives (7-Zip) | bundled with the installer (`engines/7zip`, ~2.5 MB) + built-in readers | ZIP, 7Z, RAR, TAR (+ GZ/BZ2/XZ/Z/7Z/LZ), CAB, ISO, DMG, … **→ folder, ZIP, 7Z, TAR, TAR.GZ/BZ2/XZ**; ALZ, EGG, lzip read in-process; "Compress into one archive" for any files |
| LibreOffice + H2Orestart + Java | **optional download** (~420 MB, 1.3 GB on disk) | older and rare formats only: DOC, XLS, PPT, RTF and OpenDocument → PDF and to each other, output as DOCX/ODT/ODS/ODP; HWP/HWPX → DOCX/ODT; read-only formats (see below) → PDF and their kind's editable formats; FODT, OpenDocument templates (OTT, OTS, OTP) and XLT out; SVG → EMF/WMF/ODG, a PDF page → SVG |
| Pandoc | **optional download** (~42 MB, 240 MB on disk) | Markdown ↔ DOCX/ODT/RTF, DOCX/ODT/HTML/RTF → Markdown, HTML ↔ DOCX/ODT, markup languages (reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML) ↔ Markdown/HTML/DOCX/ODT and each other (and → RTF, EPUB, TXT), Markdown and markup → PPTX slides |
| Ghostscript | **optional download** (~20 MB, 31 MB on disk) | EPS/PS → PDF, PDF → EPS/PS, PostScript-based AI → PDF (images through PDF and PDFium) |
| FFmpeg | **optional download** (~100 MB, 272 MB on disk) | **all video and audio**: video ↔ video (H.264, HEVC, AV1), video → animated GIF / APNG or a still frame, audio and subtitle extraction, audio ↔ audio (also APE, WavPack, TTA, DSD, DTS, ALAC, …), GIF, APNG and photos → MP4/WEBM/MOV, images → HEIC |
| Calibre | **optional download** (~216 MB, 660 MB on disk) | rare e-book formats: LIT, LRF, CHM, PDB, PML, RB, SNB, TCR, OEB → EPUB; EPUB → MOBI, AZW3, LIT, LRF, PDB, PML, RB, SNB, TCR |

DOCX, XLSX, XLS, ODS and PPTX → PDF need neither Microsoft Office nor LibreOffice: they are read in-process, written as HWPX
and rendered by rhwp. Anything the built-in readers understand (including PDF) is written as DOCX by the DOCX writer.
LibreOffice's and Pandoc's costs are set so the built-in route always wins where one exists.

All built-in document readers are registered in one place, `Hwp/Hwpx/DocumentReaders.cs` (format id → reader, and
which formats need Pandoc). The HWPX writer and the DOCX writer take their input formats from it: adding a reader
there is one line and makes both writers (and every route through them) accept the format.

Every engine is built in, bundled with the installer or downloaded into Filee's own folder, so a conversion behaves
the same on every PC. An external engine is looked for in this order:

1. a copy the user picked in Settings → *Engines*;
2. Filee's own copy (bundled or downloaded);
3. a copy already on the computer: on `PATH`, in Homebrew's and the usual Unix folders, or where the LibreOffice,
   calibre and Ghostscript installers put it. Settings → *Engines* shows it as "Found on this PC", with a note when
   its version is older than the one Filee downloads.

Where Filee has no download of an engine (FFmpeg and Ghostscript on macOS, LibreOffice and Ghostscript on Linux),
step 3 is how it is found. Microsoft Office
and Hancom Office are never used, even when installed. Running from source, the repository's `engines/` folder
(filled by `build/fetch-engines.ps1`) is used.

## Optional engines

The installer contains only the small engines. On first start Filee asks which of the large ones to download;
they can be installed or removed later in Settings → *Engines*. A conversion that needs a missing engine shows
"Needs LibreOffice …" on its donut slice.

- Downloads are pinned in `src/Filee.Engines/Infrastructure/engines.json` (URL, SHA-256, size). The same file is
  read by `build/fetch-engines.ps1`, so development and the app always use the same versions.
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

To bump a version, change `url`, `sha256` and `size` in engines.json together, run
`pwsh build/fetch-engines.ps1`, test, and update `THIRD-PARTY-NOTICES.md`. When the LibreOffice team retires a
version from `download.documentfoundation.org/libreoffice/stable/`, its URL stops working: keep the pinned version
current.

## Images and vector graphics

- **Animations.** An animated GIF, WebP, APNG or MNG keeps every frame and its timing when converted to one of these
  (animated GIF ↔ animated WebP ↔ APNG ↔ MNG, WebP lossy or lossless as the preset says) and becomes a multi-page TIFF;
  other targets get the first frame. Multi-page TIFFs and PDFs stay one image per page in WebP.
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
- **DICOM** (`.dcm`, `.dicom`, read only). The Q8 build reads only 8-bit DICOM, while CT, MR and X-ray scanners store
  12 or 16 bits, so `Magick/Dicom.cs` (written from the DICOM standard, PS3.5 and PS3.3 C.7.6.3 / C.11.2) reads
  uncompressed files itself: implicit and explicit VR little endian, explicit VR big endian and deflated files, with or
  without the 128-byte preamble; 8 or 16 bits allocated, signed or unsigned; MONOCHROME1 (inverted) and MONOCHROME2,
  RGB and YBR_FULL (interleaved or planar); every frame of a multi-frame file. Grey values go through the rescale
  slope / intercept and the file's first window (centre / width) like a viewer's default view, or are stretched over
  the file's range when there is no window, to 8 bits. Compressed pixel data (JPEG, JPEG-LS, JPEG 2000, RLE) is left to
  ImageMagick, which manages only 8-bit images; others fail with a message asking for an uncompressed export.
- **Fujifilm HEIF** (`.hif`) is read as HEIC.
- **Rotate and flip** happen after the EXIF orientation is applied, in the one step of a route that turns images
  (`IImageTransformer`). There is no lossless JPEG rotation: a turned JPEG is re-encoded, at its own estimated quality
  when that is lower than the preset's, so a phone photo saved at 75 doesn't come back bigger at 95.
- **Layered files.** PSD/PSB convert from Photoshop's composite (ImageMagick merges the layers when a file was saved
  without "maximize compatibility"). GIMP XCF layers are composited at their offsets with their blend modes; hidden
  layers are left out. ImageMagick reads 8-bit XCF without zlib tile compression (GIMP 2.10+ "better but slower
  compression" files give a clear error).
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

`Vector/SvgDxfWriter.cs` writes the drawing's outlines with ACadSharp (DXF, AutoCAD 2018 like the CAD engine; DWG is
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
  - tables with merged cells, column widths, borders and shading from the table style and cells, repeated header rows;
  - pictures inline and floating, text boxes, rectangles/ellipses/lines with solid or gradient fills, groups;
  - footnotes, endnotes, bookmarks, hyperlinks (web and within the document, e.g. a table of contents);
  - Word's document grid ("lines"), which 한글 does not have, as an "at least" line spacing.
- **XLSX, XLS, ODS, CSV and TSV** (`Office/Sheets`, see [Spreadsheets](#spreadsheets)): every visible sheet becomes a section with one table. Cells show what Excel
  shows (number formats via ExcelNumberFormat, dates, percentages, cached formula results), with fonts, fills,
  borders, alignment and merged cells; frozen top rows repeat on every page. The page is A4 with narrow margins,
  landscape when the columns are much wider than portrait, and the table is scaled to the page width like
  Excel's "Fit all columns on one page". CSV is read with its delimiter (comma, semicolon, tab) and encoding
  (UTF-8 / UTF-16 with BOM, else CP949 as saved by Korean Excel). Not converted: charts, pictures, conditional
  formatting, print areas and page breaks.
- **PPTX** (`Hwp/Hwpx/Pptx`): one page per slide, sized like the slide, with every object floating at its place:
  placeholders inherit position and text style from the layout and master, text with bullets and numbering,
  shapes (rectangles, rounded rectangles, ellipses, lines) with solid or gradient fills and outlines, pictures,
  tables with PowerPoint's default table style, groups, master decorations and the slide background. Not
  converted: charts, SmartArt, rotation, effects (shadows, glow), picture cropping, animations, notes and hidden
  slides.
- **TXT** needs no reader (one paragraph per line; UTF-8, UTF-16 or the Korean code page CP949).
- **Markdown** is parsed with Markdig (`MarkdownReader.cs`): headings, emphasis, code, lists (1. / a. / i. with
  start numbers), task lists, quotes, tables with spans, links, images, footnotes and math. No Pandoc needed.
- **PDF** is read with PdfPig (see *PDF reader* below); DOCM / DOTX / DOTM, XLSM / XLTX and PPTM / POTX / PPSX
  are read like DOCX, XLSX and PPTX.
- **HTML** is parsed with AngleSharp (`HtmlReader.cs`, `HtmlCss.cs`): headings, paragraphs, bold / italic /
  underline / strike / sub / sup / code / mark, the basic inline CSS (colour, background, font weight, style and
  size, text-align, text-indent, page breaks) and simple `tag` / `.class` rules of style sheets, nested lists with
  start numbers and types, tables with spans / header rows / borders, pictures (local files and data: URIs; remote
  pictures are skipped), links and anchors, quotes, `pre`, `hr` and figures. Scripts, styles, forms and `nav` are
  skipped. The encoding comes from the BOM, the XML declaration or `<meta charset>`, else UTF-8 or the system
  code page. No Pandoc needed.
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

Not converted: Word charts, SmartArt, equations (kept as text), free-form shapes, tracked changes and comments.

## Spreadsheets

`SpreadsheetConverter` (engine id `spreadsheet`) converts between XLSX, XLS (and XLT templates, read only), ODS, CSV and TSV (also
`.tab`) in-process; every format goes through one in-memory workbook (`Office/Sheets/Workbook.cs`) whose cells keep
the text the spreadsheet shows, the value behind it and its Excel number format.

| Format | Read | Written |
|---|---|---|
| XLSX | own reader: values, number formats, fonts, fills, borders, alignment, merges, widths, heights, hidden rows / columns / sheets, frozen rows | every sheet, numbers and dates as numbers with their formats, booleans, styles, merges, widths, heights, hidden rows / columns, frozen rows |
| XLS (Excel 97–2003, also Excel 5/95) | ExcelDataReader (MIT): values, number formats, merges, horizontal / vertical alignment; widths, heights and hidden rows / columns from the BIFF records (`XlsLayout.cs`) | no (LibreOffice) |
| ODS | own reader (`content.xml`, `styles.xml`, `settings.xml`): repeated rows / columns (the empty repeats up to the end of the sheet are skipped), spans, every value type, the text Calc shows (`text:p`), data styles as Excel codes, cell styles, widths, heights, hidden rows / columns / sheets, frozen and header rows | every sheet, typed cells (float, percentage, currency, date, time, boolean) with data styles and the shown text, styles, merges, widths, heights, hidden rows / columns, frozen rows |
| CSV / TSV | delimiter (CSV: comma, semicolon or tab; TSV: tab) and encoding (UTF-8 / UTF-16 with BOM, else CP949) | UTF-8 with BOM, CRLF, one file per sheet |

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
(with `PdfTextConverter`) TXT:

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
- headings from font sizes (short paragraphs clearly larger than the body text, the largest size is level 1), bold
  and italic from the fonts, colours, links from link annotations, alignment, indents, spacing and line pitch;
- pictures: JPEG streams are kept as they are, others are decoded to PNG, and what PdfPig cannot decode is cut out
  of the rendered page; a picture covering the page behind text (the scan under an OCR layer) is skipped;
- page size per page and margins shared by the document; running headers and footers that repeat on most pages
  (and bare page numbers) are left out;
- a page without text (a scan, a page of drawings, a turned page) becomes the rendered page (PDFium, 150 dpi JPEG)
  as a picture behind the text of an empty page, so nothing is lost; there is no OCR. PDF → TXT includes the
  invisible OCR text layer of scanned PDFs that have one.

Password-protected PDFs fail with a clear message. Not converted: tables (their cells come out as paragraphs in
reading order), vector drawings on text pages, form fields and annotations other than links.

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

## Data (JSON, YAML, TOML, XML, CSV, vCard)

`Data/DataConverter.cs` (engine id `data`) reads every format into one tree (`Data/DataTree.cs`: objects with their
keys in file order, arrays, text, true / false, integers of any size, floats, TOML dates and null) and writes it
again, so structure and types carry over as far as the target can hold them. Output is UTF-8 without BOM with LF
line ends, indented by two spaces (CSV: like the spreadsheet engine, UTF-8 with BOM and CRLF for Excel). There are no
options: the output is always pretty-printed.

| Format | Library | Read | Written / lost |
|---|---|---|---|
| JSON (`.json`) | System.Text.Json | comments and trailing commas allowed (JSONC) | non-ASCII text as it is; NaN / infinities as text ("NaN") |
| JSON Lines (`.jsonl`, `.ndjson`) | System.Text.Json | one value per line, blank lines skipped, read as an array | one compact line per record (the array, or the array inside wrappers, see CSV) |
| YAML (`.yaml`, `.yml`) | YamlDotNet (MIT) | YAML 1.2 core schema: unquoted `true`, `null` / `~`, numbers (`0x`, `0o`, `.inf`, `.nan`) are typed, the rest and anything quoted is text (`yes` / `no` stay text); explicit `!!str` / `!!int` … tags; anchors and aliases resolved (copied), merge keys (`<<`) applied; several documents (`---`) become an array; comments dropped; alias "bombs" (more than 5 million values) and nesting deeper than 256 refused | block style; text that would read back as another type (`"true"`, `"007"`, `""`, YAML 1.1's `yes` / `on`, dates) is quoted; multi-line text as a literal block (`\|`) |
| TOML (`.toml`) | Tomlyn (BSD-2-Clause, TOML 1.1) | dates and times keep their TOML spelling (text in JSON / YAML) | written by Filee: plain keys, then `[tables]` and `[[arrays of tables]]`; **null values are left out** (TOML has none); a root that is not an object goes under `items` (`[[items]]`); integers beyond 64 bits as text |
| XML (`.xml`) | System.Xml | root element = an object with one key; attributes as `@name`, text next to attributes or children as `#text`, repeated elements as an array (**one child is no array**), `<a/>` → null, `<a></a>` → empty text, CDATA as text; all values are text (XML has no types); comments, processing instructions and the DTD dropped, external entities never loaded; the encoding from the declaration (EUC-KR …) | a root that is not one object with one key goes into `<root>`, items of nested arrays into `<item>`; names that are not XML names are escaped (`first name` → `first_x0020_name`, unescaped when read back); `x:name` keeps its namespace when `@xmlns:x` declares it; control characters XML 1.0 can't hold are removed |
| CSV (`.csv`) | Filee (`Office/Sheets/CsvFormat.cs`) | first row = keys, one object per row; plain numbers (not `007`) and `true` / `false` typed, the rest text; delimiter and encoding detected as for spreadsheets | only tables: an array of objects (columns = every key, first seen first; an object inside a row becomes `parent.child` columns, one level only), an object of arrays (columns), an array of arrays (rows) or one flat object (one row); objects that only wrap the array (`{"users": […]}`, `<rows><row/>…</rows>`) are looked through. Anything else stops with a message saying what is not a table |

**`.csv` and `.xml` ownership.** CSV stays a spreadsheet format: the spreadsheets donut, XLSX / ODS routes and
"Merge into PDF" are unchanged, and the data engine only adds edges `csv ↔ json / jsonl / yaml / toml / xml`. Routes
combine: XLSX → CSV → JSON, YAML → CSV → XLSX, vCard → CSV → JSON. The spreadsheets donut gets "JSON". `.xml` had no
owner before; XML-based formats Filee converts have their own extensions (`.svg`, `.fb2`, `.dbk`, `.opml` …), so a
plain `.xml` file is treated as data and opens the data donut.

**vCard** (`Data/VCard.cs`, own parser): vCard 2.1, 3.0 and 4.0 → CSV with the columns Full name, Given name, Family
name, Organization, Title, Email, Phone, Address, Birthday, Note (one row per contact; several values joined with
`; `, each with its kind in brackets: `010-1234-5678 (mobile); 02-123-4567 (work)`). Folded lines, quoted-printable
with soft line breaks, `CHARSET` parameters, escapes, groups (`item1.EMAIL`), 2.1's bare types (`TEL;CELL`) and 4.0's
quoted type lists and `tel:` URIs are read; a value without `CHARSET` is UTF-8 when valid, else CP949 (address books
of older Korean phones); UTF-16 files with a BOM are read too. Pictures and nested cards (2.1 `AGENT`) are skipped,
a missing full name is made from the name parts (홍길동, "Ada Lovelace"). CSV → vCard reads those columns (also
`Name`, `Company`, `Mobile`, `E-mail` … and Korean / Chinese headers such as 이름, 전화번호, 이메일, 姓名, 电话) and writes
vCard 3.0 in UTF-8, lines folded at 75 bytes; an address goes into the street field.

## CAD drawings (DWG, DXF)

Built in, no AutoCAD or ODA software (`Filee.Engines/Cad`). [ACadSharp](https://github.com/DomCR/ACadSharp) reads
and writes the files; Filee's own renderer draws them with SkiaSharp (one drawing routine for PDF, SVG and images).

- **Reading**: DWG from R14 (AC1014) to the AutoCAD 2018 format (AC1032, still current in AutoCAD 2026); DXF R12 to
  2018, ASCII and binary. DWG R13 and older fail with "version not supported". Korean AutoCAD writes pre-2007 DXF with
  the code page name `ANSI_949`, which ACadSharp 3.8 does not map; Filee rewrites that header in memory so the text
  is decoded as CP949 (`CadFile.OpenDxf`, covered by a test that fails once ACadSharp handles it).
- **Writing** (DWG ↔ DXF): always **AutoCAD 2018 (AC1032)** — the current DWG format (AutoCAD 2018 and later, DWG
  TrueView, BricsCAD, ODA tools); 2018 DXF stores text as UTF-8. ACadSharp's DWG writer is younger than its reader
  and its DXF writer, so DXF is the safer format to hand on. Objects ACadSharp does not model (proxy and AEC objects,
  some 3D and underlay data) are not written; degenerate dimensions it cannot measure are left out of DWG output
  instead of failing the file.
- **What is drawn**: model space seen from the top (Z is dropped). If model space has nothing visible, the active
  layout (paper space) is drawn instead; viewports inside it are not followed. The page is fitted to the extents of
  what is actually drawn — the header's EXTMIN/EXTMAX are ignored, so drawings far from the origin still fill it.
  - PDF: an ISO A3 sheet (A4 or Letter when the preset asks for it), landscape when the drawing is wider than tall,
    10 mm margins (or the preset's margin). Vector paths, text as text with embedded fonts.
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

`Filee.Engines/Models3D` converts GLB, glTF, STL, OBJ, 3MF and PLY into each other in managed code: every reader
fills one in-memory model (`Model3D`: parts with positions, normals, UVs, vertex colours and triangles; materials
with a base colour and a base colour texture) and every writer takes it, so each pair is one step. glTF and GLB are
read and written with [SharpGLTF](https://github.com/vpenades/SharpGLTF) (MIT); STL, OBJ/MTL, PLY and 3MF are Filee's
own code. No native library (Assimp) and no download.

- **Units**: STL, OBJ and PLY have none; they are read and written as **millimetres**, as slicers and CAD programs
  treat them. 3MF has a `unit` attribute (micron to metre) and is written in millimetres. glTF is metres. So a 20 mm
  STL cube is 0.02 m in GLB and 20 mm again in STL. There is no preset option for another unit.
- **Axes** follow Blender's defaults: STL, 3MF and PLY are Z up, OBJ and glTF Y up, so a part standing on a printer's
  bed stands upright in a glTF viewer and comes back on its base.
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
| PLY | ASCII, binary little / big endian; vertex position, normal, UV (`s`/`t`, `u`/`v`, `texture_u`/`texture_v`), colours of any type; face lists with per-corner `texcoord` and face colours; `comment TextureFile`; other elements skipped | binary little endian, one mesh; material colours become vertex colours |

Lost on the way (no place in the target): **STL** keeps only the triangles (no colours, UVs, normals or materials);
**PLY** has no materials (their colours become vertex colours, textures are dropped); **OBJ** has no standard vertex
colours (the common `v x y z r g b` extension is written and read; alpha is lost); **3MF** output has no normals, UVs or
textures; and every format drops what Filee's model has no place for: animations, skins, morph targets, cameras,
lights, metallic / roughness / normal / emissive maps, and lines and points. Draco- and meshopt-compressed glTF fail with
a clear message, as do damaged files ("cut short", an index that does not exist, no triangles).

**3D → pictures**: a small software rasteriser (`MeshRenderer`) draws an isometric view from the front right, above,
with one light and flat shading per triangle, vertex colours or the texture interpolated across it, and a z-buffer,
at 2048 px and scaled down to 1024 px for smooth edges, on a transparent background. The preset's image options
(resize, background for JPG, quality) apply as for other pictures.

Tests (`tests/Filee.Engines.Tests/ModelTests.cs`) build a cube, a sphere and a fan of coloured triangles in code and
convert each through every format pair, checking triangle counts, bounds, signed volume (winding), normals, UVs,
colours and materials where both formats keep them; hand-written STL, OBJ, PLY, 3MF and glTF files cover the reading
details, and a generated 1,000,000-triangle STL the speed.

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

- The pinned `calibre-64bit-<version>.msi` from download.calibre-ebook.com is unpacked like LibreOffice; the folder
  with `ebook-convert.exe` becomes `engines/calibre`.
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
table is not drawn over the paragraphs below it (#49).

## Archives

The archive engine (`Archives/ArchiveConverter.cs`, id `archive`) uses Filee's own copy of the 7-Zip console program
(`7z.exe` + `7z.dll`, taken from the official x64 MSI with `msiexec /a` by `build/fetch-engines.ps1 -Only 7zip` and
bundled with the installer), never a 7-Zip installed on the PC. It runs as a separate process (LGPL).

| Format | Read | Write |
|---|---|---|
| ZIP, JAR | 7-Zip | ZIP in-process (System.IO.Compression): UTF-8 names with the language-encoding flag, times, empty folders |
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
  a preamble), the text formats extract pictures into `<name>_files` next to the result.

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
frame, to every audio format (the sound track) and to SRT, ASS and WebVTT (the subtitles); every audio format to every
audio format; and GIF, APNG and PNG to MP4, WEBM and MOV.
Same-format edges exist too, so "MP4 720p" works on MP4 files and "MP3 128k" on MP3 files.

- The download is the gyan.dev **9.0.2 "full_build-shared"** Windows build (GPL-3.0, run as a separate program).
  It is smaller than the "essentials" build (100 vs 115 MB download, 272 vs 329 MB on disk, because ffmpeg.exe and
  ffprobe.exe share their libraries) and has a superset of its encoders. It links only Windows system DLLs; GPU
  and Vulkan libraries are loaded only if present. Being over 60 MB, it is offered but not ticked on first run.
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
  | GIF | two passes: palettegen over the whole clip, then paletteuse; 15/12/10 fps and at most 640/480/320 px wide (High/Balanced/Small) |
  | APNG | one pass with the `apng` encoder (24-bit RGB, mixed prediction) and muxer, looping; the same frame rates and widths as GIF |
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
- DSD (DSF, DFF) decodes at 2.8 MHz or more: lossless and PCM targets get 88.2 kHz, Vorbis and WMA 44.1 kHz, the
  other lossy encoders their nearest supported rate.
- Subtitle extraction takes the first text subtitle track (SubRip, ASS/SSA, WebVTT, MP4 timed text, CEA-608, …).
  A video without subtitles fails with "The video has no subtitle track."; one with only picture subtitles (Blu-ray
  PGS, DVD, DVB) says they can't be turned into text without OCR. Video → SAMI, LRC, TTML, … goes through SRT and the
  subtitle engine (ASS and WebVTT extraction cost 11 so SRT is the hub).
- The first video stream (cover pictures excluded) and the first audio stream are converted; files without sound
  still convert to video. Title, artist and other tags and chapters are kept, album art too for MP3, M4A, M4B and
  FLAC. Portrait phone videos are turned upright. Odd sizes are evened out, frame rates that MPEG-2/MXF/MPEG-4 part 2
  cannot store are converted, and channels WMA or AMR cannot hold are mixed down.
- Progress comes from `-progress` (position ÷ duration from ffprobe); the GIF palette pass reports per-frame stats of
  a discarded copy of its frames. Cancelling kills the process tree and deletes the partial output. There is no
  time limit, but FFmpeg is stopped when it reports nothing for 10 minutes. At most two conversions run at a time
  (FFmpeg's encoders use every core themselves).
- Video → PNG writes one still frame (a tenth into the video, where the picture has usually faded in); the image
  engines turn it into JPG, WEBP, PDF, …. The GIF edge costs more, so these routes never go through an animated GIF
  of the whole video.
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
