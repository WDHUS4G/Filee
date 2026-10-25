# Architecture

```
            ┌──────────────────────────── Filee.App (Avalonia) ─────────────────────────────┐
 global     │ TriggerService ──► GestureDetector ──► RadialController ──► RadialWindow/DonutMenu │
 mouse/keys │   (SharpHook)        (pure state         (drag / click       (transparent,          │
 ─────────► │                       machine)            mode)               topmost window)       │
            │                                              │ drop / click                         │
 Explorer   │ SingleInstance (pipe) ── --convert files ──►│                                      │
 context    │ Home drop zone ─────────────────────────────┘                                      │
 menu       │                                              ▼                                      │
            │                                    ConversionService ──► ToastWindow (progress)     │
            │ MainWindow (settings pages) ◄──► UserDataStore (JSON in %APPDATA%\Filee)            │
            └──────────────────────────────────────────────┬──────────────────────────────────────┘
                                                           ▼
            ┌──────────────────────────── Filee.Core ───────────────────────────────────────────┐
            │ JobQueue ──► RoutePlanner (Dijkstra over converter edges) ──► IConverter steps    │
            │ FormatRegistry · Preset · OutputPathResolver · ToolbarProfile · ProfileSelector   │
            └──────────────────────────────────────────────┬──────────────────────────────────────┘
                                                           ▼
            ┌──────────────────────────── Filee.Engines ────────────────────────────────────────┐
            │ Built in: Magick (images, RAW) · PdfSharp · Pdfium · PdfPig · Svg.Skia · ICNS      │
            │ HwpxWriter + DocxWriter over DocumentReaders (DOCX, XLSX, XLS, ODS, PPTX,          │
            │   PDF, HTML, EPUB, MOBI, FB2, HWPX, Markdown) · Spreadsheets · OOXML variants      │
            │   · E-mail · Data · E-books · CAD · Fonts · Subtitles · 3D models                  │
            │ Bundled: rhwp (HWP→PDF, HWP↔HWPX) · 7-Zip (archives) · Unhwp · Markdig             │
            │ Optional: FFmpeg · LibreOffice + H2Orestart · calibre · Ghostscript · Pandoc       │
            └────────────────────────────────────────────────────────────────────────────────────┘
```

## Flow of a conversion

1. **Trigger** – `TriggerService` receives global input from SharpHook and feeds `GestureDetector`.
   A drag gesture (modifier + drag past the threshold) opens the donut *before* files are known.
2. **Donut** – `RadialWindow` is transparent, topmost and not activated, so Explorer's drag continues.
   The first `DragEnter` delivers the file list; `RadialViewModel` picks a `ToolbarProfile` by extension
   (`ProfileSelector`: the first normal profile that has every file's extension; for a mix of types the first
   mixed-selection profile whose checked extensions cover the files, else the one with nothing checked) and asks
   `PresetAvailability` which presets can run (disabled slices get a reason).
3. **Drop** – the slice under the cursor is a preset. `ConversionService.Start` enqueues a `ConversionJob`.
   The drop is always reported as *Copy* so Explorer never deletes the source files.
4. **Plan** – for every file, `RoutePlanner` finds the cheapest chain of converter edges from the source format to
   the target (at most 3 hops). Engine priority (Settings → Engines) breaks ties. Some edges are limited: tracing
   only starts from raster images (`ImageSourcesOnly`), and PDF → PPTX picture slides are only ever a route of their
   own (`DirectOnly`), never a step of a longer one.
5. **Run** – `JobQueue` runs steps with per-engine concurrency limits. Intermediate files live in a temp folder;
   final outputs are named by `OutputPathResolver` (pattern, folder, conflict policy; the source is never overwritten).
   Each name stays reserved while a conversion holds it. A conversion that fails or is cancelled deletes what it began
   writing, but only at a path that was its alone: nothing was there before and no other conversion was handed the
   same name ("Overwrite"). It frees its names and removes an output folder it made if that is left empty.
   A PDF target's finishing (watermark, page numbers, password; `IPdfFinisher`) runs after the last step, on every
   PDF a file became, and once on a merged PDF.
   *Replace the original* (`OutputRule.ReplaceOriginal`, off by default) comes after that, for a finished result of
   the source's own format and extension, or another spelling of it (`.jpeg` / `.jpg`; TXT also covers scripts and
   logs, PPT also `.pps` and `.pot`, so those need the same extension). The result is first
   moved next to the source under a temporary name, then the source goes to the Recycle Bin / Trash
   (`IPlatformServices.MoveToTrash`, in the app and the command line), then the result takes its name. Windows only
   ever recycles (IFileOperation with a sink that refuses a delete it can't recycle). When the source can't go there
   (no Recycle Bin on the drive, a file open elsewhere, a read-only file), both stay and the file's notice says why,
   with a translated reason (`trash.*`). A result "Only if smaller" copied from the source leaves the source alone.
   Watch folders and files from a browser never replace.
   "Merge into one PDF" converts each file to PDF and merges with PDFsharp.
   "Compress into one archive" packs the dropped files, unconverted, with the archive engine (`IFileCombiner`).
   "Merge videos", "Merge audio" and "Video from images" join the files, in the order picked in the merge order
   window, into one file with FFmpeg (`IMediaJoiner`); pictures become PNG through the image engines first. Like the
   other one-file jobs, the output follows "subfolder only for several files", and a join that fails or is cancelled
   is undone by the job's allocator, which deletes only paths that were the job's own.
6. **Report** – progress and results appear in the toast window and in Home → Recent conversions.
   Renderers check only the original source for referenced fonts. `ConversionStep.MissingFonts` reports any missing
   families to `FileResult.MissingFonts`; the toast card can offer one trusted source, while the CLI and watch jobs
   report the names without pausing conversion or installing anything.

## Files dragged from a web browser

A picture, a link to a file or a download dragged out of a browser opens the donut like a file from the file manager.
A drag gesture over a browser (`WebBrowsers`, by process name) counts as over the file manager, so the default scope
covers it; click and key gestures don't, as they read the file manager's selection.
`DraggedFiles.Read` asks `BrowserDrags` (App, reads Avalonia's drag data) first; `BrowserDrag` (Core, pure rules)
decides what in the drag is a file:

| Source | What the drag carries | What Filee takes |
| --- | --- | --- |
| Chrome / Edge, Windows | the picture as a virtual file (`FileGroupDescriptorW` + `FileContents`, which Avalonia 12 gives as a storage item without a local path), a `.url` virtual file for links, `UniformResourceLocatorW`, `text/x-moz-url`, `HTML Format` | the picture's own bytes; for a link, its address |
| Firefox, Windows | the picture written to `%TEMP%` (`CF_HDROP`), `text/x-moz-url`, file promise formats | the temporary copy (a local file in the temp folder *and* a web address on the drag) |
| Chrome / Firefox, Linux | `text/x-moz-url`, `_NETSCAPE_URL`, `text/html` (Avalonia turns `text/uri-list` into local files only and drops http addresses) | the address |
| Safari / Chrome, macOS | file promises (not receivable through Avalonia), `public.url`, `public.html`, sometimes picture data | the address, else the picture data |

Rules, in order: files handed over with their bytes (never `.url` shortcuts or programs); a picture (an HTML fragment
that is one `<img>` and no text, or a Firefox file promise); a `DownloadURL` a page sets; links only when the address
ends in an extension Filee converts and isn't a web page (`.html`, `.php`, none); last, picture pixels for a `blob:`
picture. Plain text is never read. `data:` addresses are decoded.

Until the conversion runs, the files stand as **placeholders** (`WebDrop`): paths in `%TEMP%\Filee\web\<drop>\drop\<n>\`
named like the file will be, so the donut picks the profile by extension without touching the network. No HEAD request
is made: a picture without an extension stands as `.jpg` and a link without one isn't offered. `ConversionService.Start`
gives the job a `WebDropFetcher` (`ISourceFetcher`): `JobQueue` saves every source first (half of its progress), then
converts. Failures are per file (`error.web_*`), cancelling stops a download, and the job toast/ring shows both phases.
While files are saved, a badge pops up above the donut's progress ring and above the toast's progress bar with the
speed and the amounts ("2.4 MB/s · 12.3 / 48.0 MB", "· 2 of 3 files" for several): `FileResult.Transfer` holds each
file's byte count, `ConversionJob.Transfer()` sums them, and `DownloadBadge` (App) averages the speed over the last
second with the Engines page's `TransferRate` and units, rewritten four times a second and shown after 300 ms.

- **Where**: Settings → General → "Files dragged from a browser" (`AppSettings.BrowserDrops`): the user's Downloads
  folder (`IPlatformServices.DownloadsFolder`: the shell's known folder on Windows, `XDG_DOWNLOAD_DIR` on Linux,
  `~/Downloads` on macOS) or a chosen one. "Next to the source" means this folder for these files
  (`OutputRule.SourceFolder`, set per job, never saved), so subfolder rules apply there and fixed folders stay fixed.
- **Save only the converted file**: the original goes to the drop's work folder, which `Release` deletes when the job
  ends; leftovers from a crash are deleted at start-up after a day.
- **Downloads** (`WebDownloader`): http and https only, at most 5 redirects (never from https to http or off the web),
  2 GB cap whether or not the server says the size, 30 s to answer and 30 s without data at most. No cookies or
  credentials are sent: a file behind a sign-in comes back as the sign-in page, which is refused as a web page
  ("save it from the browser first"). A browser's own bytes (virtual files, Firefox's copy) don't have that limit.
- **Names and safety**: Content-Disposition, else the page's name, else the address; then the first bytes decide the
  extension of pictures and PDFs (`.jpg` that is WebP becomes `.webp`). Names are cut to a plain file name (no folders,
  no device names, at most 120 characters) and never replace an existing file (`name (2).ext`). Programs, installers and
  scripts are refused by extension, media type and first bytes; nothing downloaded is ever run, and every file goes
  through the same converters as any other.

## Donut control

`Controls/DonutMenu.cs` draws the ring itself (no child controls) for speed. `DonutGeometry.cs` holds the pure math
(slice angles, hit testing) and is unit tested. The same control is the live toolbar and, with `IsEditMode`, the
editor on the *Donut toolbar* settings page (drag to reorder, ✎ / right-click to edit).
Animations are small springs advanced per frame; `Motion.Enabled == false` (*Reduce animations*) snaps them.

Every `ToolbarProfile` has two faces: `PresetIds` (the front) and `BackPresetIds` (the back; the built-in donuts get
the less common formats and tools of their file type, see `BuiltInData.CreateProfiles`). The flip key
(`DonutSettings.FlipKey`, Tab unless recorded otherwise in Settings → Shortcuts; `EffectiveFlipKey` refuses keys the
donut or a keyboard shortcut uses) and the mouse wheel (`FlipWithWheel`, one flip per turn of the wheel: notches less
than `DonutMenu.WheelQuietMs` apart are one turn) flip the open donut (`DonutMenu.Flip.cs`): the ring turns about its
vertical axis in 200 ms, an open group's ring folding first, and halfway, edge-on, `FaceSwapping` has
`RadialViewModel` show the other face, so slices, hit tests and drops always belong to the face that is drawn.
*Reduce animations* swaps the faces at once. `RadialViewModel` builds both faces with the same `PresetAvailability`
check (the back right after the front is on screen); a face with no slice for the files (or none at all) isn't
flipped to, and only then does the hub leave out its "Tab ↻ back" hint (a mouse in place of the key when only the
wheel flips). In click mode the focused window gets the key (`DonutMenu.HandleKey`) and the donut the wheel. During a
drag the donut's window has no keyboard focus, so `RadialController` turns on `TriggerService.WatchFlipKey` while
the drag donut shows files and flips on `FlipKeyPressed` / `FlipWheelTurned`: the global hook only listens (it
can't hold an event back; Explorer and Finder ignore Tab during a drag) and reports one flip per key press.
The editor's Front / Back switch (`ToolbarPageViewModel.EditingBack`) edits either list with the same drag and drop.

A group is one slice that opens up to six non-group actions. Its members may be conversions or interactive tools
(for example the PDF Edit group contains Crop and the page editor); groups cannot contain another group. The group
editor offers both kinds, and `UserDataStore.EnsureConsistency` removes missing, duplicate, nested or excess members
without excluding tools.

## Crop overlay and PDF page editor

The image Crop tool (`tool-crop`) and PDF Crop (`tool-pdf-crop`) open `Views/CropOverlayWindow` instead of running a job:
the files one after another,
each cropped by hand and saved by `CropService` (the preset's output rule, the source's quality kept, animated or
multi-frame pictures refused, the EXIF thumbnail dropped), one history entry for the run. A PDF preview is rendered
with PDFium and the chosen rectangle changes every page's crop box in a copied document opened in modify mode, so
text, vectors, outlines, forms, page labels and document information remain PDF objects and stay attached.

- `CropOverlayViewModel` walks the queue (Enter saves and opens the next file, Skip, Esc closes), keeps a
  `CropQueueItem` per file for the strip of thumbnails (waiting, current, done, skipped, failed; a thumbnail is a
  small copy of the preview, made once) and, after the last file, shows the summary when something failed or was
  skipped. Every file starts free and whole, its portrait/landscape choice following the picture.
- `Controls/CropCanvas` is the stage: the picture inside `ImagePadding`, the part outside the frame frosted (a copy at
  most 40 px across, drawn large) under a veil of the page colour, the frame in the accent colour with round handles,
  a rule-of-thirds grid while dragging and a size chip that follows the frame. A frame being moved snaps onto the
  picture's vertical or horizontal centre line within `CropMath.SnapDistance` (8 px) and shows that line as a dashed
  guide until the drag ends. The preview is downscaled once by
  `CropService.LoadPreviewAsync`; nothing is decoded again while dragging. Programmatic changes (reset, a ratio, a
  new picture) ease in with tweens read against the clock, so a lost frame never leaves one half done; dragging and
  arrow-key nudges follow at once, and `PlayAnimations` (from `Motion.Enabled`) turns all of it off.
- `Controls/AspectDial` is a small donut of the ratios (free, original, 1:1, 5:4, 4:3, 3:2, 16:9): each slice a
  `RingSegment` button that answers only inside its wedge (`DonutGeometry.Wedge`, rounded by the softness), the hub
  swaps portrait and landscape. A with the pointer over the stage opens it around the pointer, as the donut opens,
  growing from its middle and kept inside the window; A without a pointer there (the keyboard) and its toolbar button
  open it over the button with a slice focused for the arrow keys. `AspectGlyph` is the dial in miniature on that
  button, and `CropSummaryRing` the end summary's donut (a slice per file).
- `Controls/CropMath` holds the arithmetic (ratios and their portrait forms, nudging by whole pixels, the frame in
  pixels, where the size chip goes, where the dial opens) and is unit tested.

`tool-pdf-pages` opens `Views/PdfPageEditorWindow`: it previews, reorders, rotates, deletes, adds and extracts pages.
Each source PDF's bytes and page sizes are cached once after the window has appeared; thumbnails and the large preview
render off the UI thread. A whole-document edit keeps the original catalogue and changes `/Rotate` in modify mode.
An actual reorder, deletion, addition or extraction builds a new PDF from selected page objects. A PDF that opens
without a password but requires an owner password for modify mode uses the same page-copy fallback as compression;
its outlines and form fields are not preserved by that fallback. Generated copies are writable even when the source
has the read-only attribute; the source's bytes and attributes are left alone.

## Clip window

The Cut a clip tool (`tool-clip`, first on the back of the video donut) opens `Views/ClipWindow` instead of running a
job. The window only chooses the clip; writing it is an ordinary conversion.

- `Core/Presets/ClipCutter` does every edit without the window: the parts of the clip (start, end, speed, volume,
  mute), gaps left by Delete, the blade, edge trims that take back or cut away, music on A2, fades, Remove silence and
  undo / redo (one step per drag). The result is a `ClipEdit`, unit tested on its own.
- `ClipWindowViewModel` holds the window's state: the overview of the whole video (`Controls/ClipTimeline`: 16
  frames, the loudness, the handles) and the sequence under it (`Controls/ClipSequence`: V1 pictures, A1 the video's
  sound, A2 music). Both are custom-drawn controls; their pictures and loudness are taken again only when the clip's
  ends rest.
- Space plays the clip as it will be written: `Engines/Media/ClipPreview` runs the same filter graph as the output,
  the pictures as raw BGRA frames over a pipe and the sound through `ffplay` (when it sits next to Filee's FFmpeg;
  without it the preview is silent). FFmpeg for the window runs below normal priority.
- Save calls `ClipTool.ConversionOf`, which carries the `ClipEdit` in `MediaOptions.Clip`, so the toast, cancel, the
  output rules (`{name}_clip` next to the video) and the history work as for any conversion. `FfmpegConverter`
  copies one plain stretch with `-ss/-to` and `-c copy` (*Fast, no re-encoding*), or encodes the exact cut.
- Anything else is one filter graph (`MediaEncoding.ClipGraphOf`): the video is read once, `split`/`trim` per piece,
  `setpts` and chained `atempo` for speed, `volume`, `color`/`anullsrc` for gaps, `concat`, the music as a second
  input (`atrim`, `volume`, `afade`, `adelay`, `amix`), then `fade`/`afade`. It is encoded with the preset's quality,
  size and codec.

## Spreadsheet print studio

The `tool-sheet-studio` tool opens an independent `Views/SpreadsheetPrintStudioWindow` for each dropped XLSX workbook
instead of starting a normal conversion job. `SpreadsheetPrintStudioViewModel` owns an ordered, non-destructive print plan: each visible
sheet starts as one region and the user can add more regions, change paper, orientation, scale, margins, page order,
hidden rows and manual breaks, then see the PDF that those settings will produce.

- `SpreadsheetPrintInspector` reads workbook metadata and streams the large worksheet data with `XmlReader`; opening
  a large workbook neither builds nor retains an `XDocument` for every sheet.
- `SpreadsheetPrintPackage` makes a disposable XLSX under `JobQueue.DefaultWorkRoot`. It keeps every worksheet and
  named range so cross-sheet formulas remain valid, hides the sheets not being rendered, and changes only the chosen
  sheet's print settings. A new print area is written only after the user explicitly changes or accepts the range.
- `SpreadsheetPrintService` runs LibreOffice once for each region. Preview and export use the same disposable-workbook
  path; export then joins the region PDFs in the chosen order with PDFsharp. The ordinary XLSX → PDF donut conversion
  can still differ because route planning may select Filee's built-in spreadsheet reader instead of LibreOffice.
- `PdfPreviewDocument` caches a PDF's bytes and page geometry. The studio asks PDFium/PDFtoImage for small thumbnails
  and only one large selected page, all decoded away from the UI thread; exporting never performs preview rendering.
  Cancellation closes LibreOffice and the temporary job folder is removed on success, error or window close.

## Settings and data

`UserDataStore` owns `settings.json`, `presets.json`, `profiles.json` and `history.json`
(System.Text.Json source generation, atomic writes, broken files are kept as `*.broken`).
Bump `AppSettings.CurrentSchemaVersion` and add a step to `SettingsMigrations` when the format changes.

## Localization and theming

- `LocalizationService` loads `Assets/i18n/<lang>.json` into `Application.Resources`; XAML uses
  `{DynamicResource key}`, so switching language updates the UI live.
- `ThemeService` writes the accent colour, corner radii, fonts, the reading direction and Fluent's
  `SystemAccentColor*` into application resources. Arabic reads right to left: `Styles/Soft.axaml`'s Window style
  takes `FlowDirection` from that resource, so every window mirrors; the donut, the crop canvas and animated images
  stay left to right. Light/dark surface colours are theme dictionaries in `Styles/Soft.axaml`; a user's
  `theme.json` (`CustomTheme`, see [CUSTOM-THEMES.md](CUSTOM-THEMES.md)) overrides them with theme dictionaries in
  application resources, which are searched first.
- Window zoom (`Services/UiZoom`): Theme → *Window size* (− / + and *Reset*), and in any tracked window the
  platform's command key (`HotkeyConfiguration.CommandModifiers`: Ctrl, Cmd on macOS) with the mouse wheel, plus /
  minus (Shift allowed, Alt not: AltGr is Ctrl + Alt) or 0 for 100 %. 70–180 % in 10 % steps, every window at once.
  `UiZoom.Track(window)` wraps the window's content in a `LayoutTransformControl`, so layouts see the room they really
  have. Small wheel deltas (touchpads, smooth wheels, pinching on a Windows touchpad) add up to one step per whole notch.
  Windows that can't be resized grow with the zoom (their designed width × the zoom) up to their screen, cap their
  height to it and scroll; the engine setup keeps itself on screen and scrolls its own list (`fitsItself`).
  Tracked: the main window, the preset, group, rename and merge-order editors, the engine setup, LibreOffice options,
  close, sponsor, what's new and update-progress windows and `WindowService`'s message dialog. Not tracked, so at
  100 %: the donut (it has its own size), the toast and the corner notices (update, engine update, feedback, format
  request, elevation), which are placed in a screen corner like system notifications, and the crop overlay, whose
  wheel zooms the crop. Drop-down lists, context menus and tooltips open in their own popup windows outside the
  transform and stay at 100 % too. The size is `UiScale` in settings.json (1 when missing, so no schema step); it is
  written by `UserDataStore.SaveUiScale` 0.6 s after the last change (and on exit), without raising `SettingsChanged`.

## "What's new" slides

After an update, Filee shows the new version's features once as slides (an animation or picture, a title, a line or
two). The pieces:

- `Assets/WhatsNew/<version>/slides.json` lists the slides: `id`, `media` (a file next to it), and the i18n keys of
  `title` and `text` (`whats_new.<version>.<id>.title`, in every language file). `SlideDeck` (Controls) reads every
  manifest from the assembly's resources; a broken one is skipped.
- `AnimatedImage` shows GIF, animated WebP or a still picture. Avalonia's `Image` shows only the first frame, so
  `AnimatedImageDecoder` decodes frames with Skia's `SKCodec`, which also says which earlier frame each one is drawn
  over (`RequiredFrame`, disposal included). The file is copied into an `SKData` and each frame is decoded straight
  into a `WriteableBitmap`'s framebuffer: no managed buffer is ever handed to native code. `FrameTimeline` keeps the
  frame durations (0–10 ms counts as 100 ms, as in browsers) and the loop count. The animation pauses while the
  control is hidden or its window is minimized, shows the first frame with reduced motion (`AutoPlay`), and gives
  the decoder and bitmap back when it leaves the visual tree or its source changes.
- `SlideShow` is the deck itself (picture, texts, position dots), built in code so `Filee.Setup` can link the same
  files for a slideshow on its progress page (`AutoAdvance`). All slides' texts sit in one grid cell, only the
  current one visible, so the height is the tallest slide's and the window doesn't jump.
- `WhatsNewWindow` adds the card, ✕, Previous / Next ("Get started" on the last slide), ←/→ and Esc.
- `WhatsNewService` decides at start-up, before the first run is marked complete:
  - a fresh install (first run not completed) needs no slides, as the first-run setup introduces Filee; its
    version is stored as seen;
  - otherwise `AppSettings.WhatsNewSeenVersion` (null in settings older than the slides, which means "updated")
    is compared with the running version, and the newest deck after it, up to this version, is shown;
  - the same version or a downgrade shows nothing and never lowers what was seen; an update without a deck is
    stored as seen quietly.
  The version is stored when the window opens. A start in the background (`--background` at sign-in, or the
  Explorer menu) keeps the deck until `WindowService.ShowMain` raises `MainShown`; the installer starts an updated
  Filee with `--background --whats-new`, which shows it at once. About → "What's new in Filee X" opens the newest
  deck again.

The `<!-- update -->` lines of CHANGELOG.md are the other half: the update card shows them for a version that isn't
installed yet; the slides introduce the installed one. The demo animations are rendered by
`tests/Filee.App.Tests/WhatsNewMedia.cs` from the real windows (see CONTRIBUTING.md, "Releasing").

## Command line and watch folders

- `Filee.Cli` (`filee-cli.exe`, run as `filee` through `cli\filee.cmd`, which Setup can put on PATH) composes the same
  pipeline as the app without any UI (`CliHost`: `UserDataStore`, `EngineRegistry`, `ConverterCatalog`, `JobQueue`)
  and reads the app's presets and engine order. It is published into the app folder, so both share one runtime and
  the bundled engines; its name can't be `filee.exe` next to `Filee.exe` on a case-insensitive file system.
- `Filee.Core/Watching/FolderWatcher` watches one `WatchRule` with a `FileSystemWatcher` and converts a file once it
  has kept its size and time for a moment and opens without sharing (downloads and copies in progress are skipped).
  Temporary files, unknown formats and dedicated output and originals folders are ignored. A rule may also write
  into the watched folder itself. In that case, generated file versions (path, size and modification time) and
  generated result directories are recorded in `.filee-watch-<rule hash>.json` inside the watched root. The hidden
  state file prevents those results from becoming new inputs, including after an inbox watcher restarts; a changed
  file with an old output name is accepted again. A failure to save this state is logged because results may be
  retried after restart. Removing a rule's state file also forgets its results.
  The tray app runs one per rule (`WatchFolderService`, Settings → Watch folders, conversions in the toast and
  history); `filee watch` runs one on its own, with a stable `cli` rule identity across command restarts.

## Platform code

Everything OS-specific goes through `IPlatformServices` (`Filee.Core/Platform`). Windows lives in
`Filee.Platform.Windows`; macOS will get its own project. Global input uses SharpHook on both.

Home → Recent conversions opens a result with `OpenFile` (the shell on Windows, `open` on macOS, `xdg-open` on Linux,
so a file is handed to its app and never started itself; only formats Filee knows are offered) and, where
`SupportsOpenWith` (Windows), the shell's "Open with" dialog (`SHOpenWithDialog`, on a thread of its own because it
blocks until closed). The cards' pictures come from `HistoryThumbnails`: Skia decodes them off the UI thread, scaled
down while decoding where the format allows, from a copy read in one go so the file isn't held open, and a small cache
keeps them while the files are unchanged. For video files, FFmpeg extracts and scales a representative frame off the UI thread without locking the file.

On macOS, files reach Filee from file managers other than Finder too (issue #160). The "Convert with Filee" service
(`MacOSServicesProvider`) and the donut's drags (`DraggedFiles`) read file URLs first, then the older
`NSFilenamesPboardType` array of paths (`PasteboardPaths`); the service also takes plain-text lines that are absolute
paths. The donut registers those types with AppKit next to Avalonia's own, so such a drag reaches it, and a drag or
service request without readable files is logged with the types it offered. File promises (`NSFilePromiseProvider`)
can't be received through Avalonia; for a browser drag Filee reads the address and HTML it also offers, which the donut
registers too (see "Files dragged from a web browser"). A click-mode donut opened for a later launch or the service forgives one loss of
focus within a second of opening (`ActivationGrace`): Filee becoming the active app can take focus away from it.

The donut, the toast and the notices are transparent windows. On Windows Filee keeps Avalonia's rendering defaults
(ANGLE on Direct3D 11 composed through WinUI composition, then DirectComposition, then the window's redirection
surface; software rendering when Direct3D 11 fails): each of them keeps per-pixel transparency. The log says what was
picked (`Rendering: AngleEgl with WinUIComposition`), lists the graphics adapters, carries Avalonia's own errors from
starting the GPU paths (`AvaloniaLogForwarder`), and describes each kind of transparent window the first time it
shows (`TransparentWindows`). A system backdrop (Acrylic, Mica) or an accent blur on such a window turns its
transparent area into a blurred rectangle (issue #269). Filee sets neither, so one found there came from another
program: each time the window shows, a backdrop is set back to none and the accent (which can't be read back) is
turned off. For a report that still shows the wrong thing, the `FILEE_RENDERING` environment variable picks another
way to draw (`WindowsRendering`): `dcomp` (skip WinUI composition), `redirection` (Direct3D into the redirection
surface) or `software` (no GPU); unset or `auto` keeps the defaults. Filee reads it at start.

## Explorer integration

Settings → General → "Show “Convert with Filee” in the Explorer context menu" (`AppSettings.ContextMenuEnabled`) is
applied by `WindowsPlatformServices.SetContextMenu` at start-up and whenever settings change (installed builds only):

- **Classic verb** `HKCU\Software\Classes\*\shell\Filee` (`Filee.exe --convert "%1"`, one process per file; the running
  instance collects them through `SingleInstance`) and a **Send To** shortcut. Per user, no admin rights. On Windows 10
  this is the entry; on Windows 11 it sits under "Show more options".
- **Windows 11 top-level entry** (optional): an `IExplorerCommand` in `FileeExplorerMenu.dll`
  (`src/Filee.ExplorerMenu`, plain C, no C runtime) declared by the sparse package `FileeExplorerMenu.msix`
  (`ExplorerMenuPackage.CreateManifest`: `desktop4:FileExplorerContextMenus`, `desktop5:ItemType Type="*"`,
  `com:SurrogateServer`, `uap10:AllowExternalContent`, `AppListEntry="none"`). Both files ship next to Filee.exe and the
  package's external location is the install folder (`C:\Program Files\Filee` by default).
  - The package is **unsigned** (publisher contains `OID.2.25.311729368913984317654407730594956997722=1`; Filee has no
    code-signing certificate). Windows only registers an unsigned package with executable content **with
    administrator rights** (it installs it for all users); a per-user attempt fails with 0x80073D2B "an unsigned
    package cannot include Executable activations". Setup (`src/Filee.Setup`) has those rights anyway and
    registers it on Windows 11 x64 while its option is ticked. Later the General page offers "Add to the main menu",
    which runs `Add-AppxPackage -Path … -ExternalLocation … -AllowUnsigned` in Windows PowerShell through one UAC
    prompt (`ExplorerMenuRegistration`). A signed package would register per user without a prompt.
  - The manifest version (`ExplorerMenuPackage.ManifestVersion`) is independent of the app version, so app updates
    don't need a new prompt: the DLL is loaded from the install folder and updated with the app. Bump it only when the
    manifest changes; the page then offers to add the entry again ("Outdated").
  - The DLL shows the entry only for files (not folders or items inside ZIP folders) and only while
    `%APPDATA%\Filee\explorer-menu.txt` exists. The app writes the title in the UI language there while the setting is
    on and deletes it when the setting is off, so switching the setting takes effect without re-registering. While the
    package is registered the classic verb is removed (Windows lists packaged commands under "Show more options" too).
  - Invoke starts `Filee.exe --convert "<path>"…` next to the DLL and returns at once. Selections longer than a
    command line go through `%TEMP%\Filee-convert-*.txt` (`--convert-list`, one path per line), which the app deletes.
  - Updates: Setup asks the running Filee to exit (`Filee.exe --quit`), then renames a `FileeExplorerMenu.dll` that
    Explorer still has loaded into its backup folder (Windows allows renaming a loaded DLL, not replacing or deleting
    it) and has Windows delete the old copy at the next restart, so updating needs no restart. An update keeps the
    registration as it was and refreshes it. Uninstall runs `Filee.exe --uninstall-cleanup` (`UninstallCleanup`): it
    removes the package, the title file, the classic verb, the startup entry and downloaded engines; the uninstaller
    then asks whether to delete the settings too (they stay by default).
  - Build: `pwsh build/build-explorer-menu.ps1` downloads a pinned Zig (SHA-256 checked) into `build/.cache`, compiles
    the DLL and packs the MSIX with `build/tools/make-explorer-package.cs` (Windows' own packaging API, no SDK).
    Output: `build/.cache/explorer-menu`; CI builds it for the tests, `build/build-installer.ps1` builds it into the
    publish folder.

Debugging: `Get-AppxPackage Filee.ExplorerMenu` shows the registration (`InstallLocation` is the external location);
`Get-AppPackageLog -ActivityID <id>` explains a failed deployment. To try a local build, copy
`FileeExplorerMenu.dll` + `.msix` next to an installed Filee.exe and use the button, or run elevated:
`Add-AppxPackage -Path FileeExplorerMenu.msix -ExternalLocation <folder with Filee.exe> -AllowUnsigned`. Explorer picks
the entry up for new menus; if not, restart Explorer. To remove it by hand:
`Get-AppxPackage Filee.ExplorerMenu | Remove-AppxPackage`.

## Windows installer

`Filee-<version>-win-Setup.exe` is Filee's own installer, `src/Filee.Setup` (it replaced the Inno Setup script of
1.x). It is an Avalonia program with the app's look and translations: it links `Styles/Soft.axaml` (which therefore
styles only Avalonia's own controls; Filee's controls are in `Controls.axaml`), `SoftPalette` (accent, radii, font
order), `AppLanguages` and `Assets/i18n/*.json` (Setup's texts are the `installer.*` keys). To stay small it uses
Windows' own UI fonts (Segoe UI, Nirmala UI and the UI language's CJK font first; titles bold, as those CJK fonts
have no semibold) instead of the app's embedded Noto fonts.

- **Look**: one borderless 640×420 card (`SetupWindow`, its own window buttons, drag to move) whose states crossfade
  (`Views/StateViews.axaml`): Welcome (the logo assembling itself, `DonutLogo`; one Install button; an Options sheet
  sliding up with the language, folder, options and engines), Progress (the new version's "What's new" slides, a thin
  `ShimmerBar`), Done (`CheckBurst`, "Launch Filee"), Failed ("Copy details") and the uninstaller's states.
  `SoftBackdrop` draws slowly drifting accent blobs behind it. The slides are the app's (`SlideShow`, `AnimatedImage`,
  `SlideDeck` and `Assets/WhatsNew`, linked): the newest deck up to the version being installed, advancing every 6 s.
  Every animation runs only while the window is visible and not minimized (`FrameLoop`) and is a still picture when
  Windows' "Show animations" is off (`SetupMotion`). `/SILENT` shows only the progress card, without slides. After an
  update Setup starts Filee with `--background --whats-new`, so the slides show once in the app as well.

- **Build** (`build/build-installer.ps1`): Setup is published as one self-contained, trimmed file
  (`-p:FileeSetupPublish=true`), so it runs on Windows 10 1809+ x64 without .NET. `build/tools/make-setup.cs` appends
  the app's publish folder as the payload: `[program][LZMA streams of all files][manifest JSON][72-byte trailer]`
  (`SetupPayload`): ~48 MB segments compressed in parallel with SharpCompress's LZMA (48 MB dictionary), programs
  first through the x86 branch filter (`X86Filter`, from the LZMA SDK) as 7-Zip and Inno Setup do. The manifest lists
  every file with its size, SHA-256 and filter, the SHA-256 of the compressed data, the version and the engines to
  offer (from `EngineDownloads`). Setup.exe's signature goes after the trailer (Setup looks
  for the trailer in front of the certificate table; the manifest is padded so the file ends on the 8-byte boundary
  signtool needs), so Setup.exe is signed after packing. The Setup program is signed before: its signature stays at
  the end of the program, Setup.exe's header stops pointing to it, and the manifest's `programSignature` lets the
  uninstaller copied out of Setup.exe (`CopyProgramTo`) point to it again, so `uninstall.exe` is signed too. The code is trim- and AOT-safe
  (compiled bindings, source-generated P/Invoke and COM); Native AOT would need the MSVC linker on the build machine
  and still couldn't link SkiaSharp's native libraries in.
- **Rights**: Setup starts unelevated (asInvoker) and, for an install for all users, starts itself again with
  administrator rights (one UAC prompt) and waits. The elevated copy writes what to start at the end into a file
  (`/LAUNCHFILE`), and the unelevated one starts Filee from it as the signed-in user. Elevated without such a parent
  (run as administrator, a package manager), it starts Filee with Explorer's token.
- **Install** (`Installer`): checks the free space and the payload's hash first, closes Filee (`--quit`, then ends
  what still runs from the folder), unpacks into `<folder>\~setup-*\new` checking every file, checks that every
  program finds the Microsoft C++ runtime it imports (next to it or in System32; `RuntimeCheck`), then swaps the files
  in (`FileSwap`: replaced and obsolete files are renamed into a backup, which works for files in use) and writes the
  uninstall entry, the shortcuts and PATH. Any failure until then puts everything back. Filee's own settings (Explorer
  menu, start at sign-in, engines, a picked language) are handed to Filee on its command line, as before.
- **Updates from 1.x**: the uninstall key stays `{C8686E06-8327-40A2-B0F7-3DEDD61D9248}_is1` (Inno Setup's AppId), so
  Apps & Features keeps one Filee. `PreviousInstall` reads the folder and choices of an Inno Setup installation; an
  update keeps the folder and what is registered now (desktop shortcut, PATH, the top-level menu package), removes
  `unins000.*` and every file 2.0 doesn't have, and rewrites the key without the `Inno Setup: …` values. The app
  counts as installed when `uninstall.exe` (or 1.x's `unins000.exe`) is next to it (`UpdateService.IsInstalled`).
- **Uninstall**: `uninstall.exe` is Setup without its payload. Run from the install folder it copies itself to
  `%TEMP%` and waits for the copy, which removes what `uninstall.json` lists (other files in the folder stay), the
  shortcuts, the PATH entry and the key. Files in use are moved out and deleted at the next restart.
- **Command line**: Inno Setup's switches (`SetupOptions`, documented at the top of the file), because Filee 1.x
  updates itself with `/SILENT /NORESTART` and winget passes `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. Exit codes as
  Inno Setup's: 0 done, 1 could not start, 2 cancelled before installing (also a declined UAC prompt), 4 failed.
- **Testing**: `Filee.Setup.Tests` runs installs, updates (also over a simulated 1.x installation) and uninstalls
  against `MemoryRegistry`, a fake `ISetupSystem` and scratch folders, and renders every page. `/SANDBOX=<folder>`
  runs the real Setup with a registry file and all folders inside `<folder>`; `build-installer.ps1 -CheckOnly` (CI)
  installs, updates and uninstalls that way.

## Linux installer and updates

On Linux, Filee is distributed as a self-contained archive (`Filee-<version>-<rid>.tar.gz` for `linux-x64` and
`linux-arm64`) containing the application, `filee-cli`, bundled conversion engines, application icon, `.desktop` launcher,
and standalone shell scripts (`install.sh`, `uninstall.sh`).

- **Installation**: `install.sh` installs for the current user into `~/.local/share/filee/application` without root,
  creating a launcher symlink at `~/.local/bin/filee` and desktop registration at
  `~/.local/share/applications/com.filee.app.desktop`. A `.filee-package` marker containing `com.filee.app` identifies
  the directory as an authentic managed installation. Running without `--upgrade` refuses to overwrite an existing
  installation.
- **In-app updates** (`UpdateService`): Checks GitHub releases for matching `<rid>.tar.gz` and `<rid>-SHA256SUMS.txt`
  assets. The tarball is verified against SHA-256 before extraction. Archive extraction validates that all entries stay
  within the target directory, rejecting absolute paths, directory symlinks, and escaping links, and masks permissions to
  standard rwx bits (excluding setuid/setgid/sticky bits).
- **Transactional upgrade**: `UpdateAsync` stages the payload in a private directory (`~/.local/share/filee/.update-*`),
  spawns a detached `/bin/sh` helper, and requests application shutdown (`App.Quit`). Once the old process exits, the
  helper executes `install.sh --upgrade` (which safely moves the existing installation to a backup directory, validates the
  new files, and rolls back on failure or signal interruption), and restarts Filee with `--background --whats-new`.
  Output and rollback diagnostics are appended to Filee's daily log (`~/.config/Filee/logs/`). Downloaded conversion engines
  (`~/.local/share/Filee/engines`) and user settings (`~/.config/Filee`) remain untouched.
- **Uninstallation**: `uninstall.sh` removes the application directory, desktop launcher, and `~/.local/bin/filee` symlink
  after verifying the `.filee-package` marker. User settings and converted files are kept.
