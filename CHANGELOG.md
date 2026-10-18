# Changelog

What changed in each version of Filee, for the people using it. The release workflow puts the section of the
version it publishes under "What's Changed" in the GitHub release notes, above the list of pull requests.

Add a `## <version>` section before tagging a release. Write what someone using Filee notices: what's new, where to
find it, what it does and doesn't do.

Start the section with an `<!-- update -->` block (hidden on GitHub). The app's update notice shows its level and its
one-line summaries in the user's language (`ko`, `en`, `zh`; a line per change, a language may repeat):

- `required`: wrong or lost results, security, Filee not starting. The card shows every time Filee starts.
- `recommended`: bug fixes. The card shows once per version.
- `optional`: new features only. No card, the sidebar button only.

## 1.11.0

<!-- update
level: recommended
ko: 지원 형식이 244가지로 늘었어요: 자막, JSON·YAML 같은 데이터, 3D 모델 등
ko: 그림을 SVG로 바꾸는 "벡터화"와 회전·반전 프리셋이 생겼어요
ko: 도넛 한 칸에 여러 프리셋을 묶어 두고 펼쳐서 고를 수 있어요
ko: HEVC·AV1 동영상, 무손실 오디오, 동영상 자막 추출을 지원해요
ko: 바탕화면 아이콘이 가끔 클릭되지 않던 문제를 고쳤어요
ko: HWP를 PDF로 바꿀 때 표 안의 표가 아래로 밀리던 문제를 고쳤어요
en: 244 formats now, including subtitles, data files (JSON, YAML…) and 3D models
en: "Trace to SVG" turns pictures into vectors, and new presets rotate and flip images
en: A donut slice can hold a group of presets that fans out
en: HEVC and AV1 video, lossless audio formats and subtitle extraction
en: Fixed desktop icons that sometimes stopped reacting to clicks
en: Fixed tables inside a table cell drawn too low in HWP → PDF
zh: 支持的格式增至 244 种，新增字幕、数据文件（JSON、YAML 等）和 3D 模型
zh: 新增"描摹为 SVG"可将图片转为矢量图，并新增旋转和翻转预设
zh: 圆环工具栏的一格可以放一组预设，展开后选择
zh: 支持 HEVC 和 AV1 视频、无损音频格式以及从视频提取字幕
zh: 修复了桌面图标有时点击无反应的问题
zh: 修复了 HWP 转 PDF 时表格内的表格位置偏下的问题
-->

### New

- **Subtitles.** A new category with its own donut: SRT, ASS, WebVTT, SAMI (SMI), LRC, TTML and MicroDVD convert into
  each other without any engine, and SSA and SCC are read. Korean SMI and SRT files saved in CP949 are detected and
  written as UTF-8. "Extract subtitles" on the video donut takes the first text subtitle track of an MKV, MP4, MOV or
  WebM out as SRT (needs FFmpeg).
- **Data files.** A new category for JSON, JSON Lines, YAML, TOML and XML, converting into each other and, when the data
  is a table, to and from CSV and Excel. Outlook `.msg` e-mails convert to EML, HTML, text or a ZIP of their
  attachments, and vCard contacts (also CP949 ones from old phones) to and from CSV.
- **3D models.** A new category for GLB, glTF, STL, OBJ, 3MF and PLY, converting into each other with colours,
  textures and materials where the format keeps them, and to a shaded PNG or JPG picture. Nothing to install.
- **Trace to SVG.** Two new image presets turn a picture into vector shapes: "Trace to SVG" in up to 64 colours and
  "Line art to SVG" in black for logos, signatures and scans. SVG drawings also convert to DXF and DWG for CAD.
- **Rotate and flip** (issue #71, by @SeoJCalive). Images can be turned left, right or 180° and mirrored, after the
  photo's own orientation, as a preset option and as ready-made presets.
- **Donut groups** (issue #74, by @SeoJCalive). A donut slice can hold up to six presets that fan out in an outer ring
  when you rest on it, drag outward or click it; the keyboard works too. Create one with "New group" in Settings →
  Toolbar. The ready-made "Rotate" and "Flip" groups are on the Presets page.
- **Video and audio.** Video presets can encode HEVC (H.265) or AV1 (MP4 HEVC is on the video donut), keeping 10-bit
  and HDR video. New audio targets WavPack, TTA, W64, E-AC3 and ALAC (in M4A), and APE, Musepack, TAK, DSD (DSF/DFF),
  DTS and Speex are read.
- **More formats.**
  - Images: APNG (animated PNG, without FFmpeg), QOI, DDS, PCX, CUR, XPM, XBM, WBMP, PAM, PFM, SGI, Sun Raster, MIFF
    and MNG; DICOM medical images, OpenEXR, Radiance HDR and Photo CD are read; Fujifilm `.hif` is read as HEIC.
  - Documents and text (with Pandoc): Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter notebooks, Textile, man pages
    and OPML, and Markdown → PowerPoint slides.
  - With LibreOffice: ClarisWorks, MacWrite, T602, Pocket Word and flat OpenDocument files are read, OTT, OTS, OTP,
    FODT and XLT written, SVG → EMF/WMF and one PDF page → SVG.
  - E-books: KEPUB for Kobo readers, built in.
- **Sponsor.** "Sponsor" in the sidebar and the About page opens the ways to support Filee (GitHub Sponsors, Patreon,
  Ko-fi, PayPal, Fairy), and sponsors are thanked on the About page.

### Changed

- **Engines page.** LibreOffice's switches moved into a Settings window on its card, and engines are put in order by
  dragging their cards.
- **Shorter texts.** Long hints in the app are shorter and break after each sentence.

### Fixed

- **Desktop icons that stopped reacting to clicks.** After a conversion through the donut, a Ctrl+drag that brought no
  files (selecting icons on the desktop, dragging a folder) could leave Filee's invisible drag window over the desktop,
  catching clicks until Filee quit. A donut opened from Explorer's menu on Windows 10 also closes with Esc and clicks
  elsewhere again.
- **Tables inside a table cell in HWP → PDF** (issue #49). A table placed in a table cell was drawn too low, over the
  paragraph below it, when the file was HWP (HWPX was fine).
- **Korean e-mails after an Outlook message.** Converting an Outlook `.msg` first left later Korean (EUC-KR) e-mails of
  the same session garbled.

## 1.10.4

<!-- update
level: recommended
ko: 서명·도장·로고의 단색 배경을 투명하게 만드는 "배경 제거" 프리셋이 생겼어요
ko: 자동 변환 폴더에서 하위 폴더의 파일은 결과도 같은 하위 폴더에 저장돼요
ko: Filee가 켜지는 중에 연 파일이나 업데이트 요청이 사라지던 문제를 고쳤어요
ko: 보안 업데이트: PDF 처리에 쓰는 .NET 암호화 라이브러리를 최신 버전으로 바꿨어요
en: A "Remove background" preset makes the plain backdrop of signatures, stamps and logos transparent
en: Watch folders keep the subfolders of their files in the output folder
en: Files opened, or an update started, while Filee is still starting are no longer lost
en: Security update: the .NET cryptography library used for PDFs is updated
zh: 新增"去除背景"预设，可将签名、印章、标志的纯色背景变为透明
zh: 自动转换文件夹会在输出中保留文件所在的子文件夹
zh: 修复了 Filee 启动过程中打开的文件或更新请求丢失的问题
zh: 安全更新：处理 PDF 所用的 .NET 加密库已更新
-->

### New

- **Remove background** (issue #69, by @SeoJCalive). A new image option and built-in preset make a plain backdrop
  transparent: the paper behind a signature or stamp, the backdrop of a logo. Offline, with smooth edges and the ink
  colour kept, also on phone photos with uneven lighting. "Keep enclosed areas" keeps the inside of a logo. Formats
  without transparency (JPG) get the background colour instead. The preset is on the Presets page; add it to a donut
  in Settings → Toolbar.

### Changed

- **Watch folders keep subfolders** (issue #72, by @SeoJCalive). With "Include subfolders", files from
  `Inbox\2025\scans` now go to `converted\2025\scans` instead of all into `converted` (where files of the same name
  became `a (2).pdf`), the way originals are already moved. `filee watch` does the same.

### Fixed

- **Starting Filee again while it is still starting.** Files opened from the right-click menu or Send To, or the
  installer's request to close Filee for an update, were lost when Filee took more than a few seconds to start (a
  slow or busy computer). They now wait until Filee is ready.
- **Security update.** The .NET cryptography library PDFsharp uses (System.Security.Cryptography.Pkcs) is updated to
  10.0.13, which fixes two denial-of-service advisories (CVE-2026-84008, CVE-2026-70162).

## 1.10.3

<!-- update
level: recommended
ko: 엑셀에서 정한 페이지 나누기가 PDF에 반영돼요
ko: 중국어·일본어 등 여러 언어 문서를 PDF로 바꿀 때 그 언어의 글꼴을 써요
ko: 한글 문서의 보기 상자와 수식이 PDF에서 제대로 나와요
ko: Word·엑셀 → PDF를 LibreOffice로 변환하도록 고를 수 있어요
ko: 업데이트 알림에 중요도와 바뀐 내용이 나와요
en: Excel page breaks carry over to PDF
en: Chinese, Japanese and other languages get a font of their own in PDFs
en: Boxes and equations of 한글 documents come out right in PDFs
en: Word and Excel → PDF can go through LibreOffice
en: Update notices say how much an update matters and what it changes
zh: Excel 中设置的分页符会保留到 PDF 中
zh: 中文、日文等文档转为 PDF 时使用对应语言的字体
zh: 韩文 Hangul 文档中的框和公式在 PDF 中正确显示
zh: 可以选择用 LibreOffice 将 Word、Excel 转为 PDF
zh: 更新通知会显示重要程度和更新内容
-->

### New

- **Update notices say how much an update matters.** The card names the update required, recommended or optional and
  lists what it changes in one line each, in the app's language (for someone a few versions behind, every version in
  between). Optional updates no longer bring up the card, only the sidebar button; required ones show it every time
  Filee starts.
- **LibreOffice for Word and Excel → PDF** (issue #66). Settings → Engines → "When LibreOffice is installed" has two
  new switches, off by default: "Word documents → PDF" (DOCX, DOC, ODT, RTF, laid out as in Word, with Chinese and
  Japanese punctuation rules) and "Spreadsheets → PDF" (XLSX, XLS, ODS, CSV, with the workbook's print area and page
  setup).

### Fixed

- **Excel page breaks** (issue #67). XLSX → PDF starts a new page where a page break was set in Excel (Page Layout →
  Breaks), with frozen header rows repeated at the top. This works without LibreOffice.
- **Fonts for every language in PDFs** (issue #64). Markdown, text, HTML and other documents Filee turns into PDF used
  Korean fonts, so Chinese text mixed two fonts character by character. Chinese (Simplified or Traditional) and
  Japanese text now gets a Chinese or Japanese font already on the computer (e.g. Microsoft YaHei), and text in other
  scripts (Russian, Greek, Thai, Arabic, …) a font that has all its characters. Filee does not download or ship fonts.
- **한글 boxes and equations in PDFs** (issue #65). In HWP / HWPX → PDF:
  - Boxes inside a text box (the "보기" box of an exam paper) came out narrow, with their content clipped and pictures
    shrunk. They now have the size 한글 shows.
  - Equations written in 한글 2022 showed commands as text ("fCIRCf", "ASMALLINTERB", "lek", "rootx") and != or +-
    instead of ≠ and ±. They now show ∘, ∩, ⊂, ≤, √, ≠ and ±.
  - When the computer has no font with the equation symbols (∪, ∈, ⊂, …), Cambria Math, which comes with Windows,
    draws the equations.

## 1.10.2

### Fixed

- **One progress card per conversion** (issue #60). When a long conversion moved from the ring around the donut to
  the progress card in the bottom-right corner, the card appeared twice.
- **Blank PDF pages with variable fonts** (issue #61). A Word or 한글 document whose font is installed as a variable
  OpenType font with CFF2 outlines, such as Source Han Serif VF (思源宋体 VF), became a PDF without text. Such a font is
  now replaced by a similar font already on the computer (e.g. SimSun for Simplified Chinese serif text). Filee does
  not download or ship fonts.

## 1.10.1

### New

- **Long images: trim and split** (issue #50). Two options under "Stack the pages into one tall image" in the PDF
  part of a preset:
  - **Cut the blank space above and below each page.** The pages follow each other without wide white gaps; about
    4 mm stays around the content.
  - **Split into several images when the stack is too tall for the format.** JPG holds up to 65,500 px and WebP
    16,383 px. Instead of shrinking the long image to fit, Filee writes several (name_1, name_2, …), split between
    pages, at full size.
- **Resize to a width.** "Set the width" next to "Scale by percent" and "Limit the long edge" makes images, and long
  images, a given number of pixels wide (e.g. 800 or 1200). Smaller images are left as they are.

### Fixed

- Images taller or wider than a format holds (a long PNG → JPG, over 65,500 px) failed with "could not write the
  file". They are now scaled down to fit (issue #50).

## 1.10.0

### New

- **PDF → one long image** (issue #50). A multi-page PDF becomes a single tall image with the pages one under
  another, e.g. to post a whole document at once. Use **Long image** on the PDF donut (PNG), or tick "Stack the pages
  into one tall image" in any image preset. Pages of different widths are centred; a result taller than the format
  stores (JPG 65,500 px, WebP 16,383 px) or very large is scaled down to fit. TIFF and GIF keep one page per frame.
- **Photos → video in full colour.** HEIC, JPG, PNG, AVIF and other pictures → MP4, MOV or WEBM (with FFmpeg) used
  to pass through a GIF, which leaves 256 colours and bands skies and skin. They now go straight from a full-size PNG:
  a 5-second still video at the photo's own size, with its colours (Display P3 photos stay P3) and transparent areas
  in black. Only photos over about 36 megapixels (48 MP phone cameras) are scaled down, to the largest picture H.264
  and VP9 players decode. Animated GIF/WebP and PDF pages still become a moving video or a slideshow.
- **MOV ↔ MP4 without quality loss.** When the new format can hold the video and sound as they are, like an iPhone
  video (HEVC or H.264 with AAC) going from MOV to MP4, Filee now only rewraps them instead of re-encoding: nothing
  is lost, HDR and 10-bit colour stay, and it takes seconds. The same goes for MKV, WebM and TS. Choosing a smaller
  size (e.g. 720p), another audio bitrate or quality **Small** still re-encodes.
- **Save images as HEIC** (with FFmpeg). Any image Filee reads → HEIC, the format iPhones take photos in: about a
  quarter of a JPG's size at the same quality, stored in 512-pixel tiles like a phone photo, upright, with the colour
  profile and Exif kept. The **HEIC** preset is on the Presets page (quality 80); add it to a donut to use it.
  Transparent areas are filled with the preset's background colour.

## 1.9.0

### New

- **Engine updates.** When a Filee update brings a newer version of an engine you downloaded (FFmpeg, LibreOffice,
  Pandoc, Ghostscript, calibre), a card in the bottom-right corner says so once per version, and Settings → Engines
  shows "New version" with an **Update** button. Engines you never installed are left alone, and the card follows
  "Check for updates automatically".
- **When LibreOffice is installed** (Settings → Engines): presentations (PPTX, PPT, ODP) → PDF now go through
  LibreOffice, so slide backgrounds and shape positions come out as in PowerPoint; turn it off to use the built-in
  route. HWP·HWPX → PDF can go through LibreOffice too (off by default), for a document rhwp lays out wrong.

### Fixes

- **Update installed an older release.** A release found by an earlier check stayed the one Update downloaded, even
  after a newer one came out. Update now asks for the latest release first.
- **PPTX → PDF, HWPX, images: picture backgrounds were dropped** (issue #49). A slide whose background is a picture
  (Format Background → Picture) came out white, hiding white titles; the picture now covers the page.
- **HWP·HWPX → PDF: rhwp 0.8.7.** Among other layout fixes, the header table of two-column exam papers sits at the top
  of the page again instead of over the first questions (issue #51; another overlap in that file is reported to rhwp
  as edwardkim/rhwp#7826).

## 1.8.1

### New

- **Sponsor Filee.** Filee stays free; if it helps you, you can now support its development. **Sponsor** sits at the
  bottom of the navigation next to **GitHub**, above the version, and on the About page next to Star and Report. It
  opens Fairy when Filee is in Korean (Korean cards and easy-pay) and GitHub Sponsors in other languages.

## 1.8.0

### New

- **Failed files at a glance, and a retry.** When files of a conversion fail, Recent conversions on the Home page
  lists them with the reason (click "2 failed" to open the list; names and reasons can be selected and copied).
  **Retry** converts the files that failed again with the same settings and save location, even after the preset
  was changed or deleted. Files that are no longer there are left out. The progress card that pops up after a
  conversion has the same **Retry** button. A retried file from a watch folder is handled like the folder handles
  its files: when the folder moves originals to "originals", the original moves once it converts.
- **Watch folders: choose files and name them** (Settings → Watch folders → *More options*, all optional; empty
  fields change nothing):
  - *Files to convert*: patterns entered as tags. Typing `heic` adds `*.heic`, other text picks names that contain
    it, and `*` stands for any text, `?` for one character. Extensions are suggested while typing, and the card says
    how many files in the folder match. Other files in the folder are left alone.
  - *Names of converted files*: **Edit name rules** opens a window with the base name (typed, or built with buttons
    for the original name, date, time, number and preset name; empty keeps the preset's) and changes applied to it
    from top to bottom. Each change says what it does — replace text, remove text, add at the start or end, spaces
    to `_`, remove a copy number like "(2)", text in brackets, a leading number or symbols, all lowercase or
    uppercase — and asks only for the text it needs; a regular expression is there as the last, advanced kind.
    **Common replacements** on the left lists ready-made changes with an example of each; double-click one or press +
    to add it. The preview shows the name each file in the folder will get, and any name you type. Nothing changes
    until you save. The card shows the name in words ("[Original name]_scan") and the changes.
  - A **preview** on every watch folder shows a converted file as it will be saved (`IMG_0412.jpg →
    converted\Photo_0412.pdf`, using a file already in the folder when there is one), what happens when the name is
    taken and what happens to the original.
  - **Recent results** on every watch folder: how many files it converted and which failed, with the reason, and
    **Retry** right there. A file that converts on a retry or a later copy no longer counts as failed.

### Documentation

- docs/ENGINES.md said Filee never uses engines installed on the computer. It does, as the last choice after a copy
  you picked and Filee's own: that is how FFmpeg and Ghostscript are found on macOS. The order is now described.

## 1.7.2

### Fixes

- When the computer was busy (a screen recorder running, a slow graphics card), slices of the donut could stay lit
  after the pointer left them, pile up in the accent colour and fly apart as the donut closed. A late frame moved the
  highlight further than it should, and every following frame made it worse. The animations now stay steady however
  late the frames come. Turning on "Reduce animations" was the workaround until now.

## 1.7.1

### Fixes

- Moving the pointer from one slice of the donut to another no longer makes the slice it left flash in the accent
  colour as its highlight fades.
- After Filee sat unused for a long time, the donut could take seconds to appear or open without its animation. A
  frame the animation waited for could get lost; the donut now asks again and, if frames still don't come, shows
  itself fully open.
- With "Reduce animations" following Windows, a moment when Windows reported animations off (a remote session,
  waking from sleep) could switch them off in Filee until the setting was changed. Filee reads the Windows setting
  again whenever the donut or the main window opens.

## 1.7.0

### macOS and Linux (preview)

- Filee runs on macOS 14 or later (Apple silicon and Intel) and on Linux (x64 and ARM64). Download the package for
  your system from this release and run `install.sh` in it. These are previews: the macOS app isn't notarized yet,
  so macOS asks you to allow it once under System Settings → Privacy & Security.
- macOS: hold Option and drag files to open the donut. Filee asks for the Accessibility permission the gesture needs
  and starts it as soon as you switch Filee on. "Convert with Filee" is in Finder's right-click menu.
- Linux: the drag gesture works in an X11 session (on Ubuntu, "Ubuntu on Xorg" on the login screen). On Wayland, use
  the drop zone and the right-click action in Nautilus, Nemo, Thunar or Dolphin.
- FFmpeg and Ghostscript come from Homebrew on macOS and from your distribution's packages on Linux. Filee finds them
  by itself.

### New

- Search boxes above the profiles and the available presets (Donut toolbar) and above the preset list. A profile is
  also found by an extension it handles: type "png" to find the image donut.
- "Never trigger in these apps" (Shortcuts) holds the apps as tags. Type a name, with the running apps suggested, or
  pick one from the apps that have a window open.
- The update settings are on the About page now: the automatic check, Check now and the update button.
- The first-run engine choice starts with nothing ticked, so only what you pick is downloaded.

### Fixes

- On macOS and Linux, watch folders wait for a copy that pauses to finish instead of converting it half-written.

## 1.6.0

### New in the preset editor for audio

- Opus and the other lossy audio formats offer 32, 48 and 64 kbit/s as well, for speech and small files (#37).
- **Channels**: same as the source, mono or stereo, for every audio format except AMR (which is always mono).
- WAV gets a **sample rate** (8, 16, 22.05, 44.1 or 48 kHz, or the same as the source) and a **bit depth** (8, 16,
  24 or 32-bit, or 32-bit float). 16-bit stays the default.

### Fixes

- EPS and PS → PNG keep a transparent background where the drawing leaves the page empty, instead of white (#39).
  Ghostscript renders the PNG directly; JPG and other formats without transparency still get a white background.
- PDF → DOCX keeps rows laid out with tab stops, such as a numbered schedule with dates on the right ("1.  RFP
  Published   08/14/2026"): each row stays one paragraph with tabs at the PDF's positions, instead of the numbers,
  labels and dates coming out as separate paragraphs. Forms with a label and a value on each row come out the same
  way (#38).
- PDF → DOCX keeps the line breaks of address blocks and other short lines: "West Linn, Oregon 97068" no longer
  moves up onto the street line (#38). PDF → TXT has the tabs and line breaks too.

## 1.5.2

### Fixes

- Converting audio to Opus or Ogg Vorbis now keeps the album art. FFmpeg can't put a picture into those files, so
  Filee writes it the way they store pictures (a METADATA_BLOCK_PICTURE tag); title, artist and the other tags stay
  too. Matroska audio (MKA) gets the album art as a cover attachment.

## 1.5.1

### Fixes

- Sometimes, after Ctrl+drag opened the donut, the format under the dragged files didn't light up and dropping
  didn't pick it, until a while later. The donut's animation could wait forever for a frame that never came; it now
  starts again after a quarter of a second.
- Each drag over the donut writes one line to the log (`%APPDATA%\Filee\logs`), so if a donut ever ignores a drag
  again, the log shows whether the drag reached it.

## 1.5.0

### FFmpeg, calibre and the others you already have are used by themselves

- Filee now finds FFmpeg, calibre, Pandoc, Ghostscript and LibreOffice that are already on your PC, without asking
  you to download them: on the PATH (Scoop's shims included) or where their installers put them (LibreOffice,
  calibre and Ghostscript under Program Files). Settings → Engines shows "Found on this PC" and the folder.
- If that copy is older than the version Filee is tested with, Settings → Engines recommends an update. It keeps
  working; most conversions don't need the newest version.
- Order: a copy you chose with **Use my copy…**, then Filee's own download (if you installed it), then one found on
  the PC. **Install** is still there if you'd rather use Filee's tested copy. **Check again** searches again after
  you install something.

### One multi-page TIFF

- New option for TIFF presets: **Put every file into one multi-page TIFF**. Every image, every PDF page and every
  page of a TIFF becomes a page of one TIFF, in order, named after the first file. One PDF is enough: a 10-page PDF
  becomes one 10-page TIFF.
- Pages use the preset's compression and are marked as pages of a multi-page document, so older viewers (fax
  software, Microsoft Office Document Imaging) show them page by page. Transparent areas get the preset's background.

### Arranging the donut

- On the Donut toolbar page, dragging a preset (or a slice) over the donut opens a gap where it will land, with a
  see-through slice showing its name. The other slices slide aside, and a moved slice leaves its old place.

## 1.4.0

### Updates install themselves

- In an installed copy, **Update** (bottom of the menu, tray menu, the update notice, Settings → General) now
  downloads the new installer inside Filee, checks it against the release's SHA-256 file and runs it without the setup
  wizard. Setup closes Filee, updates it, keeps your settings and engines and starts Filee again. Windows asks for
  administrator rights once.
- The sidebar, tray menu, Settings → General and the notice show the download progress.
- If the download or the installer fails (no connection, a file that doesn't match its checksum, an installer
  that won't start), Filee tells you, and after OK it opens filee.sh so you can download the new version there.
- The portable copy still opens the release page: download the new ZIP and unpack it over the old folder.
- Updating **to** 1.4.0 from an older version still goes the old way. From 1.4.0 on, it's one click.

### Default save location

- Settings → General → **Default save location**: next to the original (as before), in a subfolder such as
  `converted`, or in one fixed folder.
- Every preset follows it unless you give the preset its own location (Presets → Output → Save to).
- Built-in presets, and your presets that saved next to the original, follow the default from now on. Since the
  default starts as "next to the original", nothing changes until you pick another one.

### Keep the original file dates for every conversion

- New switch in Settings → General: converted files get the created and modified dates of the original, with every
  preset (photos, videos, PDFs, archive conversions).
- Not when several files become one (merged PDF, several files packed into one ZIP): that file has no single original.
- The per-preset option from 1.3.1 stays, for keeping dates with some presets only.
- The `filee` command follows the default save location and this switch too.

### Use the engines you already have

- Settings → Engines → **Use my copy…** next to FFmpeg, calibre, Pandoc, Ghostscript and LibreOffice. Pick the
  program (`ffmpeg.exe`, `ebook-convert.exe`, `pandoc.exe`, `gswin64c.exe` or `soffice.exe`) and Filee uses it
  instead of downloading its own. **Stop using it** goes back.
- FFmpeg needs `ffprobe.exe` in the same folder (most builds have both). Picking a build's main folder finds its
  `bin` folder too.
- The `filee` command uses the same copies.

## 1.3.1

### Keep the original file dates

- Presets → Output → **Keep the original file dates**: converted files get the created and modified dates of the
  original, so converted photos still sort by when they were taken. On the command line: `--keep-dates`.

### Conversions that were there but hard to find

- New **HWP** preset (saved through HWPX). It is on the PDF, documents, HWP, spreadsheet, presentation, text and
  e-book donuts, so Word, PDF, Excel, PowerPoint, Markdown and EPUB files can be saved as `.hwp` directly.
- On the donuts now: PDF → Markdown and EPUB, Word → PNG, HWP → JPG and EPUB. These worked before through another
  format, but weren't on any donut.
- Existing donuts get the new entries once, unless a donut is full or already has them.

### Texts

- Plainer wording in English, Korean and Chinese.
- Fixed: the Korean shortcut button said "recording", and removing a watch folder read like deleting it in Chinese.

## 1.3.0

### The `filee` command line

- Setup can add the `filee` command to PATH. `filee convert` converts files, wildcards (`*.heic`) and folders with
  `--to <format>` or one of your presets; `filee watch` converts files that land in a folder; `filee formats` and
  `filee presets` list what's available.
- `--json` prints the result for scripts. Exit codes: 0 all converted, 1 some failed, 2 wrong arguments or a missing
  file, 3 nothing to convert. Guide: https://filee.sh/cli/

### Watch folders

- A new **Watch folders** page: files that land in a folder you choose are converted while Filee runs, once
  downloads and copies are complete. Keep the originals, or move them to an `originals` folder so the folder works
  like an inbox.

### History

- Settings → General: turn off the list of recent conversions, or clear it.

### Fixes

- Ctrl+drag stopped working after the Photos app had been opened, until Filee was restarted. The donut now stays on
  top of other windows.
