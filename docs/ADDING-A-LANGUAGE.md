# Adding a language

1. Copy `src/Filee.App/Assets/i18n/en.json` to `<code>.json` (e.g. `ja.json`, `de.json`, `zh-TW.json`).
   The file is a flat list of `"key": "text"` pairs.
2. Translate the values. Keep `{0}`, `{1}` placeholders — a test checks they match English.
   Keep the text short where English is short (buttons, donut labels).
3. Register the language in `LocalizationService.Languages`
   (`src/Filee.App/Services/LocalizationService.cs`) with its native name, e.g. `("ja", "日本語")`.
   If the OS culture should pick it automatically, extend `LocalizationService.Resolve`.
4. Add the code to `LocalizationTests.Languages` (`tests/Filee.App.Tests/LocalizationTests.cs`).
5. If the script needs another font (Japanese, Arabic, …), add a static Noto font to `build/fetch-fonts.ps1`
   and to the font list in `ThemeService` (variable fonts are not supported by Avalonia).
6. Run `dotnet test` and look at the screenshots in `tests/Filee.App.Tests/bin/.../screenshots`
   (add your language to `RenderTests.Pages` to render a few pages).

Missing keys fall back to English at runtime, but the test suite requires every language to be complete.

Optional, but welcome in the same or a later pull request: a translated README. Add `README.<code>.md` and link
it from the language line at the top of the other READMEs.

## Adding a UI string

Add the key to **all** language files in the same pull request. In XAML use
`Text="{DynamicResource my.key}"`; in code use `ILocalizer["my.key"]` or `ILocalizer.Format("my.key", arg)`.
