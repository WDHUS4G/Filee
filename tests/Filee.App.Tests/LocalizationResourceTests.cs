using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;

namespace Filee.App.Tests;

public class LocalizationResourceTests
{
    [AvaloniaFact]
    public void Arabic_font_chain_draws_trimming_ellipsis_and_matches_arabic_letters()
    {
        TestServices.EnsureInitialized("ar");
        try
        {
            var family = FontFamily.Parse(ThemeService.FontFamilies("ar"));
            // Avalonia takes the trimming symbol from the primary typeface rather than applying character fallback.
            Assert.NotEqual((ushort)0, new Typeface(family).GlyphTypeface.CharacterToGlyphMap.GetGlyph(0x2026));
            Assert.True(FontManager.Current.TryMatchCharacter(0x0645, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, family, System.Globalization.CultureInfo.GetCultureInfo("ar"), out var arabic));
            Assert.NotEqual((ushort)0, arabic.GlyphTypeface.CharacterToGlyphMap.GetGlyph(0x0645));
        }
        finally { TestServices.EnsureInitialized("en"); }
    }

    [AvaloniaFact]
    public void Arabic_mirrors_the_main_window_and_english_brings_it_back()
    {
        TestServices.EnsureInitialized("en");
        using var model = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        model.Navigate("general");
        var window = new MainWindow { DataContext = model, Width = 1080, Height = 820 };
        window.Show();
        try
        {
            RenderTests.Pump();
            var navigation = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("nav"));
            var page = window.FindControl<ScrollViewer>("PageScroller")!;
            double Left(Visual visual) => new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(window)!.Value).Left;
            double Center(Visual visual) =>
                new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(window)!.Value).Center.X;
            Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
            Assert.True(Center(navigation) < Center(page));
            var englishNavigationLeft = Left(navigation);

            TestServices.EnsureInitialized("ar");
            RenderTests.Pump();
            Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
            Assert.Equal(FlowDirection.RightToLeft, page.FlowDirection);
            // The sidebar moves to the right edge, the page to its left.
            Assert.True(Center(navigation) > Center(page));
            Assert.Equal(window.Bounds.Width - englishNavigationLeft, Left(navigation) + navigation.Bounds.Width, 1);
            Assert.Equal("عام", AppHost.Get<ILocalizer>()["general.title"]);

            TestServices.EnsureInitialized("en");
            RenderTests.Pump();
            Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
            Assert.Equal(englishNavigationLeft, Left(navigation), 1);
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void Arabic_windows_read_right_to_left_but_pictures_and_the_donut_do_not_mirror()
    {
        TestServices.EnsureInitialized("ar");
        var canvas = new CropCanvas();
        var donut = new DonutMenu();
        var picture = new AnimatedImage();
        var window = new Window { Content = new StackPanel { Children = { new TextBlock(), canvas, donut, picture } } };
        window.Show();
        try
        {
            Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
            Assert.Equal(FlowDirection.RightToLeft, ((StackPanel)window.Content).Children[0].FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, canvas.FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, donut.FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, picture.FlowDirection);
            Assert.True(Application.Current!.TryGetResource("general.language", null, out var value));
            Assert.Equal("اللغة", value);

            TestServices.EnsureInitialized("en");
            Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void Strings_are_available_as_application_resources()
    {
        TestServices.EnsureInitialized("ko");
        Assert.True(Application.Current!.TryGetResource("home.title", null, out var value));
        Assert.Equal("홈", value);
    }

    [AvaloniaFact]
    public void DynamicResource_in_xaml_resolves_and_follows_language_changes()
    {
        TestServices.EnsureInitialized("en");
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, text.GetResourceObservable("home.title"));
        var toggle = new ToggleSwitch();
        var window = new Window { Content = new StackPanel { Children = { text, toggle } } };
        window.Show();
        Assert.Equal("Home", text.Text);

        TestServices.EnsureInitialized("ko");
        Assert.Equal("홈", text.Text);
        TestServices.EnsureInitialized("ja");
        Assert.Equal("ホーム", text.Text);
        Assert.Equal("オン", toggle.OnContent);
        Assert.Equal("オフ", toggle.OffContent);
        window.Close();
    }
}
