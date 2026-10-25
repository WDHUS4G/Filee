// Portuguese locale selection, live resources and regional formatting, independent of the host OS language.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class PortugueseLocalizationTests
{
    [AvaloniaTheory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void The_KWin_window_identifier_stays_compatible_when_the_ui_language_changes(string language)
    {
        TestServices.EnsureInitialized(language);
        var window = new Filee.App.Views.RadialWindow();
        try
        {
            var title = window.Title;
            Assert.False(string.IsNullOrWhiteSpace(title));
            var script = File.ReadAllText(Path.Combine(WhatsNewMedia.RepositoryRoot,
                "installer", "linux", "kwin", "filee-donut", "contents", "code", "main.js"));
            Assert.Contains($"w.caption === \"{title}\"", script);
            AppHost.Get<LocalizationService>().SetLanguage(language == "pt-PT" ? "pt-BR" : "pt-PT");
            Assert.Equal(title, window.Title);
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [Theory]
    [InlineData("auto", "pt-PT", "pt-PT")]
    [InlineData("AUTO", "pt-BR", "pt-BR")]
    [InlineData("auto", "PT-br", "pt-BR")]
    [InlineData("auto", "pt-Latn-BR", "pt-BR")]
    [InlineData("auto", "pt_BR", "pt-BR")]
    [InlineData("auto", "pt-PT-x-br", "pt-PT")]
    [InlineData("auto", "pt-x-br", "pt-PT")]
    [InlineData("auto", "pt-Latn-PT-x-br", "pt-PT")]
    [InlineData("auto", "pt-PT-u-ca-gregory-x-br", "pt-PT")]
    [InlineData("auto", "pt-BR-x-pt", "pt-BR")]
    [InlineData("auto", "pt-Latn-BR-u-ca-gregory", "pt-BR")]
    [InlineData("auto", "pt-u-rg-brzzzz", "pt-PT")]
    [InlineData("auto", "pt", "pt-PT")]
    [InlineData("auto", "pt-AO", "pt-PT")]
    [InlineData("auto", "pt-MZ", "pt-PT")]
    [InlineData("auto", "pt-CV", "pt-PT")]
    [InlineData("pt-PT", "pt-BR", "pt-PT")]
    [InlineData("PT-br", "pt-PT", "pt-BR")]
    [InlineData("en", "pt-BR", "en")]
    [InlineData("invalid", "pt-BR", "pt-BR")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData("auto", "", "en")]
    [InlineData("auto", "ptx", "en")]
    [InlineData("auto", "ja-JP", "ja")]
    [InlineData("auto", "es-MX", "es")]
    public void Language_selection_respects_the_region_and_manual_override(string setting, string system, string expected) =>
        Assert.Equal(expected, LocalizationService.Resolve(setting, () => system));

    [Theory]
    [InlineData("pt-PT", "Português (Portugal)")]
    [InlineData("pt-BR", "Português (Brasil)")]
    public void Both_variants_are_offered_under_their_native_names(string code, string name)
    {
        Assert.Contains((code, name), LocalizationService.Languages);
        Assert.Equal(code, LocalizationService.Resolve(code.ToUpperInvariant()));
    }

    // Contextual glossary: "arquivo" remains valid pt-PT in PDF/A's archival description, for example.
    [AvaloniaTheory]
    [InlineData("common.save", "Guardar", "Salvar")]
    [InlineData("general.show_in_dock", "Mostrar o Filee na Dock", "Mostrar o Filee no Dock")]
    [InlineData("crop.reset", "Repor", "Redefinir")]
    [InlineData("crop.restore", "Restaurar", "Restaurar")]
    [InlineData("common.delete", "Eliminar", "Excluir")]
    [InlineData("nav.about", "Acerca de", "Sobre")]
    [InlineData("nav.home", "Página inicial", "Início")]
    [InlineData("home.title", "Página inicial", "Início")]
    [InlineData("about.sponsor", "Apoiar", "Patrocinar")]
    [InlineData("sponsor.title", "Apoiar o Filee", "Patrocinar o Filee")]
    [InlineData("sponsor.thanks", "Obrigado a quem apoia o Filee", "Obrigado aos nossos patrocinadores")]
    [InlineData("nav.engines", "Motores", "Motores")]
    [InlineData("general.language_auto", "Predefinição do sistema", "Padrão do sistema")]
    [InlineData("triggers.button", "Botão do rato", "Botão do mouse")]
    [InlineData("triggers.scope.FileManager", "Gestores de ficheiros e ambiente de trabalho", "Gerenciadores de arquivos e área de trabalho")]
    [InlineData("triggers.scope.FileManager_linux", "Gestor de ficheiros e ambiente de trabalho", "Gerenciador de arquivos e área de trabalho")]
    [InlineData("presets.percent", "Percentagem", "Porcentagem")]
    [InlineData("presets.save_every_frame", "Guardar todos os fotogramas de um vídeo", "Salvar cada quadro de um vídeo")]
    [InlineData("watch.status_watching", "A monitorizar", "Monitorando")]
    [InlineData("engines.package.calibre.description",
        "Guarda em MOBI e AZW3 para Kindle e converte formatos raros de livros digitais (LIT, LRF, PDB, CHM…).\nEPUB, MOBI, AZW3, FB2 e banda desenhada são lidos sem ele.",
        "Salva MOBI e AZW3 para Kindle e converte formatos raros de e-book (LIT, LRF, PDB, CHM…).\nEPUB, MOBI, AZW3, FB2 e quadrinhos são lidos sem ele.")]
    public void Editorial_glossary_keeps_variant_specific_terms_separate(string key, string european, string brazilian)
    {
        TestServices.EnsureInitialized("en");
        Assert.Equal(european, LocalizationService.LoadStrings("pt-PT")[key]);
        Assert.Equal(brazilian, LocalizationService.LoadStrings("pt-BR")[key]);
    }

    [AvaloniaFact]
    public void Resources_and_toggle_labels_follow_live_switches_between_both_variants()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<LocalizationService>();
        var changes = new List<string>();
        void Changed(object? sender, EventArgs args) => changes.Add(loc.Language);
        loc.LanguageChanged += Changed;
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, text.GetResourceObservable("common.save"));
        var toggle = new ToggleSwitch();
        var window = new Window { Content = new StackPanel { Children = { text, toggle } } };
        try
        {
            window.Show();
            foreach (var (code, save) in new[] { ("pt-PT", "Guardar"), ("pt-BR", "Salvar") })
            {
                Assert.True(loc.NeedsUpdate(code));
                loc.SetLanguage(code);
                Assert.False(loc.NeedsUpdate(code));
                Assert.Equal(save, text.Text);
                Assert.Equal(code == "pt-PT" ? "Ativado" : "Ligado", toggle.OnContent);
                Assert.Equal(code == "pt-PT" ? "Desativado" : "Desligado", toggle.OffContent);
                Assert.Equal(code, CultureInfo.CurrentUICulture.Name);
                Assert.Equal(code, CultureInfo.DefaultThreadCurrentUICulture!.Name);
                Assert.Equal("unknown.translation.key", loc["unknown.translation.key"]);
            }
            Assert.Equal(["pt-PT", "pt-BR"], changes);
        }
        finally
        {
            window.Close();
            loc.LanguageChanged -= Changed;
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaTheory]
    [InlineData("pt-PT", "pt-PT", "12,5/s · 25")]
    [InlineData("pt-BR", "pt-BR", "12,5/s · 25")]
    [InlineData("pt-PT", "en-US", "12.5/s · 25")]
    [InlineData("pt-BR", "en-US", "12.5/s · 25")]
    public void Ui_language_preserves_the_users_regional_number_format(string language, string region, string expected)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(region);
            TestServices.EnsureInitialized(language);
            Assert.Equal(region, CultureInfo.CurrentCulture.Name);
            Assert.Equal(expected, AppHost.Get<LocalizationService>().Format("download.badge", 12.5, 25));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaTheory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Catalogs_preserve_format_items_tokens_and_line_breaks(string language)
    {
        TestServices.EnsureInitialized(language);
        var english = LocalizationService.LoadStrings("en");
        var translated = LocalizationService.LoadStrings(language);
        Assert.Equal(english.Keys.Order(), translated.Keys.Order());
        foreach (var (key, source) in english)
        {
            var value = translated[key];
            // Count repeated placeholders too, and retain the named filename-pattern tokens verbatim.
            Assert.Equal(Tokens(source), Tokens(value));
            Assert.Equal(source.Count(c => c == '\n'), value.Count(c => c == '\n'));
            Assert.Equal(value.Normalize(NormalizationForm.FormC), value);
            if (Regex.IsMatch(source, @"\{\d"))
            {
                var format = CompositeFormat.Parse(value);
                var args = Enumerable.Repeat<object>(123, format.MinimumArgumentCount).ToArray();
                Assert.DoesNotContain("{", string.Format(CultureInfo.GetCultureInfo(language), format, args));
            }
        }
        // These are real folder names/CLI arguments, not translatable labels.
        Assert.Matches(@"\boriginals\b", translated["watch.originals_move"]);
        Assert.Matches(@"\bconverted\b", translated["watch.output_placeholder"]);
        // Argument descriptions may be translated; the command and option are literal.
        Assert.Matches(@"filee watch <[^>]+> --to <[^>]+>", translated["watch.cli_hint"]);
        Assert.Contains(@"IMG_(\d+)", translated["watch.rename_find"]);
        Assert.Contains("$1", translated["watch.rename_replace"]);
    }

    [AvaloniaTheory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Descriptive_engine_names_are_localized_and_brands_are_preserved(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        try
        {
            vm.Navigate("engines");
            var engines = ((EnginesPageViewModel)vm.CurrentPage).Engines;
            Assert.Equal(language == "pt-PT" ? "Dados (motor integrado)" : "Dados (mecanismo integrado)",
                engines.Single(e => e.Converter.Id == "data").Name);
            Assert.Equal("ImageMagick", engines.Single(e => e.Converter.Id == "magick").Name);
            Assert.DoesNotContain(engines, e => e.Name.Contains("built-in", StringComparison.Ordinal));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Count_labels_work_with_zero_one_and_many_without_parenthesized_plurals(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<LocalizationService>();
        foreach (var count in new[] { 0, 1, 2, 21 })
        {
            Assert.Equal($"Conversões no histórico: {count}", loc.Format("general.history_count", count));
            Assert.Equal($"Conversões: {count}, sem falhas", loc.Format("watch.results_ok", count));
            Assert.Equal($"Conversões: {count} · Falhas: 1", loc.Format("watch.results_failed", count, 1));
            Assert.Equal($"Conversões: {count}", loc.Format("engines.conversions", count));
            Assert.Equal($"{count} s para terminar", loc.Format("engines.eta.seconds", count));
            Assert.Equal($"{count} min para terminar", loc.Format("engines.eta.minutes", count));
        }
    }

    private static string[] Tokens(string value) =>
        Regex.Matches(value, @"\{[^{}]+\}").Select(m => m.Value).Order().ToArray();

    [AvaloniaTheory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Preset_numbers_use_regional_separators_without_changing_saved_values(string region)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(region);
            TestServices.EnsureInitialized(region);
            var loc = AppHost.Get<ILocalizer>();
            var preset = new Preset { Name = "Animation", TargetFormat = "gif" };
            var vm = new PresetEditorViewModel(preset, loc);
            Assert.Equal("0,5×", vm.AnimationSpeeds.Single(s => s.Value == 0.5).Label);
            Assert.Contains(MediaOptions.MaxFrames.ToString("N0", CultureInfo.CurrentCulture), vm.FrameEstimate);
            vm.AnimationSpeed = vm.AnimationSpeeds.Single(s => s.Value == 0.5);
            Assert.True(vm.Apply());
            Assert.Equal(0.5, preset.Image.PlaybackSpeed);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            TestServices.EnsureInitialized("en");
        }
    }
}
