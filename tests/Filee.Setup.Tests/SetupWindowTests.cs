// Renders every state of Setup's card headlessly (Skia) in Filee's languages, light and dark: catches XAML and binding
// errors and text that doesn't fit, and leaves screenshots in bin/.../screenshots for review. Animations are off
// (static logo, a still from each clip) except in the test that runs them.

extern alias app;

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.Setup.Controls;
using Filee.Setup.Installation;
using Filee.Setup.Payload;
using Filee.Setup.ViewModels;
using Filee.Setup.Views;
using CommandLine = app::Filee.App.Services.CommandLine;

namespace Filee.Setup.Tests;

public class SetupWindowTests
{
    [ModuleInitializer]
    internal static void StaticScreenshots() => SetupMotion.Enabled = false;

    /// <summary>Every language Setup offers.</summary>
    public static TheoryData<string> Languages => new(SetupTexts.Languages.Select(l => l.Code));

    private sealed class FakeRunner : ISetupRunner
    {
        public InstallChoices? Installed { get; private set; }
        public bool? DeletedSettings { get; private set; }
        public InstallResult Result { get; set; } = new(new LaunchRequest(@"C:\Program Files\Filee\Filee.exe", "--background"), []);
        public TaskCompletionSource? Hold { get; set; }

        public InstallResult Install(InstallChoices choices, IProgress<SetupProgress> progress)
        {
            Installed = choices;
            progress.Report(new SetupProgress(SetupStep.Copying, 0.62));
            Hold?.Task.Wait();
            return Result;
        }

        public Exception? Uninstall(bool deleteSettings, IProgress<SetupProgress> progress)
        {
            DeletedSettings = deleteSettings;
            return null;
        }
    }

    private static readonly EngineOffer[] Offers =
    [
        new("libreoffice", ["libreoffice", "jre"], 520_000_000, 1_265_000_000, false),
        new("pandoc", ["pandoc"], 42_000_000, 236_000_000, true),
        new("ghostscript", ["ghostscript", "vcruntime"], 20_000_000, 31_000_000, false),
        new("ffmpeg", ["ffmpeg"], 99_000_000, 272_000_000, false),
        new("calibre", ["calibre"], 226_000_000, 663_000_000, false),
    ];

    private static (SetupViewModel Model, SetupTexts Texts, FakeRunner Runner) Model(string language, bool update = false, bool uninstall = false)
    {
        var texts = new SetupTexts(language);
        App.ApplyLook(Application.Current!, texts);
        var runner = new FakeRunner();
        var previous = update
            ? new PreviousInstall(@"C:\Program Files\Filee", "1.12.0", InstallScope.Machine, true, SetupTasks.Defaults, "en", null)
            : null;
        var choices = new InstallChoices(@"C:\Program Files\Filee", InstallScope.Machine, SetupTasks.Defaults, ["pandoc"], texts.Language);
        var engines = update || uninstall ? [] : Offers.Select(o => (o, o.Id == "ffmpeg")).ToList();
        var model = new SetupViewModel(texts, runner, "2.0.0", choices, previous, engines, modernMenuSupported: true,
            neededSpace: 420_000_000, uninstall: uninstall)
        {
            LogFile = @"C:\Users\me\AppData\Local\Temp\Filee-Setup-20261020-101500.log",
            SettingsFolder = @"C:\Users\me\AppData\Roaming\Filee",
        };
        return (model, texts, runner);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Every_state_renders_and_fits(string language)
    {
        var (model, _, runner) = Model(language);
        var window = Show(model);
        Assert.Equal(language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, window.FlowDirection);
        Check(window, $"setup-1-welcome-{language}.png");

        model.ToggleOptionsCommand.Execute(null);
        Pump();
        Assert.True(model.OptionsOpen);
        // FFmpeg is installed already: shown, not offered.
        Assert.False(model.Engines.Single(e => e.Id == "ffmpeg").CanSelect);
        Check(window, $"setup-2-options-{language}.png", sheetMayScroll: true);

        runner.Hold = new TaskCompletionSource();
        var install = model.InstallAsync();
        Pump();
        Assert.True(model.IsProgress);
        Assert.False(model.OptionsOpen);
        Assert.Equal(62, model.Percent);
        var slides = window.GetVisualDescendants().OfType<SlideShow>().Single();
        Assert.True(slides.IsEffectivelyVisible);
        // A busy runner can pass the six-second interval while pumping layout. Freeze the
        // showcase and select the known slide before checking its language-specific media.
        slides.AutoAdvance = null;
        slides.GoTo(0);
        Pump();
        Assert.Equal(model.Deck!.Slides.Count, slides.Slides.Count);
        Assert.Equal(model.Deck.Slides[0].MediaFor(language).Source, slides.Media.Source);
        Assert.True(slides.Media.HasPicture);
        if (language is "pt-PT" or "pt-BR")
        {
            // Browser and flip keep the complete shared desktop interaction; crop uses its own Portuguese media.
            Assert.Equal(model.Deck.Slides[0].Media, slides.Media.Source);
            slides.GoTo(3);
            Pump();
            Assert.Contains($"/{language}/crop.webp", slides.Media.Source!.AbsolutePath);
            Assert.Equal(model.Deck.Slides[3].MediaFor(language).PosterFrame, slides.Media.PosterFrame);
        }
        Check(window, $"setup-3-installing-{language}.png");

        runner.Result = new InstallResult(new LaunchRequest(@"C:\Program Files\Filee\Filee.exe", "--install-engines=pandoc"),
            [new RuntimeWarning([new MissingRuntime(@"engines\rhwp\rhwp.exe", "vcruntime140.dll")])]);
        runner.Hold.SetResult();
        await install;
        Pump();
        Assert.True(model.IsDone);
        Check(window, $"setup-4-done-{language}.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dark_cards_follow_the_system()
    {
        var app = Application.Current!;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Dark;
            foreach (var language in new[] { "en", "ko", "ru", "ar", "pt-PT", "pt-BR" })
            {
                var (model, _, runner) = Model(language);
                var window = Show(model);
                Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
                Check(window, $"setup-dark-1-welcome-{language}.png");
                model.ToggleOptionsCommand.Execute(null);
                Check(window, $"setup-dark-2-options-{language}.png", sheetMayScroll: true);
                runner.Hold = new TaskCompletionSource();
                var install = model.InstallAsync();
                Pump();
                Check(window, $"setup-dark-3-installing-{language}.png");
                runner.Hold.SetResult();
                await install;
                Check(window, $"setup-dark-4-done-{language}.png");
                window.Close();
            }
            var (uninstall, _, _) = Model("ko", uninstall: true);
            var card = Show(uninstall);
            Check(card, "uninstall-dark-1-confirm-ko.png");
            card.Close();
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    [AvaloniaFact]
    public async Task Updates_and_failures_render()
    {
        foreach (var language in new[] { "en", "ko", "ru", "ar", "pt-PT", "pt-BR" })
        {
            var (model, texts, runner) = Model(language, update: true);
            var window = Show(model);
            Assert.False(model.HasEngines);
            Assert.Equal(texts["installer.update"], model.PrimaryText);
            Check(window, $"setup-update-1-welcome-{language}.png");

            runner.Result = new InstallResult(null, [], new SetupException("installer.error.space", "space", "C:\\", "420 MB", "12 MB"));
            await model.InstallAsync();
            Pump();
            Assert.True(model.IsFailed);
            Assert.Equal(4, model.ExitCode);
            Check(window, $"setup-update-2-failed-{language}.png");
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Uninstall_renders_and_keeps_the_settings_by_default()
    {
        foreach (var language in new[] { "en", "ko", "ru", "zh-CN", "ar", "pt-PT", "pt-BR" })
        {
            var (model, _, runner) = Model(language, uninstall: true);
            var window = Show(model);
            Assert.True(model.IsUninstallPage);
            Assert.False(model.DeleteSettings);
            Assert.Contains(@"C:\Users\me\AppData\Roaming\Filee", model.SettingsText);
            Check(window, $"uninstall-1-confirm-{language}.png");
            await model.UninstallAsync();
            Pump();
            Assert.True(model.IsUninstalled);
            Assert.False(runner.DeletedSettings);
            Assert.Equal(0, model.ExitCode);
            Check(window, $"uninstall-2-done-{language}.png");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_whats_new_slides_take_turns_while_installing()
    {
        var (model, texts, runner) = Model("en");
        Assert.Equal(new Version(2, 0, 0), model.Deck!.Version);
        var window = Show(model);
        runner.Hold = new TaskCompletionSource();
        _ = model.InstallAsync();
        Pump();
        var slides = window.GetVisualDescendants().OfType<SlideShow>().Single();
        Assert.Equal(TimeSpan.FromSeconds(6), slides.AutoAdvance);
        // Still pictures without animations (the poster frame), the texts in Setup's language.
        Assert.False(slides.PlayAnimations);
        for (var i = 0; i < slides.Slides.Count; i++)
        {
            Assert.Equal(i, slides.Index);
            Assert.True(slides.Media.HasPicture);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == texts[slides.Slides[i].TitleKey]);
            slides.AdvanceNow();
            Pump();
        }
        Assert.Equal(0, slides.Index);
        runner.Hold.SetResult();
        window.Close();
    }

    [AvaloniaFact]
    public void Silent_and_uninstall_show_no_slides()
    {
        var (model, _, _) = Model("en");
        model.Silent = true;
        model.Page = SetupPage.Progress;
        Assert.False(model.ShowShowcase);
        var (uninstall, _, _) = Model("en", uninstall: true);
        Assert.Null(uninstall.Deck);
    }

    [AvaloniaFact]
    public async Task With_animations_on_the_card_still_works()
    {
        SetupMotion.Enabled = true;
        try
        {
            var (model, _, runner) = Model("en");
            var window = Show(model);
            Pump(120);
            Check(window, "setup-motion-welcome-en.png");
            runner.Hold = new TaskCompletionSource();
            var install = model.InstallAsync();
            Pump(120);
            runner.Hold.SetResult();
            await install;
            Pump(120);
            Check(window, "setup-motion-done-en.png");
            window.Close();
        }
        finally
        {
            SetupMotion.Enabled = false;
        }
    }

    [AvaloniaTheory]
    [InlineData("ko")]
    [InlineData("ar")]
    public void Picking_a_language_changes_every_text_and_is_handed_to_filee(string language)
    {
        var (model, texts, _) = Model("en");
        var window = Show(model);
        model.SelectedLanguage = model.Languages.Single(l => l.Code == language);
        Pump();
        Assert.Equal(language, texts.Language);
        // Picking Arabic mirrors the card at once.
        Assert.Equal(language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, window.FlowDirection);
        Assert.Equal(texts["installer.install"], model.PrimaryText);
        Assert.Contains("2.0.0", model.WelcomeText);
        var choices = model.Choices();
        Assert.Equal(language, choices.Language);
        Assert.True(choices.LanguagePicked);
        window.Close();
    }

    [AvaloniaFact]
    public void Choices_follow_the_checkboxes()
    {
        var (model, _, _) = Model("en");
        model.ContextMenu = false;
        Assert.False(model.ModernMenuEnabled);
        model.DesktopIcon = true;
        model.Engines.Single(e => e.Id == "pandoc").Selected = false;
        model.Engines.Single(e => e.Id == "calibre").Selected = true;
        var choices = model.Choices();
        Assert.False(choices.Tasks.ContextMenu);
        Assert.False(choices.Tasks.ModernMenu);
        Assert.True(choices.Tasks.DesktopIcon);
        Assert.Equal(["calibre"], choices.Engines);
        Assert.False(choices.LanguagePicked);
    }

    [AvaloniaFact]
    public void Keyboard_enter_installs_and_escape_closes_the_options_then_cancels()
    {
        var (model, _, runner) = Model("en");
        var window = Show(model);
        var closed = false;
        model.CloseRequested += (_, _) => closed = true;

        model.ToggleOptionsCommand.Execute(null);
        Pump();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump();
        Assert.False(model.OptionsOpen);
        Assert.False(closed);

        var primary = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PrimaryButton");
        Assert.True(primary.IsDefault);
        Assert.True(primary.IsFocused);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump();
        Assert.True(closed);
        Assert.Equal(2, model.ExitCode);
        Assert.Null(runner.Installed);
    }

    [AvaloniaFact]
    public async Task Launch_or_close_on_the_done_card()
    {
        var (model, _, _) = Model("en");
        var window = Show(model);
        await model.InstallAsync();
        Pump();
        model.CloseCommand.Execute(null);
        Assert.Null(model.Launch);
        Assert.Equal(0, model.ExitCode);
        model.LaunchAndCloseCommand.Execute(null);
        Assert.Equal("--background", model.Launch!.Arguments);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Launch_after_an_update_opens_the_main_window_with_the_slides()
    {
        var (model, _, _) = Model("en", update: true);
        var window = Show(model);
        await model.InstallAsync();
        Pump();
        model.LaunchAndCloseCommand.Execute(null);
        var options = CommandLine.Parse(model.Launch!.Arguments.Split(' '));
        Assert.False(options.Background);
        Assert.True(options.WhatsNew);
        window.Close();
    }

    [AvaloniaFact]
    public void Closing_the_window_before_installing_cancels()
    {
        var (model, _, _) = Model("en");
        model.WindowClosing();
        Assert.Equal(2, model.ExitCode);
        Assert.Null(model.Launch);
    }

    private static Window Show(SetupViewModel model)
    {
        // The desktop behind the transparent window's rounded corners (headless captures have no desktop).
        var window = new SetupWindow(model, silent: false) { Background = new SolidColorBrush(Color.Parse("#D9DCE3")) };
        window.Show();
        Pump();
        return window;
    }

    /// <summary>
    /// Saves a screenshot. Everything visible must lie inside the card (no text pushed out of it), no button may cut its
    /// text, and the Options sheet may scroll but not the page.
    /// </summary>
    private static void Check(Window window, string name, bool sheetMayScroll = false)
    {
        Pump();
        var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Card");
        var cardBounds = new Rect(card.Bounds.Size);
        var state = ((SetupWindow)window).State!;
        foreach (var text in state.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Height > 0))
        {
            var corner = text.TranslatePoint(new Point(text.Bounds.Width, text.Bounds.Height), card);
            Assert.True(corner is { } p && p.Y <= cardBounds.Height + 0.5 && p.X <= cardBounds.Width + 0.5, $"{name}: \"{text.Text}\" runs out of the card");
            text.Measure(new Size(text.Bounds.Width + text.Margin.Left + text.Margin.Right, double.PositiveInfinity));
            Assert.True(text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom <= text.Bounds.Height + 1, $"{name}: \"{text.Text}\" is cut off");
        }
        // The slides (picture, the tallest slide's text, the dots) fit in the space they get.
        foreach (var slides in state.GetVisualDescendants().OfType<SlideShow>().Where(s => s.IsEffectivelyVisible))
        {
            slides.Measure(new Size(slides.Bounds.Width, double.PositiveInfinity));
            Assert.True(slides.DesiredSize.Height <= slides.Bounds.Height + 1, $"{name}: the slides need {slides.DesiredSize.Height:0} px, they get {slides.Bounds.Height:0}");
            slides.InvalidateMeasure();
        }
        // Nothing may overlap the buttons at the bottom of the card.

        if (state.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "PrimaryButton" && b.IsEffectivelyVisible) is { } primary
            && state is Grid { RowDefinitions.Count: 2 } grid
            && grid.Children.OfType<Control>().First(c => Grid.GetRow(c) == 0 && c.IsEffectivelyVisible) is { } content
            && !content.IsVisualAncestorOf(primary))
        {
            var contentBottom = content.TranslatePoint(new Point(0, content.Bounds.Height), card)!.Value.Y;
            var buttonsTop = primary.TranslatePoint(default, card)!.Value.Y;
            Assert.True(contentBottom <= buttonsTop + 0.5, $"{name}: the content ({contentBottom:0}) runs into the buttons ({buttonsTop:0})");
        }
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b is not ToggleButton && b.IsEffectivelyVisible))
        {
            button.Measure(Size.Infinity);
            var wanted = button.DesiredSize.Width - button.Margin.Left - button.Margin.Right;
            button.InvalidateMeasure();
            Assert.True(wanted <= button.Bounds.Width + 1, $"{name}: a button cuts its text ({button.Content})");
        }
        if (!sheetMayScroll)
            Assert.DoesNotContain(state.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Extent.Height > s.Viewport.Height + 1 && s.MaxHeight is double.PositiveInfinity);
        Pump();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var folder = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Pump(int times = 40)
    {
        for (var i = 0; i < times; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
