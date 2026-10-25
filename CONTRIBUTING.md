# Contributing to Filee

Thanks for helping! Filee is built to be easy to extend: most contributions touch one file plus a test.

## Setup

1. Install the .NET 10 SDK (the exact feature band is pinned in `global.json`, newer patches roll forward).
2. `pwsh build/fetch-fonts.ps1` (optional, for the Noto Sans UI fonts).
3. `pwsh build/fetch-engines.ps1 -Only rhwp,pandoc` (optional: rhwp renders HWP/HWPX, and everything converted
   through HWPX — DOCX, XLSX, CSV, PPTX, Markdown — to PDF, and converts HWP ↔ HWPX; Pandoc adds HTML / Markdown ↔
   DOCX. XLSX ↔ CSV and anything → HWPX work without either). Without `-Only` it also downloads LibreOffice with
   H2Orestart and Java (~420 MB), which only older formats such as DOC, XLS and PPT need. Engines in the
   repository's `engines/` folder are picked up by debug builds; users download the large ones from the app.
   Filee can also discover supported engines on `PATH` and in their standard installation folders. Settings >
   Engines lets you choose a specific copy. Microsoft Office is not used.
4. `dotnet run --project src/Filee.App`, `dotnet test`.

To try a build next to the Filee you use every day (testing, measuring memory), set `FILEE_DATA_DIR` to an empty
folder first. Settings, logs and downloaded engines then live there, the copy is its own single instance (later
launches with the same variable, `--quit` included, reach it and never your installed Filee), and it registers no
startup entry, context menu or Explorer menu.

Any IDE works (Visual Studio 2022+, Rider, VS Code with C# Dev Kit).

## Linux and macOS development

Linux packages (x64, ARM64) are released like Windows; macOS stays a preview until it is notarized. Install .NET 10
and PowerShell 7, then build on the target OS. Supported package targets are `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`.

```sh
pwsh build/fetch-fonts.ps1
pwsh build/fetch-portable-engines.ps1 -Runtime linux-x64 -Destination engines
dotnet test Filee.slnx -c Release
pwsh build/build-portable.ps1 -Runtime linux-x64
```

Use the matching runtime on a Mac or ARM64 Linux machine. Packaging runs on Linux/macOS so archives retain
executable permissions. Each archive contains the app, CLI, rhwp, 7-Zip, installation instructions and per-user
install/uninstall scripts. `Filee-<version>-<rid>-SHA256SUMS.txt` verifies the archive. macOS bundles use
`com.filee.app` and a development ad-hoc signature by default; public distribution still needs Developer ID signing
and notarization. `-SigningIdentity` can supply a signing identity, but does not perform notarization.

Linux release packages must run on Ubuntu 22.04 (glibc 2.35). CI builds rhwp from source on Ubuntu 22.04
(`build/build-linux-native.py`) and passes it with `-RhwpOverride`; a local package without it needs the glibc of the
machine that built it. Check a package with
`python3 build/check-elf-glibc.py --max 2.35 <unpacked package>`. See [docs/ENGINES.md](docs/ENGINES.md#linux-builds-for-older-glibc).

- **Linux:** drag gestures require X11 by default. Install the Avalonia desktop dependencies (fontconfig, FreeType, X11,
  Xrandr, Xi, Xtst, Xt, XkbCommon, ICE and SM) and CJK fonts for document comparisons. File actions are installed for
  Thunar, Nautilus, Nemo and Dolphin. The first enabled keyboard shortcut uses Thunar's native custom action;
  close Thunar before changing or pausing that shortcut and reopen it afterwards. Global selected-file queries
  and hold gestures are not supported. Wayland can use file actions, the drop zone, watch folders and CLI.
  On KDE Plasma 6 and Hyprland, the drag gesture can also run there, experimentally with `FILEE_WAYLAND_GESTURES=1`. It needs
  the user in the `input` group (libuiohook's libinput back end) and a compositor positioning mechanism (the KWin script in
  `installer/linux/kwin` or a Hyprland floating window rule). Native Wayland support (`Avalonia.Wayland`) is also available
  as an opt-in via `FILEE_USE_WAYLAND=1`. See `installer/linux/INSTALL.txt`.
  On GNOME Wayland, the bundled Shell extension supplies pointer state through a private Unix socket; no input
  group is required. See [docs/GNOME-WAYLAND.md](docs/GNOME-WAYLAND.md) for setup, limits and troubleshooting.
- **macOS:** grant Filee Accessibility permission for gestures and Finder Automation permission for selection.
  Keep the development `.app` at a stable path while testing these permissions. Finder integration is a Quick
  Action under Services; auto-start uses a per-user LaunchAgent.
- **Engines:** packages are selected by OS and architecture. Linux LibreOffice/Ghostscript and macOS
  FFmpeg/Ghostscript currently use system or user-selected installations. Linux ARM64 has no Unhwp native binary.
  The macOS LibreOffice package does not include H2Orestart; native rhwp/Unhwp provide the supported HWP routes.
  Use Settings > Engines to inspect actual availability before comparing formats.

The reusable `portable.yml` workflow builds and tests all four targets and starts the Linux packages on Ubuntu 22.04.
Native platform tests skip on other OSes.
Before releasing a port's packages, verify the installed package, CLI conversion, Finder/Thunar selection with Unicode
and spaced paths, drag and keyboard gestures, permissions denied/granted, Retina/scaling, tray, login startup,
engine downloads, watch folders, and uninstall. CI GUI startup alone does not validate these interactions.

The website lives in the adjacent `../filee-web` repository. Its downloads must match real release assets and
keep macOS marked as a preview until it is notarized.

## Where things live

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first. In short:

- **New file format or engine** → `src/Filee.Engines`, see [docs/ADDING-A-CONVERTER.md](docs/ADDING-A-CONVERTER.md).
- **New language** → `src/Filee.App/Assets/i18n`, see [docs/ADDING-A-LANGUAGE.md](docs/ADDING-A-LANGUAGE.md).
- **UI** → `src/Filee.App/Views` (XAML) + `ViewModels` (CommunityToolkit.Mvvm).
- **Domain rules** (routes, output names, settings) → `src/Filee.Core` — no UI or OS code here.

## Code style

- `dotnet format` must pass (CI runs `dotnet format --verify-no-changes`). `.editorconfig` has the rules.
- Comments are in English so every contributor can read them.
  - Every file starts with a short comment saying what it is for.
  - Public types and members get `///` XML docs.
  - Explain *why* for anything non-obvious (workarounds, engine quirks) — see `LibreOfficeConverter.cs` for the tone.
- Keep UI strings out of code: add a key to every `i18n/*.json` file (a test checks they match).
- Prefer small, focused pull requests.

## Tests

- `tests/Filee.Core.Tests` – pure logic (fast).
- `tests/Filee.Engines.Tests` – real conversions on generated input files. Tests needing an engine that is not
  installed skip automatically. XLSX and PPTX inputs are generated by `OfficeBuilders.cs`, DOCX by
  `DocxBuilder.cs`.
- `tests/Filee.App.Tests` – gesture state machine, donut geometry, translations and headless UI rendering.
  Screenshots of every page land in `tests/Filee.App.Tests/bin/<config>/net10.0/screenshots` — attach them to
  UI pull requests.

## Pull requests

1. Fork, create a branch, make your change with tests.
2. `dotnet format && dotnet test`.
3. Open a PR and fill in the template. Screenshots for UI changes, please.

## Releasing (maintainers)

Bump `<Version>` in `Directory.Build.props`, merge, then push a tag `vX.Y.Z`. `.github/workflows/release.yml` runs the
tests and `build/build-installer.ps1 -Portable`: it publishes the app (`-p:FileeRelease=true`, which lets it register
Explorer and startup entries), adds the Explorer menu extension and the bundled rhwp and 7-Zip, compiles
`src/Filee.Setup` (Filee's own installer) and appends the app to it. It drafts the release with the Linux and macOS
packages and release notes generated from the merged pull requests, and keeps the tested Windows build unsigned (the
artifact `windows-unsigned`).

Then, on a PC signed in to SimplySign Desktop (Certum's cloud certificate; its key needs the phone app's token), sign
the Windows files and add them to the draft:

```
pwsh build/sign-release.ps1 -Version X.Y.Z -CertificateThumbprint <sha1>
```

It signs every unsigned program in the build and the Setup program, packs and signs `Filee-X.Y.Z-win-Setup.exe`, zips
the portable copy, checks the signed Setup in a sandbox install and uploads the files with their SHA-256 sums. Look
over the draft and publish it (or pass `-Publish`). `build/sign-windows.ps1` signs any folder or file on its own.

### "What's new" slides for a release

A release with features worth showing gets slides, which Filee shows once after updating to it (see
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#whats-new-slides)). A release without slides shows nothing.

1. Write the CHANGELOG section with its `<!-- update -->` lines first: the slides are the visual version of it.
   Pick 5–7 features; put the small ones together on a last "And more" slide.
2. Add `src/Filee.App/Assets/WhatsNew/<version>/slides.json` (copy the previous one): per slide an `id`, the
   `media` file and the i18n keys `whats_new.<version with _>.<id>.title` / `.text`.
3. Add the keys to **every** `src/Filee.App/Assets/i18n/*.json` (a title and one or two short sentences; Chinese
   without fullwidth `；` / `：`).
4. Add a `Generate_<version>` method to `tests/Filee.App.Tests/WhatsNewMedia.cs` with a scene per slide (real
   windows driven frame by frame; start each clip on a frame that explains the slide on its own, since reduced
   motion shows only that one) and run it:

   ```
   dotnet build tests/Filee.App.Tests
   tests/Filee.App.Tests/bin/Debug/net10.0/Filee.App.Tests.exe -method "Filee.App.Tests.WhatsNewMedia.*" -explicit only
   ```

   `FILEE_WHATSNEW_ONLY=crop,theme` renders only those clips. Each file must stay under 400 KB (the test checks it,
   and `WhatsNewTests` checks all of them together stay under 2 MB); look at the first frames in
   `bin/.../screenshots/whatsnew-*.png` and at the window in `whatsnew-window-*.png`.
5. Run `dotnet test`: `WhatsNewTests` checks that every slide's media and keys exist.

To try it in the app, build with the new version (`dotnet run --project src/Filee.App -p:Version=X.Y.Z`) against a
data folder whose settings say an older version was seen (`"whatsNewSeenVersion": "1.0.0"`, `FILEE_DATA_DIR`), or
open About → "What's new".

To try the installer locally run `pwsh build/build-installer.ps1` (output in `Releases/`; it fetches the bundled
engines, the C++ runtime and the Zig toolchain for the Explorer menu into `build/.cache`; run `build/fetch-fonts.ps1`
first). `pwsh build/build-installer.ps1 -CheckOnly` builds Setup around a stub app and installs, updates and
uninstalls it in a sandbox. To run the real Setup without touching your PC, give it a scratch folder:
`Filee-X.Y.Z-win-Setup.exe /SANDBOX=D:\scratch` (registry, Program Files, Start menu, desktop, PATH and `%APPDATA%`
all go into that folder; no administrator rights are asked for and Filee is not started). Setup's texts are the
`installer.*` keys of the app's translation files; its engine page uses the app's engine names. See
[Architecture](docs/ARCHITECTURE.md#windows-installer).
