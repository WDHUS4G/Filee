// The donut's opacity: one setting, edited on the Theme page (with the preview following it at once, issue #276) and
// on the Donut toolbar page, and read by the donut when it opens.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class DonutOpacityTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_theme_page_slider_moves_the_preview_and_is_the_toolbar_pages_setting(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var saved = store.Settings.Donut.Opacity;
        try
        {
            store.Settings.Donut.Opacity = 0.96;
            var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>());
            vm.Navigate("theme");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            RenderTests.Pump();

            var page = window.GetVisualDescendants().OfType<ThemePage>().Single();
            var slider = page.FindControl<Slider>("DonutOpacitySlider")!;
            var preview = page.FindControl<DonutMenu>("PreviewDonut")!;
            Assert.Equal(96, slider.Value);
            Assert.Equal(0.96, preview.SliceOpacity, 3);
            // Labelled, in the page's language.
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == loc["theme.donut_opacity"] && t.IsEffectivelyVisible);

            slider.Value = 35;
            RenderTests.Pump();
            Assert.Equal(0.35, preview.SliceOpacity, 3);
            Assert.Equal(0.35, store.Settings.Donut.Opacity, 3);
            RenderTests.Save(window, $"page-theme-opacity-{language}.png");

            // The Donut toolbar page shows and edits the same value, and the Theme page reads it back.
            vm.Navigate("toolbar");
            RenderTests.Pump();
            var toolbar = Assert.IsType<ToolbarPageViewModel>(vm.CurrentPage);
            Assert.Equal(35, toolbar.SliceOpacity, 3);
            toolbar.SliceOpacity = 70;
            vm.Navigate("theme");
            RenderTests.Pump();
            Assert.Equal(70, Assert.IsType<ThemePageViewModel>(vm.CurrentPage).DonutOpacity);
            window.Close();

            // The donut takes it the next time it opens; a value below the range (an edited file) is raised to it.
            var radial = AppHost.Get<RadialViewModel>();
            radial.Load([@"C:\photos\a.jpg"], clickMode: true);
            Assert.Equal(0.7, radial.SliceOpacity, 3);
            store.Settings.Donut.Opacity = 0;
            radial.Load([@"C:\photos\a.jpg"], clickMode: true);
            Assert.Equal(DonutSettings.MinOpacity, radial.SliceOpacity, 3);
        }
        finally
        {
            store.Settings.Donut.Opacity = saved;
            store.SaveSettings();
        }
    }
}
