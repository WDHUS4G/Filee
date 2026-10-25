// Review Portuguese at the minimum supported window width, in both themes, with real Skia text layout.

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class PortugueseRenderTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void First_run_engine_names_and_statuses_fit_their_cards(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<ILocalizer>());
        var calibre = vm.Packages.Single(p => p.Package.Id == "calibre");
        var beforeStatus = calibre.Status;
        var beforeSystem = calibre.FromSystem;
        var window = new EngineSetupWindow { DataContext = vm };
        try
        {
            // The long translated package name and system-copy status reproduced a native pt-PT cut-off.
            // Make the state deterministic even when Calibre is absent from the test host.
            calibre.FromSystem = true;
            calibre.Status = EnginePackageStatus.OwnCopy;
            window.Show();
            RenderTests.Pump();
            Capture(window, $"portuguese-engine-setup-{language}.png");
            var card = window.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("card") && ReferenceEquals(b.DataContext, calibre));
            var headings = card.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text == calibre.Name || t.Text == calibre.StatusText).ToList();
            Assert.Equal(2, headings.Count);
            foreach (var text in headings)
            {
                var right = text.TranslatePoint(new Point(text.Bounds.Width, 0), card)!.Value.X;
                Assert.True(right <= card.Bounds.Width - card.Padding.Right + 1,
                    $"{language}: engine heading '{text.Text}' extends beyond its card ({right} in {card.Bounds.Width})");
                Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1 &&
                            text.TextLayout.Height <= text.Bounds.Height + 1,
                    $"{language}: engine heading '{text.Text}' is cut off");
            }
        }
        finally
        {
            window.Close();
            vm.CloseCommand.Execute(null);
            calibre.Status = beforeStatus;
            calibre.FromSystem = beforeSystem;
        }
    }

    [AvaloniaTheory]
    [InlineData("pt-PT", 440, "mp4-720")]
    [InlineData("pt-BR", 440, "mp4-720")]
    [InlineData("pt-PT", 560, "mp4-720")]
    [InlineData("pt-BR", 560, "mp4-720")]
    [InlineData("pt-PT", 440, "compress-pdf")]
    [InlineData("pt-BR", 440, "compress-pdf")]
    [InlineData("pt-PT", 560, "compress-pdf")]
    [InlineData("pt-BR", 560, "compress-pdf")]
    [InlineData("pt-PT", 440, "half-size")]
    [InlineData("pt-BR", 440, "half-size")]
    [InlineData("pt-PT", 560, "half-size")]
    [InlineData("pt-BR", 560, "half-size")]
    [InlineData("en", 440, "mp4-720")]
    [InlineData("en", 440, "compress-pdf")]
    [InlineData("en", 440, "half-size")]
    public void Translated_choices_fit_in_the_preset_dialog(string language, int width, string preset)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        var vm = new PresetEditorViewModel(AppHost.Get<UserDataStore>().FindPreset(preset)!, loc);
        if (vm.ShowMediaQuality)
            vm.MediaQuality = vm.MediaQualities.Single(q => q.Value == MediaQuality.Small);
        if (vm.ShowPdfCompression)
            vm.CompressionLevel = vm.CompressionLevels.Single(q => q.Value == PdfCompression.Screen);
        var window = new PresetEditorWindow { DataContext = vm, Width = width, Height = 820 };
        try
        {
            window.Show();
            RenderTests.Pump();
            Capture(window, $"portuguese-preset-{preset}-{language}-{width}.png");
            var boxes = window.GetVisualDescendants().OfType<ComboBox>()
                .Where(c => c.IsEffectivelyVisible && !c.Classes.Contains("targets"));
            Assert.NotEmpty(boxes);
            foreach (var box in boxes)
            {
                var choice = Assert.IsAssignableFrom<IChoice>(box.SelectedItem);
                var text = box.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == choice.Label);
                // A wrapped line may retain a trailing space outside its width; only the rendered text matters.
                Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1 &&
                            text.TextLayout.Height <= text.Bounds.Height + 1,
                    $"{language} at {width}px: '{text.Text}' is cut off ({text.TextLayout.Width}×{text.TextLayout.Height} in {text.Bounds.Size})");
            }
            foreach (var label in window.GetVisualDescendants().OfType<TextBlock>()
                         .Where(t => t.IsEffectivelyVisible && t.Classes.Contains("label")))
                Assert.True(label.TextLayout.Width <= label.Bounds.Width + 1 &&
                            label.TextLayout.Height <= label.Bounds.Height + 1,
                    $"{language} at {width}px: field label '{label.Text}' is cut off");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Tagline_fits_in_the_navigation_column(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        using var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>());
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            RenderTests.Pump();
            var text = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == loc["app.tagline"]);
            var end = text.TranslatePoint(new Point(text.Bounds.Width, 0), window)!.Value.X;
            Assert.True(end <= 216, $"{language}: tagline extends beyond the navigation padding ({end})");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Icon_actions_have_localized_accessible_names(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        using var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>());
        vm.Navigate("presets");
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            RenderTests.Pump();
            foreach (var (name, key) in new[] { ("DuplicatePresetButton", "presets.duplicate"), ("DeletePresetButton", "presets.delete") })
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
                Assert.Equal(loc[key], AutomationProperties.GetName(button));
            }
        }
        finally
        {
            window.Close();
        }
    }

    public static TheoryData<string, string, bool, double> Pages
    {
        get
        {
            var data = new TheoryData<string, string, bool, double>();
            foreach (var language in new[] { "pt-PT", "pt-BR" })
                foreach (var page in new[] { "home", "general", "triggers", "toolbar", "presets", "theme", "engines", "about", "watch" })
                    foreach (var dark in new[] { false, true })
                        foreach (var scale in new[] { 1.0, 2.0 })
                            // Both themes at 1x; 2x checks text/layout geometry once per language and page.
                            if (scale == 1 || !dark)
                                data.Add(language, page, dark, scale);
            return data;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public void Pages_render_at_minimum_width_in_both_themes(string language, string page, bool dark, double scale)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        var settings = AppHost.Get<UserDataStore>().Settings.Theme;
        theme.Apply(new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light, ReduceMotion = true }, language);
        using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate(page);
        var window = new MainWindow { DataContext = vm, Width = 820, Height = 820 };
        try
        {
            window.Show();
            window.SetRenderScaling(scale);
            RenderTests.Pump();
            var name = $"portuguese-{page}-{language}-{(dark ? "dark" : "light")}{(scale == 2 ? "-2x" : "")}";
            Capture(window, name + ".png");
            var scroller = window.FindControl<ScrollViewer>("PageScroller")!;
            // Complex editors keep a usable minimum width. On small windows every column remains reachable
            // by scrolling, instead of the grid crushing its middle column and drawing controls on top of it.
            Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"{name}: page content exceeds the outer viewport");
            var editor = window.GetVisualDescendants().OfType<ScrollViewer>().SingleOrDefault(s => s.Name == "EditorScroller");
            if (editor?.Content is Control content)
                Assert.Equal(Math.Max(content.MinWidth, editor.Viewport.Width), content.Bounds.Width, precision: 1);
            if (editor is not null && editor.Extent.Width > editor.Viewport.Width + 1)
            {
                editor.Offset = new Vector(editor.Extent.Width, 0);
                RenderTests.Pump();
                Assert.True(editor.Offset.X > 0, $"{name}: overflowing content is unreachable");
                Capture(window, name + "-right.png");
                editor.Offset = default;
            }
            if (scroller.Extent.Height > scroller.Viewport.Height)
            {
                scroller.ScrollToEnd();
                RenderTests.Pump();
                Capture(window, name + "-bottom.png");
            }
        }
        finally
        {
            window.Close();
            theme.Apply(settings, language);
        }
    }

    public static TheoryData<string, string> DefaultEditors
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var language in new[] { "en", "pt-PT", "pt-BR" })
                foreach (var page in new[] { "toolbar", "theme", "presets", "engines" })
                    data.Add(language, page);
            foreach (var (language, _) in LocalizationService.Languages.Where(l => l.Code is not ("en" or "pt-PT" or "pt-BR")))
                data.Add(language, "toolbar");
            return data;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(DefaultEditors))]
    public void Editors_fit_at_the_default_window_width(string language, string page)
    {
        TestServices.EnsureInitialized(language);
        using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate(page);
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        try
        {
            window.Show();
            RenderTests.Pump();
            var editor = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "EditorScroller");
            Capture(window, $"default-{page}-{language}.png");
            Assert.True(editor.Extent.Width <= editor.Viewport.Width + 1,
                $"{language}/{page}: horizontal scroll at the default size ({editor.Extent.Width} > {editor.Viewport.Width})");
            if (page == "toolbar")
            {
                foreach (var name in new[] { "NewPresetButton", "NewGroupButton" })
                {
                    var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
                    var text = button.GetVisualDescendants().OfType<TextBlock>().Single();
                    var bounds = new Rect(text.Bounds.Size).TransformToAABB(text.TransformToVisual(button)!.Value);
                    Assert.True(bounds.Left >= -1 && bounds.Right <= button.Bounds.Width + 1 &&
                                text.TextLayout.Width <= text.Bounds.Width + 1,
                        $"{language}: '{text.Text}' exceeds its action button");
                    Assert.All(text.TextLayout.TextLines, line => Assert.False(line.HasCollapsed));
                }
                var loc = AppHost.Get<ILocalizer>();
                var toolbar = (Filee.App.ViewModels.Pages.ToolbarPageViewModel)vm.CurrentPage;
                foreach (var item in toolbar.DonutItems)
                    Assert.Equal(loc.DonutName(AppHost.Get<UserDataStore>().FindPreset(item.Id)!), item.Label);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Close_dialog_actions_fit_inside_the_window(string language)
    {
        TestServices.EnsureInitialized(language);
        var window = new CloseAskWindow();
        try
        {
            window.Show();
            RenderTests.Pump();
            Capture(window, $"portuguese-close-{language}.png");
            foreach (var button in window.GetVisualDescendants().OfType<Button>())
            {
                var start = button.TranslatePoint(default, window)!.Value;
                Assert.True(start.X >= 0 && start.X + button.Bounds.Width <= window.Bounds.Width,
                    $"{language}: close action '{button.Content}' exceeds the dialog");
                foreach (var text in button.GetVisualDescendants().OfType<TextBlock>())
                    Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 1,
                        $"{language}: '{text.Text}' is cut off");
            }
        }
        finally
        {
            window.Close();
        }
    }
    // Geometry assertions always run; PNG encoding is opt-in for a visual review.
    private static void Capture(TopLevel window, string name)
    {
        if (Environment.GetEnvironmentVariable("FILEE_LOCALIZATION_SCREENSHOTS") == "1")
            RenderTests.Save(window, name);
    }

}
