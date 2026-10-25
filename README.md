<p align="center">
  <a href="https://filee.sh"><img src="docs/media/banner-en.jpg" alt="Filee: Drag. Drop. Converted." width="100%"></a>
</p>

<p align="center">
  <b>A free, open-source file converter you use by dragging.</b><br>
  Hold a key (<kbd>Ctrl</kbd>, <kbd>Option</kbd> on macOS), drag files and drop them on a format in the donut that opens at your cursor.
</p>

<p align="center">
  <a href="https://filee.sh"><b>Website</b></a> ·
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><b>Download</b></a> ·
  <a href="https://www.linkedin.com/company/filee-sh/"><b>LinkedIn</b></a> ·
  <a href="#install">Install</a> ·
  <a href="#command-line">Command line</a> ·
  <a href="#development">Development</a>
</p>

<p align="center">
  <a href="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml"><img src="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/WDHUS4G/Filee?color=7F77DD&label=release" alt="Latest release"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases"><img src="https://img.shields.io/github/downloads/WDHUS4G/Filee/total?color=1D9E75" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/Linux-x64%20%7C%20ARM64-3C3489" alt="Linux x64 | ARM64">
  <img src="https://img.shields.io/badge/macOS-preview-534AB7" alt="macOS preview">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MIT license"></a>
</p>

<p align="center">
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/weekly?language=C%23&v=2" alt="Filee: #1 C# repository of the week on Trendshift" width="250" height="55"></a>
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/daily?language=C%23&v=2" alt="Filee: #1 C# repository of the day on Trendshift" width="250" height="55"></a>
</p>

<p align="center">
  <b>English</b> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.zh-CN.md">简体中文</a> ·
  <a href="README.zh-TW.md">繁體中文</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.pt-PT.md">Português (Portugal)</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.th.md">ไทย</a>
</p>

<table>
  <tr>
    <td width="33%" valign="top">
      <b>Right at your cursor</b><br>
      No app window to open.
      The formats appear where you already are, only the ones that make sense for the files you drag.
    </td>
    <td width="33%" valign="top">
      <b>263 formats, no other software</b><br>
      Images, PDF, Word, Excel, PowerPoint, HWP, e-books, archives, fonts, CAD, 3D models and geospatial data work without Office, Hancom Office or LibreOffice.
    </td>
    <td width="33%" valign="top">
      <b>Stays on your computer</b><br>
      Nothing is uploaded.
      Filee works offline and saves the result next to the original.
    </td>
  </tr>
</table>

## See it in action

<p align="center">
  <a href="https://filee.sh"><img src="docs/media/demo-en.webp" alt="Dragging trip.jpg with Ctrl held: a donut of formats opens at the cursor, the file is dropped on PNG, a progress ring completes and trip.png appears" width="820"></a><br>
  <sub>Watch the 27-second video and try the interactive demo on <a href="https://filee.sh">filee.sh</a></sub>
</p>

## Install

Download from [Releases](https://github.com/WDHUS4G/Filee/releases/latest).

- **Windows 10/11**: run `Filee-<version>-win-Setup.exe`, or unzip the `win-Portable.zip` and run `Filee.exe`.
  - Setup is Filee's own installer, in Filee's languages, and needs nothing else on the PC.
  - It can add the Explorer menu, start at sign-in, the `filee` command and optional engines.
  - Silent installs take Inno Setup's switches (`/VERYSILENT /SUPPRESSMSGBOXES`).
- **macOS 13+** (preview): unpack the `osx-arm64` or `osx-x64` package and run `./install.sh`.
  - Or open the `.dmg` and drag Filee to Applications (the `filee` command comes with `install.sh`).
  - The build isn't notarized yet, so allow the first start in System Settings → Privacy & Security → **Open Anyway**.
- **Linux x64/ARM64** (Ubuntu 22.04+): unpack the package and run `./install.sh` (no root, needs Python 3).
  - Drag gestures need X11.
    On Wayland, use the drop zone and the file manager's right-click menu.

Filee shows new releases in its menu and updates itself, keeping your settings and engines.
After an update, a few slides show what's new.

**Windows: Filee doesn't start (no window, no error)?**
This happens on Windows 10 that hasn't been updated for a long time, on a PC whose processor supports Hardware-enforced Stack Protection (CET).
Windows turns that protection on but lacks a part that .NET needs, so Filee and Setup stop right away.

- **Recommended:** install the available Windows updates (the latest cumulative update).
- **If updating isn't possible:** turning the protection off for Filee alone usually works.
  Run this in PowerShell as administrator:

```powershell
Set-ProcessMitigation -Name Filee.exe -Disable UserShadowStack
Set-ProcessMitigation -Name filee-cli.exe -Disable UserShadowStack
```

Do the same with Setup's file name (e.g. `Filee-<version>-win-Setup.exe`), or use the portable ZIP, which needs only these two lines.
To undo it, remove the entries in Windows Security → App & browser control → Exploit protection settings → Program settings.

## Features

- **A donut at the cursor**: hold a key, drag files and drop them on a format.
  Only formats that fit the files appear.
- **Straight from the browser**: drag a picture, a link to a file or a download out of Chrome, Edge, Firefox or Safari the same way.
  - Filee saves it to your Downloads folder (or keeps only the converted file) and converts it.
  - Links are downloaded without your browser's sign-in.
- **One toolbar per file type**, arranged by drag and drop, with presets for:
  - quality, size, DPI
  - PDF pages and compression
  - video and audio
  - output folder and file names
- **Two sides to every donut**: press <kbd>Tab</kbd> or turn the mouse wheel while it is open to flip it over to less common formats and tools.
- **Crop before converting**: the Crop slice opens pictures one by one.
  Drag the frame, pick a ratio, and only that part is saved.
- **Many files at once**, in parallel.
  Routes with several steps (HWP → HWPX → DOCX) are found automatically.
- **Watch folders** convert every file that lands in them.
- **Recent conversions** on Home, each with a thumbnail: open the result, or open it with another app.
- **Other ways in**: the right-click menu, a keyboard shortcut, the drop zone and the [`filee` command](#command-line).
- Light and dark themes with your own accent colour and donut opacity, in English, Español, 日本語, 한국어, 简体中文, 繁體中文, Polski, हिन्दी, Русский, العربية (right to left), Português (Brasil), Français, नेपाली, ไทย, Türkçe and Português (Portugal).

## Screenshots

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="Donut toolbar editor with profiles and presets"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="Home screen with the drop zone and recent conversions"></td>
  </tr>
  <tr>
    <td align="center"><sub>Arrange the donut by drag and drop, one profile per file type</sub></td>
    <td align="center"><sub>Home: drop zone and recent conversions</sub></td>
  </tr>
</table>

## Formats

| From | To |
|---|---|
| **Images**: JPG, PNG, WEBP, AVIF, TIFF, BMP, GIF, APNG, ICO, ICNS, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, HEIC, QOI, DDS, PCX, MNG, CUR, …;<br>GIMP XCF, camera RAW (CR2, CR3, NEF, ARW, DNG, …), DICOM, OpenEXR, HDR, FITS, JPEG XR (Windows), iPhone Live Photo (LIVP) read | any of these (HEIC⁵), PDF, SVG (traced), resized, grayscale;<br>Live/Motion Photos (iPhone, Android) → their video (MP4, MOV⁵), and a video → Live Photo (iPhone) or Motion Photo (Android)⁵, experimental |
| **Vector**: SVG, SVGZ, EMF, WMF, AI<br>EPS, PS¹<br>CDR, VSD, CGM, ODG, FODG² | PDF (vector), PNG and other images,<br>SVG ↔ SVGZ, SVG → DXF/DWG, PDF → EPS/PS¹, SVG → EMF, WMF, ODG² |
| **PDF** | DOCX, HWPX, TXT, PNG, JPG, TIFF, CBZ, PPTX (a picture of each page), SVG²;<br>merge (in an order you choose first), split, page ranges, compress (smaller PDFs) |
| **Documents**: DOCX, DOCM, DOTX, EML, Outlook MSG;<br>OFD (Chinese e-invoices, official documents) read<br>DOC, ODT, FODT, OTT, RTF, WPS, WPD, Pages, ClarisWorks, MacWrite, T602, …² | PDF, DOCX, HWPX, EPUB, TXT, HTML;<br>MSG → EML<br>ODT, FODT, OTT² |
| **Spreadsheets**: XLSX, XLSM, XLS, XLT, ODS, CSV, TSV<br>OTS, FODS, Numbers, ET² | PDF, XLSX, ODS, CSV, TSV, JSON, HWPX, HTML<br>XLT, OTS² |
| **Presentations**: PPTX, PPTM, POTX, POTM, PPSX<br>PPT, ODP, OTP, FODP, Keynote² | PDF, PNG, JPG, DOCX, HWPX<br>OTP² |
| **HWP**: HWP, HWPX | PDF, PNG, DOCX, HWPX ↔ HWP, TXT, Markdown, HTML |
| **Text**: TXT, LOG, script source (PY, PAC, BAT, COMMAND; read, never run), Markdown, HTML;<br>MHTML (saved web pages) read<br>reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML³ | UTF-8 TXT, PDF, DOCX, HWPX, EPUB<br>each other, PPTX slides³ |
| **E-books**: EPUB, KEPUB, MOBI, AZW3, AZW, AZW4, FB2, CBZ, CBR, CB7, HTMLZ, TXTZ<br>LIT, LRF, CHM, PDB, …⁴ | PDF, EPUB, KEPUB (Kobo), DOCX, TXT, HTML, HWPX<br>MOBI, AZW3⁴ |
| **Video**⁵: MP4, MOV, MKV, WEBM, AVI, WMV, FLV, MPEG, Video CD (DAT), TS, M2TS, 3GP, OGV, VOB, … | MP4 (H.264, HEVC, AV1), WEBM, MOV, MKV, AVI, GIF, APNG, MP3, M4A, 720p,<br>subtitles (SRT, ASS, WebVTT) |
| **Audio**⁵: MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, WMA, AIFF, AMR, APE, WavPack, TTA, DSD (DSF, DFF), DTS, AC3, E-AC3, … | MP3, M4A (AAC, ALAC), WAV, FLAC, OGG, OPUS, AAC, WavPack, TTA, W64, AC3, E-AC3 |
| **Subtitles**: SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC | each other as UTF-8 (Korean CP949 files read correctly) |
| **Archives**: ZIP, ANSYS Workbench (WBPZ), 7Z, RAR, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, ISO, CAB, DMG, ALZ, EGG, … | extract, ZIP, 7Z, TAR, TAR.GZ;<br>any files → one ZIP |
| **CAD**: DWG, DXF | PDF, SVG, PNG, DWG ↔ DXF |
| **Fonts**: TTF, OTF, WOFF, WOFF2, EOT | each other |
| **Data**: JSON, JSON Lines, YAML, TOML, XML<br>vCard, iCalendar | each other, CSV and XLSX (tables);<br>vCard, iCalendar ↔ CSV |
| **3D models**: GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY;<br>FBX read | each other, PNG preview |
| **Geospatial vectors**: Shapefile, GeoJSON, KML, KMZ, GPX<br>CSV with coordinates | each other and CSV / XLSX / JSON;<br>projected data (UTM, Web Mercator, Lambert, Albers) comes out as WGS 84 longitude / latitude |

Unmarked formats work out of the box, without Office, Hancom Office or LibreOffice.
Marked ones need an optional engine, downloaded only when needed: ¹ Ghostscript, ² LibreOffice, ³ Pandoc, ⁴ calibre, ⁵ FFmpeg ([details](docs/ENGINES.md)).

## Command line

The `filee` command uses the same engines and presets as the app (Setup or `install.sh` adds it).

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

Every command and option: [filee.sh/cli](https://filee.sh/cli/) or `filee help`.

## Development

Build with the .NET 10 SDK on Windows, macOS or Linux:

```bash
git clone https://github.com/WDHUS4G/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # optional engines
dotnet run --project src/Filee.App
dotnet test
```

[Architecture](docs/ARCHITECTURE.md) · [Engines](docs/ENGINES.md) · [Adding a converter](docs/ADDING-A-CONVERTER.md) · [Adding a language](docs/ADDING-A-LANGUAGE.md) · [Contributing](CONTRIBUTING.md)

Pull requests are welcome.
If Filee saves you some clicks, a ⭐ helps other people find it.

## Code signing policy

Filee is developed and published by **Myeonggu Kim** ([KnifeLemon](https://github.com/KnifeLemon)).

- Windows releases are signed with a [Certum](https://www.certum.eu/) open source code signing certificate issued to Myeonggu Kim ("Open Source Developer MYEONGGU KIM").
  - Releases before it, up to 2.0.0, are not signed.
  - SmartScreen can still warn about a new release until it has been downloaded enough ("More info" → "Run anyway").
- macOS packages are ad-hoc signed and not notarized.
- Only files built by this repository's GitHub Actions release workflow are signed, and every release is signed and approved by hand.
  - Libraries and programs their makers signed (.NET, SkiaSharp, ImageMagick, the Microsoft C++ runtime …) keep their own signatures; bundled ones that come unsigned (rhwp, 7-Zip and most libraries) are signed with Filee's certificate, as part of Filee.

### Privacy policy

Filee converts files on your own computer and never uploads them.
It collects no usage data and sends no telemetry.
It connects to the internet only:

- to ask GitHub (`api.github.com`) whether a newer release exists, shortly after start and then every few hours.
  Turn this off in Settings → About ("Check for updates automatically").
- to download the conversion engines you choose to install, from the official locations pinned in [`engines.json`](src/Filee.Engines/Infrastructure/engines.json), and the text recognition (OCR) languages a preset uses, pinned in [`ocr-languages.json`](src/Filee.Engines/Infrastructure/ocr-languages.json).
- when you click a link, which opens your web browser.

## Sponsor

Filee is free and stays free.
If you'd like to support it, pick whichever way suits you ([Fairy](https://fairy.hada.io/@filee) takes Korean cards and easy-pay).

**Your name or logo here.**
Monthly backers are listed here by name, and sponsors get their logo here and on Filee's About page.
One-time supporters can ask to be listed too.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## License

MIT, © 2026 Myeonggu Kim (KnifeLemon) and Filee contributors.
Bundled third-party components keep their own licenses, listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#WDHUS4G/Filee&Date"><img src="https://api.star-history.com/svg?repos=WDHUS4G/Filee&type=Date" alt="Star history" width="600"></a>
</p>
