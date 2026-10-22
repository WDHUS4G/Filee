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
  <a href="#install">Install</a> ·
  <a href="#command-line">Command line</a> ·
  <a href="#development">Development</a>
</p>

<p align="center">
  <a href="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml"><img src="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/WDHUS4G/Filee?color=7F77DD&label=release" alt="Latest release"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases"><img src="https://img.shields.io/github/downloads/WDHUS4G/Filee/total?color=1D9E75" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/macOS%20%7C%20Linux-preview-534AB7" alt="macOS | Linux preview">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MIT license"></a>
</p>

<p align="center"><b>English</b> · <a href="README.ko.md">한국어</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.tr.md">Türkçe</a></p>

<table>
  <tr>
    <td width="33%" valign="top"><b>Right at your cursor</b><br>No app window to open. The formats appear where you already are, only the ones that make sense for the files you drag.</td>
    <td width="33%" valign="top"><b>244 formats, no other software</b><br>Images, PDF, Word, Excel, PowerPoint, HWP, e-books, archives, fonts, CAD and 3D models work without Office, Hancom Office or LibreOffice.</td>
    <td width="33%" valign="top"><b>Stays on your computer</b><br>Nothing is uploaded. Filee works offline and saves the result next to the original.</td>
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
  Setup can add the Explorer menu, start at sign-in, the `filee` command and optional engines.
- **macOS 14+** (preview): unpack the `osx-arm64` or `osx-x64` package and run `./install.sh`.
  The build isn't notarized yet, so allow the first start in System Settings → Privacy & Security → **Open Anyway**.
- **Linux x64/ARM64** (preview): unpack the package and run `./install.sh` (no root, needs Python 3).
  Drag gestures need X11. On Wayland, use the drop zone and the file manager's right-click menu.

Filee shows new releases in its menu and updates itself, keeping your settings and engines.

## Features

- **A donut at the cursor**: hold a key, drag files and drop them on a format. Only formats that fit the files appear.
- **One toolbar per file type**, arranged by drag and drop, with presets for quality, size, DPI, PDF pages,
  video and audio, output folder and file names.
- **Many files at once**, in parallel. Routes with several steps (HWP → HWPX → DOCX) are found automatically.
- **Watch folders** convert every file that lands in them.
- **Other ways in**: the right-click menu, a keyboard shortcut, the drop zone and the [`filee` command](#command-line).
- Light and dark themes, in English, 한국어, 简体中文 and Türkçe.

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
| **Images**: JPG, PNG, WEBP, AVIF, TIFF, BMP, GIF, APNG, ICO, ICNS, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, HEIC, QOI, DDS, PCX, MNG, CUR, …; GIMP XCF, camera RAW (CR2, CR3, NEF, ARW, DNG, …), DICOM, OpenEXR, HDR read | any of these (HEIC⁵), PDF, SVG (traced), resized, grayscale |
| **Vector**: SVG, SVGZ, EMF, WMF, AI · EPS, PS¹ · CDR, VSD, CGM, ODG, FODG² | PDF (vector), PNG and other images, SVG ↔ SVGZ, SVG → DXF/DWG, PDF → EPS/PS¹, SVG → EMF, WMF, ODG² |
| **PDF** | DOCX, HWPX, TXT, PNG, JPG, TIFF, CBZ, SVG²; merge, split, page ranges |
| **Documents**: DOCX, DOCM, DOTX, EML, Outlook MSG · DOC, ODT, FODT, OTT, RTF, WPS, WPD, Pages, ClarisWorks, MacWrite, T602, …² | PDF, DOCX, HWPX, EPUB, TXT, HTML; MSG → EML · ODT, FODT, OTT² |
| **Spreadsheets**: XLSX, XLSM, XLS, XLT, ODS, CSV, TSV · OTS, FODS, Numbers, ET² | PDF, XLSX, ODS, CSV, TSV, JSON, HWPX, HTML · XLT, OTS² |
| **Presentations**: PPTX, PPTM, POTX, PPSX · PPT, ODP, OTP, FODP, Keynote² | PDF, PNG, JPG, DOCX, HWPX · OTP² |
| **HWP**: HWP, HWPX | PDF, PNG, DOCX, HWPX ↔ HWP, TXT, Markdown, HTML |
| **Text**: TXT, Markdown, HTML · reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML³ | PDF, DOCX, HWPX, EPUB · each other, PPTX slides³ |
| **E-books**: EPUB, KEPUB, MOBI, AZW3, AZW, AZW4, FB2, CBZ, CBR, CB7, HTMLZ, TXTZ · LIT, LRF, CHM, PDB, …⁴ | PDF, EPUB, KEPUB (Kobo), DOCX, TXT, HTML, HWPX · MOBI, AZW3⁴ |
| **Video**⁵: MP4, MOV, MKV, WEBM, AVI, WMV, FLV, MPEG, TS, M2TS, 3GP, OGV, VOB, … | MP4 (H.264, HEVC, AV1), WEBM, MOV, MKV, AVI, GIF, APNG, MP3, M4A, 720p, subtitles (SRT, ASS, WebVTT) |
| **Audio**⁵: MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, WMA, AIFF, AMR, APE, WavPack, TTA, DSD (DSF, DFF), DTS, AC3, E-AC3, … | MP3, M4A (AAC, ALAC), WAV, FLAC, OGG, OPUS, AAC, WavPack, TTA, W64, AC3, E-AC3 |
| **Subtitles**: SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC | each other as UTF-8 (Korean CP949 files read correctly) |
| **Archives**: ZIP, 7Z, RAR, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, ISO, CAB, DMG, ALZ, EGG, … | extract, ZIP, 7Z, TAR, TAR.GZ; any files → one ZIP |
| **CAD**: DWG, DXF | PDF, SVG, PNG, DWG ↔ DXF |
| **Fonts**: TTF, OTF, WOFF, WOFF2, EOT | each other |
| **Data**: JSON, JSON Lines, YAML, TOML, XML · vCard | each other, CSV and XLSX (tables); vCard ↔ CSV |
| **3D models**: GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY | each other, PNG preview |

Unmarked formats work out of the box, without Office, Hancom Office or LibreOffice.
Marked ones need an optional engine, downloaded only when needed: ¹ Ghostscript, ² LibreOffice, ³ Pandoc, ⁴ calibre,
⁵ FFmpeg ([details](docs/ENGINES.md)).

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
git clone https://github.com/KnifeLemon/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # optional engines
dotnet run --project src/Filee.App
dotnet test
```

[Architecture](docs/ARCHITECTURE.md) · [Engines](docs/ENGINES.md) · [Adding a converter](docs/ADDING-A-CONVERTER.md) ·
[Adding a language](docs/ADDING-A-LANGUAGE.md) · [Contributing](CONTRIBUTING.md)

Pull requests are welcome. If Filee saves you some clicks, a ⭐ helps other people find it.

## Code signing policy

Filee is developed and published by **Myeonggu Kim** ([KnifeLemon](https://github.com/KnifeLemon)).

Windows releases will be signed with a [Certum](https://www.certum.eu/) open source code signing certificate issued
to Myeonggu Kim. The certificate is being issued; until then, releases are not code-signed and Windows SmartScreen
may warn when you run the installer ("More info" → "Run anyway"). macOS packages are ad-hoc signed and not notarized.

Only the installer and files built by this repository's GitHub Actions release workflow are signed, and every release
is approved by hand. Bundled third-party programs (rhwp, 7-Zip) keep their own publishers' files.

### Privacy policy

Filee converts files on your own computer and never uploads them. It collects no usage data and sends no telemetry.
It connects to the internet only:

- to ask GitHub (`api.github.com`) whether a newer release exists, shortly after start and then every few hours.
  Turn this off in Settings → About ("Check for updates automatically").
- to download the conversion engines you choose to install, from the official locations pinned in
  [`engines.json`](src/Filee.Engines/Infrastructure/engines.json).
- when you click a link, which opens your web browser.

## Sponsor

Filee is free and stays free. If you'd like to support it, pick whichever way suits you
([Fairy](https://fairy.hada.io/@filee) takes Korean cards and easy-pay).

**Your name or logo here.** Monthly backers are listed here by name, and sponsors get their logo here and on
Filee's About page. One-time supporters can ask to be listed too.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## License

MIT, © 2026 Myeonggu Kim (KnifeLemon) and Filee contributors. Bundled third-party components keep their own licenses, listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#KnifeLemon/Filee&Date"><img src="https://api.star-history.com/svg?repos=KnifeLemon/Filee&type=Date" alt="Star history" width="600"></a>
</p>
