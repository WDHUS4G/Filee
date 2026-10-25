# Adding a language

1. Copy `src/Filee.App/Assets/i18n/en.json` to `<code>.json` (e.g. `ja.json`, `de.json`, `zh-TW.json`).
   The file is a flat list of `"key": "text"` pairs.
2. Translate the values. Keep `{0}`, `{1}` placeholders — a test checks they match English.
   Keep the text short where English is short (buttons, donut labels).
3. Register the language in `AppLanguages.All` (`src/Filee.App/Services/AppLanguages.cs`) with its native name,
   e.g. `("ja", "日本語")`; the order there is the order of the language pickers. Setup (`src/Filee.Setup`) offers it
   too: add the code to the `/LANG` list in `src/Filee.Setup/SetupOptions.cs` and a row to `SetupTextsTests`.
   If the OS culture should pick it automatically, extend `LocalizationService.Resolve`. Match the code itself and
   `<code>-*` only (`"fr"` and `"fr-CA"`, not North Frisian `"frr"`), and add rows for both to
   `LocalizationTests.Auto_follows_the_language_the_user_reads_the_os_in`.
4. `LocalizationTests.Languages` automatically checks every registered language for key and placeholder parity.
5. If the script needs another font, add a static Noto font to `build/fetch-fonts.ps1` (pinned with its SHA-256),
   to the font list in `SoftPalette.FontFamilies` and to the Noto line in `THIRD-PARTY-NOTICES.md` (variable fonts
   are not supported by Avalonia). Add the script's system UI fonts too, Windows' to `SoftPalette.FontFamilies` and
   `SoftPalette.SystemFontFamilies` (Setup draws with Windows' fonts only) and macOS' to `FontFamilies`, e.g.
   Leelawadee UI and Thonburi for Thai.
   A language written right to left (Arabic) mirrors every window by itself (`SoftPalette.ApplyFlowDirection`).
   Pictures and drawings that must not mirror (the donut, the crop canvas, animated images, logos) set
   `FlowDirection="LeftToRight"` on themselves; custom-drawn text is shaped in the UI culture's direction, as
   `DonutMenu` does.
6. Run `dotnet test` and look at the screenshots in `tests/Filee.App.Tests/bin/.../screenshots`
   (add your language to `RenderTests.Pages` to render a few pages).

Missing keys fall back to English at runtime, but the test suite requires every language to be complete.

Portuguese ships as `pt-PT` and `pt-BR`. Automatic selection uses `pt-BR` for Brazil (including `pt-Latn-BR`),
and `pt-PT` for neutral `pt` and other Portuguese regions. An explicit selection always wins.
Only the region subtag counts: a private-use suffix in `pt-PT-x-br` must not select Brazil.
The UI language changes `CurrentUICulture`; number and date formatting continue to use the user's regional
`CurrentCulture`. Do not change machine-readable filenames, pattern tokens or command-line switches when translating.

The Windows installer embeds the same JSON files, including the `installer.*` keys. Test the language with
`Filee.Setup.Tests` as well, and extend `SetupTexts.Resolve` when a region needs a distinct catalog. The CLI
currently has a separate, English-only text provider.

Native macOS text also needs `<language>.lproj/ServicesMenu.strings` and, for permission explanations,
`InfoPlist.strings`, plus a `CFBundleLocalizations` entry in `installer/macos/Info.plist`. Keep the English service
identifier on the left of `ServicesMenu.strings`; translate its value. The packaging loop copies all `.lproj` folders
and validates their strings files with `plutil`. These system texts follow macOS language preferences.
Keep technical identifiers such as the KWin `Filee Donut` window title stable.

Optional, but welcome in the same or a later pull request: a translated README. Add `README.<code>.md` with every
section `README.md` has, link it from the language line at the top of the other READMEs and add the language to
their list of UI languages. The maintainers add the language to the website (filee.sh).

## Demonstration recordings

A slide may supply `localizedMedia` entries, for example
`"pt-PT": { "media": "pt-PT/crop.webp", "poster": 57 }`. The code uses the exact UI language, otherwise the shared
recording; regional variants do not borrow each other's recordings. Poster indices belong to each recording.
The optional `FILEE_WHATSNEW_LANGUAGE` environment variable makes `WhatsNewMedia.Generate_2_0_0` generate a
language's recordings and register them in the shared manifest. Rebuild App and Setup after generating them.
The language-showcase slide intentionally demonstrates several languages and stays shared. Browser and flip
are desktop recordings made outside the headless generator: retain the shared recordings until localized desktop
scenes show the full interaction. Do not automatically replace them with abbreviated headless scenes.

Compact action names can use `preset.<name>.donut` when the donut already shows the target format underneath.
Keep the full action name for the preset editor; custom names always take priority. `.short` is for a group's inner
choices, whose group supplies additional context (Rotate → Left), and is a separate contract.

## Adding a UI string

Add the key to **all** language files in the same pull request. In XAML use
`Text="{DynamicResource my.key}"`; in code use `ILocalizer["my.key"]` or `ILocalizer.Format("my.key", arg)`.

Set `FILEE_LOCALIZATION_SCREENSHOTS=1` for PNG captures in `PortugueseRenderTests`. Geometry assertions
always run. Dispose directly created `MainWindowViewModel` fixtures: production `WindowService` owns their lifetime,
but a window constructed directly in a test does not automatically unsubscribe its model.
