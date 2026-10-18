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

Any IDE works (Visual Studio 2022+, Rider, VS Code with C# Dev Kit).

## Linux and macOS development

These ports are development builds until native desktop tests are complete. Install .NET 10 and PowerShell 7,
then build on the target OS. Supported package targets are `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`.

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

- **Linux:** drag gestures require X11. Install the Avalonia desktop dependencies (fontconfig, FreeType, X11,
  Xrandr, Xi, Xtst, XkbCommon, ICE and SM) and CJK fonts for document comparisons. File actions are installed for
  Thunar, Nautilus, Nemo and Dolphin. The first enabled keyboard shortcut uses Thunar's native custom action;
  close Thunar before changing or pausing that shortcut and reopen it afterwards. Global selected-file queries
  and hold gestures are not supported. Wayland can use file actions, the drop zone, watch folders and CLI.
- **macOS:** grant Filee Accessibility permission for gestures and Finder Automation permission for selection.
  Keep the development `.app` at a stable path while testing these permissions. Finder integration is a Quick
  Action under Services; auto-start uses a per-user LaunchAgent.
- **Engines:** packages are selected by OS and architecture. Linux LibreOffice/Ghostscript and macOS
  FFmpeg/Ghostscript currently use system or user-selected installations. Linux ARM64 has no Unhwp native binary.
  The macOS LibreOffice package does not include H2Orestart; native rhwp/Unhwp provide the supported HWP routes.
  Use Settings > Engines to inspect actual availability before comparing formats.

The reusable `portable.yml` workflow builds and tests all four targets. Native platform tests skip on other OSes.
Before calling a port ready, verify the installed package, CLI conversion, Finder/Thunar selection with Unicode
and spaced paths, drag and keyboard gestures, permissions denied/granted, Retina/scaling, tray, login startup,
engine downloads, watch folders, and uninstall. CI GUI startup alone does not validate these interactions.

The website lives in the adjacent `../filee-web` repository. Its downloads must match real release assets and
keep development status visible until native validation is complete.

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
- Keep UI strings out of code: add a key to all three `i18n/*.json` files (a test checks they match).
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
`installer/Filee.iss` with a pinned, portable Inno Setup, and uploads `Filee-X.Y.Z-win-Setup.exe`, the portable zip and
their SHA-256 sums with release notes generated from the merged pull requests.

To try the installer locally run `pwsh build/build-installer.ps1` (output in `Releases/`). Setup needs administrator
rights; `pwsh build/build-installer.ps1 -CheckOnly` only compiles the script. The installer's engine page and some of
its texts are generated from `EngineDownloads` and the app's translations (`build/tools/make-installer-engines.cs`);
its own texts are in the `[CustomMessages]` section of `installer/Filee.iss` (English, Korean, Simplified Chinese).
