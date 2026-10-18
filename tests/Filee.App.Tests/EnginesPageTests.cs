// The Engines page: dragging a card changes the engine priority, the LibreOffice switches open from its card.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class EnginesPageTests
{
    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static Point Middle(Control card, Visual to) => card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), to)!.Value;

    [AvaloniaFact]
    public void Dragging_a_card_below_the_next_one_moves_it_there_and_saves_the_order()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var saved = store.Settings.EnginePriority.ToList();
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("engines");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        try
        {
            var page = window.GetVisualDescendants().OfType<EnginesPage>().Single();
            var engines = ((EnginesPageViewModel)page.DataContext!).Engines;
            var (first, second, third) = (engines[0], engines[1], engines[2]);
            var list = page.FindControl<ItemsControl>("EngineList")!;
            // The first card a little below the top of the page, away from the edges that scroll the page while dragging.
            var scroller = page.FindAncestorOfType<ScrollViewer>()!;
            var top = list.ContainerFromIndex(0)!.TranslatePoint(default, scroller)!.Value.Y;
            scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + top - 150);
            Pump();

            // Grab the first card and move past the middle of the second one: it moves there while dragging.
            var from = Middle(list.ContainerFromIndex(0)!, window);
            var past = Middle(list.ContainerFromIndex(1)!, window) + new Point(0, 12);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Point(0, 8));
            Assert.True(first.IsDragging);
            window.MouseMove(past);
            Pump();
            Assert.Equal([second, first, third], engines.Take(3));
            Assert.Equal(saved, store.Settings.EnginePriority); // saved on release

            window.MouseUp(past, MouseButton.Left);
            Pump();
            Assert.False(first.IsDragging);
            Assert.Equal(engines.Select(e => e.Converter.Id), store.Settings.EnginePriority);

            // A click without moving changes nothing.
            var order = engines.ToList();
            var at = Middle(list.ContainerFromIndex(2)!, window);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            Assert.Equal(order, engines);
        }
        finally
        {
            window.Close();
            store.Settings.EnginePriority = saved;
            store.SaveSettings();
        }
    }

    [AvaloniaFact]
    public void Only_LibreOffice_has_settings_and_they_change_the_saved_switches()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.LibreOfficeForSheets;
        var vm = new EnginesPageViewModel(store, AppHost.Get<Filee.Core.Conversion.ConverterCatalog>(), AppHost.Get<ILocalizer>(),
            AppHost.Get<EngineDownloadService>());
        try
        {
            Assert.Equal(["libreoffice"], vm.Packages.Where(p => p.HasSettings).Select(p => p.Package.Id));

            var options = new LibreOfficeOptionsWindow { DataContext = vm };
            options.Show();
            Pump();
            var sheets = options.GetVisualDescendants().OfType<ToggleSwitch>().ElementAt(2);
            sheets.IsChecked = !before;
            Assert.Equal(!before, store.Settings.LibreOfficeForSheets);
            options.Close();
        }
        finally
        {
            vm.LibreOfficeForSheets = before;
            vm.Dispose();
        }
    }
}
