// Renders real windows headlessly (Skia) to catch XAML / binding errors and to produce screenshots
// in bin/.../screenshots for visual review.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class RenderTests
{
    public static TheoryData<string, string> Pages => new()
    {
        { "home", "tr" }, { "general", "tr" }, { "triggers", "tr" }, { "toolbar", "tr" },
        { "presets", "tr" }, { "theme", "tr" }, { "engines", "tr" }, { "about", "tr" }, { "watch", "tr" },
        { "home", "ar" }, { "general", "ar" }, { "triggers", "ar" }, { "toolbar", "ar" },
        { "presets", "ar" }, { "theme", "ar" }, { "engines", "ar" }, { "about", "ar" }, { "watch", "ar" },
        { "home", "th" }, { "general", "th" }, { "triggers", "th" }, { "toolbar", "th" },
        { "presets", "th" }, { "theme", "th" }, { "engines", "th" }, { "about", "th" }, { "watch", "th" },
        { "home", "en" }, { "general", "en" }, { "triggers", "en" }, { "toolbar", "en" },
        { "presets", "en" }, { "theme", "en" }, { "engines", "en" }, { "about", "en" },
        { "general", "ko" }, { "toolbar", "ko" }, { "home", "ko" },
        { "general", "zh-CN" }, { "presets", "zh-CN" }, { "toolbar", "zh-CN" }, { "home", "zh-CN" },
        { "general", "zh-TW" }, { "presets", "zh-TW" }, { "toolbar", "zh-TW" }, { "home", "zh-TW" },
        { "triggers", "zh-TW" }, { "engines", "zh-TW" }, { "watch", "zh-TW" },
        { "general", "pl" }, { "toolbar", "pl" }, { "presets", "pl" }, { "home", "pl" },
        { "home", "es" }, { "general", "es" }, { "toolbar", "es" }, { "presets", "es" }, { "engines", "es" },
        { "home", "fr" }, { "general", "fr" }, { "toolbar", "fr" }, { "presets", "fr" }, { "engines", "fr" },
        { "home", "hi" }, { "general", "hi" }, { "toolbar", "hi" }, { "presets", "hi" }, { "engines", "hi" },
        { "home", "ja" }, { "general", "ja" }, { "triggers", "ja" }, { "toolbar", "ja" },
        { "presets", "ja" }, { "theme", "ja" }, { "engines", "ja" }, { "about", "ja" }, { "watch", "ja" },
        { "home", "ru" }, { "general", "ru" }, { "triggers", "ru" }, { "toolbar", "ru" }, { "presets", "ru" },
        { "theme", "ru" }, { "engines", "ru" }, { "about", "ru" }, { "watch", "ru" },
        { "home", "pt-PT" }, { "general", "pt-PT" }, { "triggers", "pt-PT" }, { "toolbar", "pt-PT" },
        { "presets", "pt-PT" }, { "theme", "pt-PT" }, { "engines", "pt-PT" }, { "about", "pt-PT" }, { "watch", "pt-PT" },
        { "home", "pt-BR" }, { "general", "pt-BR" }, { "triggers", "pt-BR" }, { "toolbar", "pt-BR" },
        { "presets", "pt-BR" }, { "theme", "pt-BR" }, { "engines", "pt-BR" }, { "about", "pt-BR" }, { "watch", "pt-BR" },
        { "home", "ne" }, { "general", "ne" }, { "toolbar", "ne" }, { "presets", "ne" }, { "engines", "ne" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public void Main_window_pages_render(string page, string language)
    {
        TestServices.EnsureInitialized(language);
        using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate(page);
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();

        Assert.NotNull(window.FindControl<ScrollViewer>("PageScroller"));
        Save(window, $"page-{page}-{language}.png");
        window.Close();
    }

    [AvaloniaFact]
    public void General_page_icon_rows_follow_platform_and_saved_values()
    {
        TestServices.EnsureInitialized("en");
        var directory = Directory.CreateTempSubdirectory("filee-icons-render-").FullName;
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            store.Settings.ShowInDock = false;
            store.Settings.ShowMenuBarIcon = true;
            store.SaveSettings();
            foreach (var supported in new[] { true, false })
            {
                var platform = new IconPlatform { SupportsDockVisibility = supported };
                using var vm = new GeneralPageViewModel(store, AppHost.Get<ILocalizer>(), platform,
                    NullLogger<GeneralPageViewModel>.Instance);
                var page = new GeneralPage { DataContext = vm };
                var window = new Window { Content = page, Width = 800, Height = 820 };
                try
                {
                    window.Show();
                    Pump();
                    var dock = page.FindControl<ToggleSwitch>("ShowInDockSwitch")!;
                    var menu = page.FindControl<ToggleSwitch>("ShowMenuBarIconSwitch")!;
                    Assert.Equal(supported, dock.IsVisible);
                    Assert.Equal(supported, menu.IsVisible);
                    Assert.False(dock.IsChecked);
                    Assert.True(menu.IsChecked);
                    Assert.Empty(platform.DockCalls);
                    if (supported)
                        Save(window, "page-general-icons-en.png");
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void Watch_folders_page_renders_a_rule(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var rule = new Filee.Core.Watching.WatchRule { Folder = @"C:\Scans\Inbox", PresetId = "to-pdf", Enabled = false };
        store.Settings.WatchFolders.Add(rule);
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("watch");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            Pump();

            var page = (Filee.App.ViewModels.Pages.WatchFoldersPageViewModel)vm.CurrentPage;
            var item = Assert.Single(page.Rules);
            Assert.Equal("to-pdf", item.Preset?.Value);
            Assert.False(string.IsNullOrWhiteSpace(item.Status));
            Assert.EndsWith(Path.Combine("converted", Path.ChangeExtension(AppHost.Get<Filee.Core.Localization.ILocalizer>()["watch.sample_file"], "pdf")),
                item.PreviewExample); // where a converted file goes and what it is called
            Assert.False(item.ShowOptions); // the optional settings stay folded until used
            Save(window, $"page-watch-{language}.png");
            window.Close();
        }
        finally
        {
            store.Settings.WatchFolders.Remove(rule);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    public void Watch_folder_card_shows_recent_results_and_retries_in_the_folder(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var conversions = AppHost.Get<ConversionService>();
        var folder = Directory.CreateTempSubdirectory("filee-watch-").FullName;
        var broken = Path.Combine(folder, "scan-0412.heic");
        File.WriteAllBytes(broken, [0]);
        var rule = new Filee.Core.Watching.WatchRule { Folder = folder, PresetId = "to-pdf", Enabled = false };
        store.Settings.WatchFolders.Add(rule);
        store.AddHistory(new Filee.Core.History.HistoryEntry
        {
            WatchRuleId = rule.Id,
            FinishedAt = DateTime.Now,
            Sources = [Path.Combine(folder, "a.jpg"), Path.Combine(folder, "b.jpg"), broken],
            Failures = [new() { Source = broken, ErrorKey = "error.conversion_failed", ErrorDetail = "HEIC decoder: unexpected end of file" }],
            Preset = new Filee.Core.Presets.Preset { TargetFormat = "pdf" },
        });
        var retried = new List<(string Rule, IReadOnlyList<string> Files)>();
        var previousRetry = conversions.WatchRetry;
        conversions.WatchRetry = (id, files, _) =>
        {
            retried.Add((id, files));
            return Task.CompletedTask;
        };
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("watch");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 1000 };
            window.Show();
            Pump();

            var item = Assert.Single(((Filee.App.ViewModels.Pages.WatchFoldersPageViewModel)vm.CurrentPage).Rules);
            Assert.True(item.HasResults);
            Assert.Equal("scan-0412.heic", Assert.Single(item.ResultFailures).Name);
            Assert.True(item.CanRetryResults);
            item.ToggleResultFailuresCommand.Execute(null);
            Pump();
            Save(window, $"page-watch-results-{language}.png");

            // Retrying goes through the watch folder (which then moves originals when it does that).
            item.RetryResultsCommand.Execute(null);
            var (ruleId, files) = Assert.Single(retried);
            Assert.Equal(rule.Id, ruleId);
            Assert.Equal([broken], files);
            window.Close();
        }
        finally
        {
            conversions.WatchRetry = previousRetry;
            store.Settings.WatchFolders.Remove(rule);
            store.SaveSettings();
            store.ClearHistory();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ru")]
    [InlineData("ar")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Watch_folder_options_rename_and_filter_with_a_preview(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var folder = Directory.CreateTempSubdirectory("filee-watch-").FullName;
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [0x41]);
        File.WriteAllBytes(Path.Combine(folder, "IMG_0412.jpg"), [0xFF, 0xD8]);
        var rule = new Filee.Core.Watching.WatchRule
        {
            Folder = folder,
            PresetId = "to-pdf",
            Enabled = false,
            Include = ["*.jpg"],
            FileNamePattern = "{name}_scan",
        };
        store.Settings.WatchFolders.Add(rule);
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("watch");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 1500 };
            window.Show();
            Pump();

            var item = Assert.Single(((Filee.App.ViewModels.Pages.WatchFoldersPageViewModel)vm.CurrentPage).Rules);
            Assert.True(item.ShowOptions); // options in use are shown

            // Typed patterns become chips: an extension turns into *.ext, a broken expression is not added.
            item.AddIncludeCommand.Execute("heic");
            Assert.Equal(["*.jpg", "*.heic"], rule.Include);
            item.AddIncludeCommand.Execute("/IMG_(/");
            Assert.NotNull(item.IncludeError);
            Assert.Equal("/IMG_(/", item.IncludeText);
            item.IncludeText = "";
            Assert.NotNull(item.IncludeMatches); // IMG_0412.jpg matches, notes.txt doesn't

            // The rename window works on copies: a ready-made replacement becomes a rule, the preview follows.
            var tool = item.CreateRenameTool();
            Assert.Equal("{name}_scan", tool.NamePattern); // the base name is edited here too
            tool.NamePattern = "";
            tool.InsertToken("date"); // an empty name starts from the original one
            Assert.Equal("{name}_{date}", tool.NamePattern);
            tool.NamePattern = "{name}_scan";
            tool.Recipes[0].AddCommand.Execute(null); // change IMG_ to Photo_
            Assert.Empty(rule.Renames); // nothing changes before saving
            Assert.Equal(new Filee.App.ViewModels.RenamePreview("IMG_0412.jpg", "Photo_0412_scan.pdf"), Assert.Single(tool.Previews));
            tool.CustomName = "img_0815.heic";
            Assert.Equal("Photo_0815_scan.pdf", tool.CustomResult);

            // Plain text is never "wrong"; only the advanced kind checks its expression.
            tool.AddStepCommand.Execute(null);
            var extra = tool.Steps[1];
            extra.Find = "(";
            Assert.Null(extra.Error);
            extra.Kind = tool.Kinds.First(k => k.Value == Filee.Core.Presets.RenameKind.Regex);
            Assert.NotNull(extra.Error);
            Assert.False(tool.IsValid);
            extra.Kind = tool.Kinds.First(k => k.Value == Filee.Core.Presets.RenameKind.Suffix);
            Assert.True(extra.ShowReplaceOnly);
            extra.Replace = "_v2";
            Assert.True(tool.IsValid);
            Assert.Equal("Photo_0412_scan_v2.pdf", tool.Previews[0].After);
            extra.RemoveCommand.Execute(null);
            var dialog = new Filee.App.Views.RenameToolWindow { DataContext = tool };
            dialog.Show();
            Pump();
            Save(dialog, $"window-rename-{language}.png");
            dialog.Close();

            item.SetNaming(tool.NamePattern, tool.Result); // saved
            Assert.Equal("IMG_", Assert.Single(rule.Renames).Find);
            // A file already in the folder that the patterns let through, renamed by the name rule and the rule.
            Assert.Equal($"IMG_0412.jpg → {Path.Combine("converted", "Photo_0412_scan.pdf")}", item.PreviewExample);
            Pump();
            Save(window, $"page-watch-options-{language}.png");
            window.Close();
        }
        finally
        {
            store.Settings.WatchFolders.Remove(rule);
            store.SaveSettings(); // editing the rule saved it: the next test's application would load it again
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void Home_page_lists_failed_files_with_a_retry_button(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var folder = Directory.CreateTempSubdirectory("filee-home-").FullName;
        var scan = Path.Combine(folder, "scan-0412.heic");
        File.WriteAllBytes(scan, [0]);
        var entry = new Filee.Core.History.HistoryEntry
        {
            FinishedAt = DateTime.Now,
            PresetName = "PDF",
            State = Filee.Core.Conversion.JobState.CompletedWithErrors,
            Sources = [Path.Combine(folder, "report.docx"), scan, Path.Combine(folder, "notes.xyz")],
            Failures =
            [
                new() { Source = scan, ErrorKey = "error.conversion_failed", ErrorDetail = "HEIC decoder: unexpected end of file" },
                new() { Source = Path.Combine(folder, "notes.xyz"), ErrorKey = "error.unsupported_source", ErrorDetail = "xyz" },
            ],
            Preset = new Filee.Core.Presets.Preset { TargetFormat = "pdf" },
        };
        store.AddHistory(entry);
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            Pump();

            var item = ((Filee.App.ViewModels.Pages.HomePageViewModel)vm.CurrentPage).History[0];
            Assert.True(item.CanRetry);
            item.ToggleFailuresCommand.Execute(null);
            Pump();
            Save(window, $"page-home-failed-{language}.png");
            window.Close();
        }
        finally
        {
            store.ClearHistory(); // also on disk: the next test's application loads the history again
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    [InlineData("pl")]
    [InlineData("hi")]
    [InlineData("ru")]
    [InlineData("ar")]
    [InlineData("pt-BR")]
    [InlineData("fr")]
    public void Home_page_search_finds_old_conversions_in_a_long_history(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        store.ClearHistory();
        // A full history (newest first), with the report far below what the page lists without a search.
        var entries = Enumerable.Range(0, store.Settings.EffectiveHistoryLimit).Select(i => new Filee.Core.History.HistoryEntry
        {
            FinishedAt = DateTime.Now.AddMinutes(-i),
            PresetName = i == 150 ? "PDF" : "PNG",
            State = Filee.Core.Conversion.JobState.Completed,
            Sources = i == 150 ? [@"C:\Work\Annual report.docx", @"C:\Work\notes.txt"] : [$@"C:\Photos\IMG_{i:0000}.heic"],
        });
        store.History.AddRange(entries);
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            Pump();

            var page = (HomePageViewModel)vm.CurrentPage;
            Assert.Equal(HomePageViewModel.Shown, page.History.Count);
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("search"));
            Assert.True(search.IsEffectivelyVisible);
            Assert.Equal(loc["common.search"], search.PlaceholderText);

            page.Search = " REPORT "; // any part of a source file name, ignoring case and spaces around
            Pump();
            var found = Assert.Single(page.History);
            Assert.Equal("PDF", found.Title);
            Assert.False(page.NoMatches);
            Save(window, $"page-home-search-{language}.png");

            page.Search = "PNG"; // the preset: the newest matches, no more than without a search
            Assert.Equal(HomePageViewModel.Shown, page.History.Count);
            Assert.All(page.History, item => Assert.Equal("PNG", item.Title));

            page.Search = "zzz";
            Pump();
            Assert.Empty(page.History);
            Assert.True(page.NoMatches);
            Assert.False(page.IsEmpty);
            Save(window, $"page-home-search-none-{language}.png");

            page.Search = "";
            Assert.Equal(HomePageViewModel.Shown, page.History.Count);
            window.Close();
        }
        finally
        {
            store.ClearHistory(); // also on disk: the next test's application loads the history again
        }
    }

    [AvaloniaFact]
    public void A_new_preset_shows_in_the_list_whatever_was_searched()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("presets");
        var page = (PresetsPageViewModel)vm.CurrentPage;
        page.Search = "zzz";
        Assert.Empty(page.VisiblePresets);

        page.AddCommand.Execute(null);
        try
        {
            Assert.Equal("", page.Search);
            Assert.Contains(page.SelectedPreset!, page.VisiblePresets);
        }
        finally
        {
            store.Presets.Remove(page.SelectedPreset!.Preset);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void General_page_keeps_the_history_limit_within_its_range()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var before = store.Settings.HistoryLimit;
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("general");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            Pump();

            var page = (GeneralPageViewModel)vm.CurrentPage;
            Assert.Equal(200, page.HistoryLimit);
            var limit = window.GetVisualDescendants().OfType<NumericUpDown>().Single();
            Assert.True(limit.IsEffectivelyVisible);

            page.HistoryLimit = 500;
            Assert.Equal(500, store.Settings.HistoryLimit);
            page.HistoryLimit = 5000;
            Assert.Equal(Filee.Core.Settings.AppSettings.MaxHistoryLimit, store.Settings.HistoryLimit);

            page.NoHistory = true; // nothing is recorded: nothing to limit either
            Pump();
            Assert.False(limit.IsEffectivelyVisible);
            page.NoHistory = false;
            window.Close();
        }
        finally
        {
            store.Settings.HistoryLimit = before;
            store.SaveSettings();
        }
    }

    [AvaloniaFact]
    public void Engines_page_shows_versions_descriptions_and_download_speed()
    {
        TestServices.EnsureInitialized("ko");
        var downloads = AppHost.Get<EngineDownloadService>();
        var package = downloads.Packages[0];
        var before = (package.Status, package.Progress, package.ProgressDetail);
        package.Status = EnginePackageStatus.Downloading;
        package.Progress = 42;
        package.ProgressDetail = "176 MB / 422 MB · 8.4 MB/s · 30초 남음";
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("engines");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 1400 };
            window.Show();
            Pump();

            var engines = ((Filee.App.ViewModels.Pages.EnginesPageViewModel)vm.CurrentPage).Engines;
            Assert.All(engines.Where(e => e.IsAvailable), e => Assert.False(string.IsNullOrWhiteSpace(e.Version)));
            Assert.All(engines, e => Assert.False(string.IsNullOrWhiteSpace(e.Description)));
            Save(window, "page-engines-downloading-ko.png");
            window.Close();
        }
        finally
        {
            (package.Status, package.Progress, package.ProgressDetail) = before;
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("hi")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void Reset_to_default_sits_beside_delete_for_a_built_in_profile_only(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("toolbar");
            var page = (ToolbarPageViewModel)vm.CurrentPage!;
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            page.SelectedProfile = page.Profiles.Single(p => p.Profile.Id == "images");
            Pump();

            var actions = window.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "ProfileActions");
            var reset = (Button)actions.Children[0];
            var delete = (Button)actions.Children[1];
            Assert.Same(page.ResetProfileCommand, reset.Command);
            Assert.Same(page.DeleteProfileCommand, delete.Command);
            Assert.True(reset.IsVisible && delete.IsVisible);
            // On one row, reset first, both inside the profile card.
            var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ProfileCard");
            var resetBox = Avalonia.VisualExtensions.TranslatePoint(reset, default, card)!.Value;
            var deleteBox = Avalonia.VisualExtensions.TranslatePoint(delete, default, card)!.Value;
            Assert.Equal(resetBox.Y, deleteBox.Y);
            Assert.True(resetBox.X + reset.Bounds.Width < deleteBox.X);
            Assert.True(resetBox.X >= 0 && deleteBox.X + delete.Bounds.Width <= card.Bounds.Width);
            actions.BringIntoView();
            Pump();
            Save(window, $"toolbar-reset-profile-{language}.png");

            // A profile the user made has no default: only delete.
            page.AddProfileCommand.Execute(null);
            Pump();
            Assert.False(reset.IsVisible);
            Assert.True(delete.IsVisible);
            window.Close();
        }
        finally
        {
            store.Profiles.Clear();
            store.Profiles.AddRange(profiles);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void Dragging_a_preset_opens_a_gap_with_a_preview_slice()
    {
        TestServices.EnsureInitialized("ko");
        using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("toolbar");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        var donut = window.GetVisualDescendants().OfType<DonutMenu>().Single(d => d.IsEditMode);
        var count = donut.Items!.Count;

        // A chip over the third slice: the slices from there on slide clockwise and a preview takes slot 2.
        donut.SetDropPreview(2, "TIFF 300dpi");
        for (var i = 0; i < 120; i++)
            donut.Advance(1 / 60.0);
        donut.InvalidateVisual();
        Pump();
        Assert.Equal(2, donut.SlotAt(DonutPoint(donut, 2, count + 1), count + 1));
        Save(window, "toolbar-drop-preview-ko.png");

        // Moving the first slice to slot 3: it leaves its place, the preview shows it at the new one.
        donut.SetDropPreview(3, donut.Items[0].Label, removed: 0);
        for (var i = 0; i < 120; i++)
            donut.Advance(1 / 60.0);
        donut.InvalidateVisual();
        Pump();
        Save(window, "toolbar-move-preview-ko.png");

        donut.SetDropPreview(-1);
        window.Close();
    }

    /// <summary>
    /// The built-in "Rotate" and "Flip" groups put on a full image donut (as a user would; they are on no donut by
    /// default): their presets' names fit their ring slices, in every language.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("en")]
    [InlineData("zh-CN")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void The_rotate_and_flip_groups_fan_out_names_that_fit(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var before = images.PresetIds.ToList();
        try
        {
            images.PresetIds.AddRange(["rotate", "flip"]);
            var vm = AppHost.Get<RadialViewModel>();
            vm.Load([@"C:\photos\a.jpg"], clickMode: true);
            vm.FitToThisDonut();
            var rotate = vm.Items.Count - 2;
            Assert.Equal(3, vm.Items[rotate].Children!.Count);
            Assert.All(vm.Items[rotate].Children!, item => Assert.True(item.IsEnabled, item.Label));
            // Short names on the ring ("Rotate" → "Left"), the full one ("Rotate left") everywhere else.
            var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
            var left = store.FindPreset("rotate-left")!;
            Assert.Equal(loc["preset.rotate_left.short"], vm.Items[rotate].Children![0].Label);
            Assert.NotEqual(loc.DisplayName(left), vm.Items[rotate].Children![0].Label);

            var window = new RadialWindow { DataContext = vm };
            window.Show();
            var donut = window.DonutControl;
            donut.SetExternalHit(new DonutHit(DonutHitKind.Segment, rotate));
            donut.ExpandPendingGroup();
            donut.SetExternalHit(new DonutHit(DonutHitKind.SubSegment, rotate, 1));
            vm.ShowHint(rotate, donut.ActiveSubIndex, centerHot: false);
            Pump();
            Save(window, $"donut-rotate-{language}.png");
            window.Close();
        }
        finally
        {
            images.PresetIds.Clear();
            images.PresetIds.AddRange(before);
        }
    }

    /// <summary>
    /// A group of three formats on the image donut: hovering it fans PNG / JPG / WEBP out outside it, and the slice
    /// under the pointer there is what a drop or click converts with.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void A_group_slice_fans_out_its_presets(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var before = images.PresetIds.ToList();
        var group = new Filee.Core.Presets.Preset
        {
            Id = "render-group",
            Name = language == "ko" ? "형식" : "Formats",
            Kind = Filee.Core.Presets.PresetKind.Group,
            SubPresetIds = ["to-png", "to-jpg", "to-webp"],
        };
        try
        {
            store.Presets.Add(group);
            images.PresetIds.Add(group.Id);
            var vm = AppHost.Get<RadialViewModel>();
            vm.Load([@"C:\photos\a.jpg"], clickMode: true);
            vm.FitToThisDonut();
            Assert.True(vm.ReserveSubRing);
            var index = vm.Items.Count - 1;
            Assert.Equal(3, vm.Items[index].Children!.Count);
            Assert.Null(vm.PresetAt(index, -1)); // the group itself converts nothing
            Assert.Equal("to-jpg", vm.PresetAt(index, 1)!.Id);

            var window = new RadialWindow { DataContext = vm };
            window.Show();
            var donut = window.DonutControl;
            donut.SetExternalHit(new DonutHit(DonutHitKind.Segment, index));
            donut.ExpandPendingGroup(); // as after resting on it
            donut.SetExternalHit(new DonutHit(DonutHitKind.SubSegment, index, 1));
            vm.ShowHint(index, donut.ActiveSubIndex, centerHot: false);
            Pump();

            var n = vm.Items.Count;
            var center = new Avalonia.Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
            var onRing = donut.SubSliceCenter(index, 1);
            Assert.Equal(new DonutHit(DonutHitKind.SubSegment, index, 1), donut.HitTest(onRing));
            // The gap before the ring keeps the group; a neighbour's slice, outer half or inner, is that neighbour (a click
            // or drop there converts with it; DonutGroupTests checks the ring stays open over its outer half).
            var neighbour = DonutGeometry.MidAngle(index, n) - DonutGeometry.Span(n) * 0.6;
            Assert.Equal(new DonutHit(DonutHitKind.Segment, index),
                donut.HitTest(DonutGeometry.PointAt(center, vm.OuterRadius + 10, DonutGeometry.MidAngle(index, n))));
            Assert.Equal(new DonutHit(DonutHitKind.Segment, index - 1), donut.HitTest(DonutGeometry.PointAt(center, vm.OuterRadius - 8, neighbour)));
            Assert.Equal(new DonutHit(DonutHitKind.Segment, index - 1),
                donut.HitTest(DonutGeometry.PointAt(center, vm.OuterRadius * vm.HoleRatio + 6, neighbour)));
            Assert.Contains("JPG", vm.CenterSubtitle);
            Save(window, $"donut-group-{language}.png");
            window.Close();
        }
        finally
        {
            images.PresetIds.Clear();
            images.PresetIds.AddRange(before);
            store.Presets.Remove(group);
        }
    }

    /// <summary>
    /// The theme's softness rounds the donut's slices (and a group's ring) too, live while it is open; the corners it
    /// cuts away still belong to their slice for clicks and drops.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Donut_slices_follow_the_softness(bool dark)
    {
        TestServices.EnsureInitialized("en");
        var theme = AppHost.Get<ThemeService>();
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var before = images.PresetIds.ToList();
        var group = new Filee.Core.Presets.Preset
        {
            Id = "render-soft-group",
            Name = "Formats",
            Kind = Filee.Core.Presets.PresetKind.Group,
            SubPresetIds = ["to-png", "to-jpg", "to-webp"],
        };
        var mode = dark ? ThemeMode.Dark : ThemeMode.Light;
        var suffix = dark ? "dark" : "light";
        try
        {
            store.Presets.Add(group);
            images.PresetIds.Add(group.Id);
            theme.Apply(new ThemeSettings { Mode = mode, CornerRadius = 0 }, "en");
            var vm = AppHost.Get<RadialViewModel>();
            vm.Load([@"C:\photos\a.jpg", @"C:\photos\b.jpg"], clickMode: true);
            vm.FitToThisDonut();
            var window = new RadialWindow { DataContext = vm };
            window.Show();
            var donut = window.DonutControl;
            donut.SetExternalHighlight(1);
            Pump();
            Assert.Equal(0, donut.SliceCornerRadius);
            Save(window, $"donut-softness-0-{suffix}.png");

            // Changing the setting while the donut is open rounds it at once.
            theme.Apply(new ThemeSettings { Mode = mode, CornerRadius = 28 }, "en");
            Pump();
            Assert.Equal(14, donut.SliceCornerRadius);
            Save(window, $"donut-softness-28-{suffix}.png");

            // The sharp corner of slice 2, cut away now, still picks slice 2.
            var n = vm.Items.Count;
            var center = new Avalonia.Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
            var inner = vm.OuterRadius * vm.HoleRatio;
            var corner = DonutGeometry.Wedge(center, inner, vm.OuterRadius, DonutGeometry.MidAngle(2, n), DonutGeometry.Span(n), 3).Start;
            var inside = center + (corner - center) * 0.995;
            Assert.Equal(new DonutHit(DonutHitKind.Segment, 2), donut.HitTest(inside));

            var index = n - 1;
            donut.SetExternalHit(new DonutHit(DonutHitKind.Segment, index));
            donut.ExpandPendingGroup();
            donut.SetExternalHit(new DonutHit(DonutHitKind.SubSegment, index, 1));
            vm.ShowHint(index, donut.ActiveSubIndex, centerHot: false);
            Pump();
            Assert.Equal(new DonutHit(DonutHitKind.SubSegment, index, 1), donut.HitTest(donut.SubSliceCenter(index, 1)));
            Save(window, $"donut-softness-28-group-{suffix}.png");
            window.Close();
        }
        finally
        {
            images.PresetIds.Clear();
            images.PresetIds.AddRange(before);
            store.Presets.Remove(group);
            theme.Apply(new ThemeSettings(), "en");
        }
    }

    /// <summary>The geospatial donut opens for Shapefiles with its six slices, rounded by the softness like the others.</summary>
    [AvaloniaFact]
    public void Geospatial_donut_opens_for_shapefiles_with_rounded_slices()
    {
        TestServices.EnsureInitialized("en");
        var theme = AppHost.Get<ThemeService>();
        try
        {
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Light, CornerRadius = 28 }, "en");
            var vm = AppHost.Get<RadialViewModel>();
            vm.Load([@"C:\gis\roads.shp", @"C:\gis\parcels.shp"], clickMode: true);
            vm.FitToThisDonut();
            Assert.Equal(["to-geojson", "to-shp", "to-kml", "to-csv", "to-gpx", "to-kmz"], vm.Presets.Select(p => p.Id));
            Assert.All(vm.Items, item => Assert.True(item.IsEnabled));
            var window = new RadialWindow { DataContext = vm };
            window.Show();
            var donut = window.DonutControl;
            donut.SetExternalHighlight(1);
            Pump();
            Assert.Equal(14, donut.SliceCornerRadius);

            // A corner the rounding cut away still picks its slice.
            var n = vm.Items.Count;
            var center = new Avalonia.Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
            var inner = vm.OuterRadius * vm.HoleRatio;
            var corner = DonutGeometry.Wedge(center, inner, vm.OuterRadius, DonutGeometry.MidAngle(4, n), DonutGeometry.Span(n), 3).Start;
            Assert.Equal(new DonutHit(DonutHitKind.Segment, 4), donut.HitTest(center + (corner - center) * 0.995));
            Save(window, "donut-geospatial-soft.png");
            window.Close();
        }
        finally
        {
            theme.Apply(new ThemeSettings(), "en");
        }
    }

    /// <summary>The middle of slot <paramref name="slot"/> of a donut with <paramref name="slots"/> slices.</summary>
    private static Avalonia.Point DonutPoint(DonutMenu donut, int slot, int slots)
    {
        var center = new Avalonia.Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
        var radius = donut.OuterRadius * (1 + donut.HoleRatio) / 2;
        return DonutGeometry.PointAt(center, radius, DonutGeometry.MidAngle(slot, slots));
    }

    [AvaloniaTheory]
    [InlineData("ko", "JPG 3개")]
    [InlineData("ru", "3 × JPG")]
    [InlineData("ar", "3 × JPG")]
    public void Donut_toolbar_renders_for_images(string language, string title)
    {
        TestServices.EnsureInitialized(language);
        var files = new[] { @"C:\photos\a.jpg", @"C:\photos\b.jpg", @"C:\photos\c.jpg" };
        var vm = AppHost.Get<RadialViewModel>();
        vm.Load(files, clickMode: true);
        var window = new RadialWindow { DataContext = vm };
        window.Show();
        window.DonutControl.SetExternalHighlight(1);
        Pump();

        Assert.Equal(10, vm.Items.Count); // with "Trace to SVG" and "Line art to SVG"
        Assert.Equal(title, vm.CenterTitle);
        Save(window, $"donut-images-{language}.png");
        window.Close();
    }

    [AvaloniaFact]
    public void LibreOffice_options_window_renders()
    {
        TestServices.EnsureInitialized("ko");
        using var vm = new Filee.App.ViewModels.Pages.EnginesPageViewModel(AppHost.Get<Filee.Core.Settings.UserDataStore>(),
            AppHost.Get<Filee.Core.Conversion.ConverterCatalog>(), AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<EngineDownloadService>());
        var window = new Filee.App.Views.LibreOfficeOptionsWindow { DataContext = vm };
        window.Show();
        Pump();
        Save(window, "window-libreoffice-options-ko.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ru")]
    [InlineData("ar")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Sponsor_window_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var window = Filee.App.Views.SponsorWindow.Create(AppHost.Get<Filee.Core.Localization.ILocalizer>());
        window.Show();
        Pump();
        Save(window, $"window-sponsor-{language}.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Sponsor_window_with_supporters_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var window = Filee.App.Views.SponsorWindow.Create(AppHost.Get<Filee.Core.Localization.ILocalizer>());
        var view = window.Supporters;
        view.Show(new SupporterList(
            [new Supporter("Acme", "https://acme.example", "acme.png"), new Supporter("Globex", "https://globex.example")],
            [new("Kim"), new("Lee"), new("Park")]), _ => Logo());
        window.Show();
        Pump();
        CheckAcknowledgement(window);
        Save(window, $"window-sponsor-supporters-{language}.png");
        window.Close();

        using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("about");
        var main = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        main.Show();
        Pump();
        var about = main.GetVisualDescendants().OfType<Filee.App.Views.Pages.AboutPage>().Single();
        about.Supporters.Show(new SupporterList(
            [new Supporter("Acme", "https://acme.example", "acme.png"), new Supporter("Globex", "https://globex.example")],
            [new("Kim"), new("Lee"), new("Park")]), _ => Logo());
        Pump();
        CheckAcknowledgement(main);
        Save(main, $"page-about-supporters-{language}.png");
        main.Close();

        static void CheckAcknowledgement(Window target)
        {
            var thanks = AppHost.Get<Filee.Core.Localization.ILocalizer>()["sponsor.thanks"];
            var labels = target.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Text == thanks).ToArray();
            Assert.NotEmpty(labels);
            Assert.All(labels, text => Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1 &&
                                                    text.TextLayout.Height <= text.Bounds.Height + 1,
                $"Acknowledgement is clipped: {thanks}"));
        }

        // A stand-in logo: a wide coloured mark like a company's.
        static Avalonia.Media.Imaging.Bitmap Logo()
        {
            var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(120, 32));
            using (var context = bitmap.CreateDrawingContext())
            {
                context.DrawRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3B6FD8")), null, new Avalonia.Rect(0, 4, 24, 24), 6, 6);
                context.DrawRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7A8699")), null, new Avalonia.Rect(32, 10, 84, 12), 3, 3);
            }
            return bitmap;
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ru")]
    [InlineData("ar")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void First_run_engine_setup_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<Filee.Core.Localization.ILocalizer>());
        var window = new EngineSetupWindow { DataContext = vm };
        window.Show();
        Pump();

        // Everything not installed is offered, nothing is ticked: installing needs a choice first.
        Assert.Contains(vm.Packages, p => !p.IsInstalled && p.CanInstall);
        Assert.DoesNotContain(vm.Packages, p => p.Selected);
        Assert.False(vm.InstallCommand.CanExecute(null));
        vm.Packages.First(p => p.CanInstall && !p.IsInstalled).Selected = true;
        Assert.True(vm.InstallCommand.CanExecute(null));
        vm.Packages.First(p => p.Selected).Selected = false;
        Save(window, $"engine-setup-{language}.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Engine_setup_on_a_short_screen_scrolls_the_list_and_keeps_its_buttons(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<Filee.Core.Localization.ILocalizer>());
        var window = new EngineSetupWindow { DataContext = vm };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        Pump();
        var fullHeight = window.ClientSize.Height;

        // A screen with room for the whole list leaves the window as it was.
        window.FitTo(new Avalonia.PixelRect(0, 0, 1920, (int)fullHeight + 200), 1);
        Pump();
        Assert.Equal(fullHeight, window.ClientSize.Height);

        // 1366 × 768 at 150 % less the taskbar (#370): 480 high in window units, shorter than the list.
        window.FitTo(new Avalonia.PixelRect(0, 0, 1366, 720), 1.5);
        Pump();
        Assert.True(fullHeight > 480, $"The list is {fullHeight} high, too short to test this.");
        Assert.InRange(window.ClientSize.Height, 200, 480);
        Assert.InRange(window.Position.Y, 0, 720 - window.ClientSize.Height * 1.5);
        var scroller = window.FindControl<ScrollViewer>("PackageScroller")!;
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
        foreach (var name in new[] { "CloseButton", "InstallButton" })
        {
            var button = window.FindControl<Button>(name)!;
            var bottom = Avalonia.VisualExtensions.TranslatePoint(button, new Avalonia.Point(0, button.Bounds.Height), window)!.Value.Y;
            Assert.InRange(bottom, 0, window.ClientSize.Height);
        }
        Save(window, $"engine-setup-short-screen-{language}.png");

        // Esc closes it like Cancel / Close in the other dialogs.
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void Donut_toolbar_waits_for_files_in_drag_mode()
    {
        TestServices.EnsureInitialized("en");
        var vm = AppHost.Get<RadialViewModel>();
        vm.ResetForDrag();
        var window = new RadialWindow { DataContext = vm };
        window.Show();
        Pump();

        Assert.Empty(vm.Items);
        // Nothing shows until files are dragged in (a Ctrl + drag without files must not open anything).
        Assert.Equal(0, vm.DonutOpacity);
        Save(window, "donut-drag-waiting.png");
        vm.Load([@"C:\photos\a.jpg"], clickMode: false);
        Assert.Equal(1, vm.DonutOpacity);
        window.Close();
    }

    [AvaloniaFact]
    public void Dark_theme_with_custom_accent_renders()
    {
        TestServices.EnsureInitialized("en");
        var theme = new ThemeSettings { Mode = ThemeMode.Dark, Accent = "#1D9E75", CornerRadius = 22 };
        AppHost.Get<ThemeService>().Apply(theme, "en");
        using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("toolbar");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();

        Save(window, "page-toolbar-dark.png");
        window.Close();
        AppHost.Get<ThemeService>().Apply(new ThemeSettings(), "en");
    }

    [AvaloniaFact]
    public void Custom_theme_colours_replace_the_built_in_ones_and_go_away_with_the_file()
    {
        TestServices.EnsureInitialized("en");
        var service = AppHost.Get<ThemeService>();
        var dark = new ThemeSettings { Mode = ThemeMode.Dark };
        var custom = new CustomTheme
        {
            Dark = { ["page"] = "#101820", ["surface"] = "#18222C", ["surfaceAlt"] = "#22303C", ["text"] = "#E8E4D8" },
        };
        try
        {
            service.Apply(dark, "en", custom);
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("theme");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            Pump();

            Assert.Equal(Avalonia.Media.Color.Parse("#101820"), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(window.Background).Color);
            Assert.Equal(Avalonia.Media.Color.Parse("#E8E4D8"), BrushColor(window, "TextPrimaryBrush"));
            // Colours the file does not set, and the other variant, keep Filee's own.
            Assert.Equal(Avalonia.Media.Color.Parse("#AEACA4"), BrushColor(window, "TextSecondaryBrush"));
            Assert.Equal(Avalonia.Media.Color.Parse("#F6F5F1"), BrushColor(window, "PageBrush", Avalonia.Styling.ThemeVariant.Light));
            Save(window, "page-theme-custom.png");

            // Applying without a theme file (deleted, or emptied) restores the built-in colours on the open window.
            service.Apply(dark, "en");
            Pump();
            Assert.Equal(Avalonia.Media.Color.Parse("#1B1B19"), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(window.Background).Color);
            window.Close();

            service.Apply(dark, "en", custom);
            var radial = AppHost.Get<RadialViewModel>();
            radial.Load([@"C:\photos\a.jpg"], clickMode: true);
            var donut = new RadialWindow { DataContext = radial };
            donut.Show();
            donut.DonutControl.SetExternalHighlight(1);
            Pump();
            Assert.Equal(Avalonia.Media.Color.Parse("#18222C"), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(donut.DonutControl.SliceBrush).Color);
            Save(donut, "donut-custom-theme.png");
            donut.Close();
        }
        finally
        {
            service.Apply(new ThemeSettings(), "en");
        }
    }

    [Fact]
    public void Every_custom_theme_name_maps_to_a_brush()
    {
        Assert.Equal(CustomTheme.Keys.Order(), ThemeService.CustomBrushKeys.Keys.Order());
    }

    private static Avalonia.Media.Color BrushColor(Window window, string key, Avalonia.Styling.ThemeVariant? variant = null)
    {
        Assert.True(window.TryFindResource(key, variant ?? window.ActualThemeVariant, out var value), key);
        return Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(value).Color;
    }

    [AvaloniaFact]
    public void Preset_popup_renders()
    {
        TestServices.EnsureInitialized("ko");
        var preset = AppHost.Get<UserDataStore>().FindPreset("jpg-70")!;
        var window = new PresetEditorWindow { DataContext = new PresetEditorViewModel(preset, AppHost.Get<Filee.Core.Localization.ILocalizer>()) };
        window.Show();
        Pump();

        Save(window, "preset-popup-ko.png");
        window.Close();
    }

    [AvaloniaFact]
    public void Preset_editor_shows_and_saves_rotation_and_flip()
    {
        TestServices.EnsureInitialized("ko");
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "rotate-left");
        var vm = new PresetEditorViewModel(preset, AppHost.Get<Filee.Core.Localization.ILocalizer>());
        Assert.Equal(Filee.Core.Presets.ImageRotation.Left90, vm.Rotation.Value); // not a hidden option any more
        Assert.Equal(Filee.Core.Presets.ImageFlip.None, vm.Flip.Value);
        var window = new PresetEditorWindow { DataContext = vm, Height = 900 };
        window.Show();
        Pump();
        Save(window, "preset-rotate-ko.png");
        window.Close();

        // Rotate and convert in one: turn left, mirror, and make it a JPG.
        vm.Flip = vm.Flips.Single(f => f.Value == Filee.Core.Presets.ImageFlip.Horizontal);
        vm.Target = vm.Targets.Single(t => t.Value == "jpg");
        Assert.True(vm.Apply());

        Assert.Equal(Filee.Core.Presets.ImageRotation.Left90, preset.Image.Rotation);
        Assert.Equal(Filee.Core.Presets.ImageFlip.Horizontal, preset.Image.Flip);
        Assert.Equal("jpg", preset.TargetFormat);
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ru", false)]
    [InlineData("ar", true)]
    public void Preset_editor_offers_the_subfolder_only_for_several_files_with_a_subfolder(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        // PDF → page images saves into a folder per document.
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "pdf-split");
        var vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.ShowSubfolder);
        Assert.False(vm.SubfolderOnlyForSeveralFiles);

        vm.SubfolderOnlyForSeveralFiles = true;
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        try
        {
            var window = new PresetEditorWindow { DataContext = vm, Height = 1400 };
            window.Show();
            Pump();
            Save(window, $"preset-subfolder-{language}{(dark ? "-dark" : "")}.png");
            window.Close();
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
        Assert.True(vm.Apply());
        Assert.True(preset.Output.SubfolderOnlyForSeveralFiles);

        // Hidden with another location, and not kept on behind it.
        vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.SubfolderOnlyForSeveralFiles);
        vm.Location = vm.Locations.Single(l => l.Value == Filee.Core.Presets.OutputLocation.SameFolder);
        Assert.False(vm.ShowSubfolder);
        Assert.True(vm.Apply());
        Assert.False(preset.Output.SubfolderOnlyForSeveralFiles);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Preset_editor_shows_and_saves_watermark_page_numbers_and_password(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "merge-pdf");
        var vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.ShowPdfBuild);
        vm.Watermark = "  DRAFT ";
        vm.PageNumbers = true;
        vm.PdfPassword = "1234";

        var window = new PresetEditorWindow { DataContext = vm, Height = 1400 };
        window.Show();
        Pump();
        Save(window, $"preset-pdf-finishing-{language}.png");
        window.Close();

        Assert.True(vm.Apply());
        Assert.Equal(("DRAFT", true, "1234"), (preset.Pdf.Watermark, preset.Pdf.PageNumbers, preset.Pdf.Password));
        Assert.True(preset.Pdf.FinishesPdf);

        // Not for other targets: nothing is kept behind them.
        vm = new PresetEditorViewModel(preset, loc);
        vm.Target = vm.Targets.Single(t => t.Value == "png");
        Assert.True(vm.Apply());
        Assert.False(preset.Pdf.FinishesPdf);
    }

    [AvaloniaFact]
    public void Preset_editor_password_fits_readers_and_is_off_with_pdfa()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "pdf", Name = "Locked" };
        var vm = new PresetEditorViewModel(preset, loc);

        // 54 Korean letters are 162 UTF-8 bytes; readers use 127, so the box keeps the 42 letters that fit.
        vm.PdfPassword = new string('비', 54);
        Assert.Equal(new string('비', 42), vm.PdfPassword);
        vm.PdfPassword = "pässwörd";
        Assert.Equal("pässwörd", vm.PdfPassword);

        vm.PdfA = true;
        Assert.True(vm.Apply());
        Assert.Equal("", preset.Pdf.Password); // PDF/A can't be encrypted
        vm.PdfA = false;
        Assert.True(vm.Apply());
        Assert.Equal("pässwörd", preset.Pdf.Password);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Preset_editor_shows_and_saves_text_recognition(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "searchable-pdf");
        var vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.ShowOcr);
        Assert.True(vm.Ocr);
        Assert.Equal("eng", vm.OcrLanguage.Value);
        Assert.Equal("eng", vm.OcrLanguageChoices[0].Value); // English first, the others by their own names
        // On a Mac Apple Vision reads the common languages, with nothing to download.
        Assert.Equal(loc[OperatingSystem.IsMacOS() ? "presets.ocr_hint_macos" : "presets.ocr_hint"], vm.OcrHint);
        if (OperatingSystem.IsMacOS())
            Assert.Null(vm.OcrLanguageChoices.Single(l => l.Value == "deu").Detail);

        var window = new PresetEditorWindow { DataContext = vm, Height = 1400 };
        window.Show();
        Pump();
        Save(window, $"preset-ocr-{language}.png");
        window.Close();

        // Scanned letters into Word, in German.
        vm.Target = vm.Targets.Single(t => t.Value == "docx");
        vm.OcrLanguage = vm.OcrLanguageChoices.Single(l => l.Value == "deu");
        Assert.Equal("Deutsch", vm.OcrLanguage.Label);
        Assert.True(vm.Apply());
        Assert.True(preset.Pdf.Ocr);
        Assert.Equal("deu", preset.Pdf.OcrLanguage);

        // Not for pictures: the box is hidden there, and not kept on behind it.
        vm = new PresetEditorViewModel(preset, loc);
        vm.Target = vm.Targets.Single(t => t.Value == "png");
        Assert.False(vm.ShowOcr);
        Assert.True(vm.Apply());
        Assert.False(preset.Pdf.Ocr);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Preset_editor_shows_and_saves_the_fits_options(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "tiff", Name = "Astro TIFF" };
        var vm = new PresetEditorViewModel(preset, loc);
        // TIFF: linear (the default) or stretched; pictures: debayer (the default) or mono.
        Assert.True(vm.ShowFitsTiff);
        Assert.False(vm.ShowFitsDebayer);
        Assert.Equal(Filee.Core.Presets.FitsTiffMode.Linear, vm.FitsTiffMode.Value);
        Assert.Equal(loc["presets.fits_tiff.Stretched"], vm.FitsTiffModes[1].Label);

        var window = new PresetEditorWindow { DataContext = vm, Height = 1400 };
        window.Show();
        Pump();
        Save(window, $"preset-fits-tiff-{language}.png");
        window.Close();

        vm.FitsTiffMode = vm.FitsTiffModes.Single(m => m.Value == Filee.Core.Presets.FitsTiffMode.Stretched);
        Assert.True(vm.Apply());
        Assert.Equal(Filee.Core.Presets.FitsTiffMode.Stretched, preset.Image.FitsTiff);

        vm = new PresetEditorViewModel(preset, loc);
        vm.Target = vm.Targets.Single(t => t.Value == "png");
        Assert.False(vm.ShowFitsTiff);
        Assert.True(vm.ShowFitsDebayer);
        Assert.True(vm.FitsDebayer);
        vm.FitsDebayer = false;
        Assert.True(vm.Apply());
        Assert.False(preset.Image.FitsDebayer);

        // Neither is offered where no picture is written.
        vm.Target = vm.Targets.Single(t => t.Value == "pdf");
        Assert.False(vm.ShowFitsTiff || vm.ShowFitsDebayer);
    }

    [AvaloniaFact]
    public void Preset_editor_hides_and_clears_what_text_recognition_does_not_do()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        var preset = new Filee.Core.Presets.Preset
        {
            TargetFormat = "pdf",
            Name = "Scans",
            Pdf = { SplitPages = true, Compression = Filee.Core.Presets.PdfCompression.Screen, PageSize = Filee.Core.Presets.PdfPageSize.A4, MarginMm = 10 },
        };
        var vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.ShowPdfSplit && vm.ShowPdfCompression && vm.ShowPageSize);

        vm.Ocr = true;

        Assert.False(vm.ShowPdfSplit);
        Assert.False(vm.ShowPdfCompression);
        Assert.False(vm.ShowPdfCompressionOptions);
        Assert.False(vm.ShowPageSize);
        Assert.True(vm.Apply());
        var pdf = preset.Pdf;
        Assert.True(pdf.Ocr);
        Assert.Equal((false, Filee.Core.Presets.PdfCompression.None, Filee.Core.Presets.PdfPageSize.FitImage, 0.0),
            (pdf.SplitPages, pdf.Compression, pdf.PageSize, pdf.MarginMm));

        // Without it they are back.
        vm.Ocr = false;
        Assert.True(vm.ShowPdfSplit && vm.ShowPdfCompression && vm.ShowPageSize);
    }

    [AvaloniaFact]
    public void Preset_editor_keeps_rotation_visible_for_pdf_and_drops_it_for_targets_that_cannot_turn()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();

        // Photos → PDF are turned on the pages: the PDF card shows the rotation, and it is kept.
        var toPdf = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "rotate-left");
        var vm = new PresetEditorViewModel(toPdf, loc);
        vm.Target = vm.Targets.Single(t => t.Value == "pdf");
        Assert.False(vm.ShowImage);
        Assert.True(vm.ShowTurnForPdf);
        Assert.True(vm.Apply());
        Assert.Equal(Filee.Core.Presets.ImageRotation.Left90, toPdf.Image.Rotation);

        // A video can't be turned: a rotation left from the image target would fail every file with nothing on screen.
        var toVideo = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "rotate-left");
        vm = new PresetEditorViewModel(toVideo, loc);
        vm.Flip = vm.Flips.Single(f => f.Value == Filee.Core.Presets.ImageFlip.Vertical);
        vm.Target = vm.Targets.Single(t => t.Value == "mp4");
        Assert.False(vm.ShowTurnForPdf);
        Assert.True(vm.Apply());
        Assert.Equal(Filee.Core.Presets.ImageRotation.None, toVideo.Image.Rotation);
        Assert.Equal(Filee.Core.Presets.ImageFlip.None, toVideo.Image.Flip);
    }

    [AvaloniaFact]
    public void Preset_editor_saves_every_frame_only_for_png_and_jpg()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();

        var preset = new Filee.Core.Presets.Preset { TargetFormat = "jpg" };
        var vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.ShowSaveEveryFrame);
        vm.SaveEveryFrame = true;
        vm.FrameInterval = 5;
        Assert.Equal("One minute of 30 fps video gives 360 pictures, at most 100,000 per video.", vm.FrameEstimate);
        vm.FrameInterval = 5000; // out of range: clamped
        Assert.True(vm.Apply());
        Assert.True(preset.Media.SaveEveryFrame);
        Assert.Equal(1000, preset.Media.FrameInterval);

        // Ticked, then another target: nothing on screen says so any more, so it isn't saved.
        vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.SaveEveryFrame);
        vm.Target = vm.Targets.Single(t => t.Value == "webp");
        Assert.False(vm.ShowSaveEveryFrame);
        Assert.True(vm.Apply());
        Assert.False(preset.Media.SaveEveryFrame);
        Assert.Equal(1, preset.Media.FrameInterval);
    }

    [AvaloniaFact]
    public void Preset_editor_shows_playback_speed_only_for_targets_that_keep_animations()
    {
        TestServices.EnsureInitialized("ko");
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "gif", Name = "Slow GIF" };
        var vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.ShowAnimationSpeed);
        Assert.Equal(1, vm.AnimationSpeed.Value);
        Assert.Equal(["0.25×", "0.5×", "0.75×", "1× (원본)", "1.5×", "2×", "4×"], vm.AnimationSpeeds.Select(s => s.Label));
        vm.AnimationSpeed = vm.AnimationSpeeds.Single(s => s.Value == 0.5);
        var window = new PresetEditorWindow { DataContext = vm, Height = 900 };
        window.Show();
        Pump();
        Save(window, "preset-animation-speed-ko.png");
        window.Close();

        foreach (var target in new[] { "webp", "apng", "mng", Filee.Core.Presets.BuiltInData.SameAsSource })
        {
            vm.Target = vm.Targets.Single(t => t.Value == target);
            Assert.True(vm.ShowAnimationSpeed, target);
        }
        vm.Target = vm.Targets.Single(t => t.Value == "gif");
        vm.AnimationSpeed = null!; // the combo box clearing its selection keeps the value
        Assert.Equal(0.5, vm.AnimationSpeed.Value);
        Assert.True(vm.Apply());
        Assert.Equal(0.5, preset.Image.AnimationSpeed);

        // A speed set for GIF and left behind by a still target isn't saved: nothing on screen would say so.
        vm = new PresetEditorViewModel(preset, loc);
        Assert.Equal(0.5, vm.AnimationSpeed.Value);
        foreach (var target in new[] { "png", "jpg", "mp4", "pdf" })
        {
            vm.Target = vm.Targets.Single(t => t.Value == target);
            Assert.False(vm.ShowAnimationSpeed, target);
        }
        Assert.True(vm.Apply());
        Assert.Equal(1, preset.Image.AnimationSpeed);

        // A speed set outside the editor is offered instead of being lost.
        preset.TargetFormat = "webp";
        preset.Image.AnimationSpeed = 3;
        Assert.Equal("3×", new PresetEditorViewModel(preset, loc).AnimationSpeed.Label);
    }

    [AvaloniaFact]
    public void Preset_editor_shows_and_saves_background_removal()
    {
        TestServices.EnsureInitialized("ko");
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "remove-background");
        var vm = new PresetEditorViewModel(preset, AppHost.Get<Filee.Core.Localization.ILocalizer>());
        Assert.True(vm.RemoveBackground);
        Assert.Equal(25, vm.BackgroundTolerance);
        var window = new PresetEditorWindow { DataContext = vm, Height = 900 };
        window.Show();
        Pump();
        Save(window, "preset-remove-background-ko.png");
        window.Close();

        vm.BackgroundTolerance = 140; // out of range: clamped
        vm.KeepEnclosedBackground = true;
        Assert.True(vm.Apply());

        Assert.True(preset.Image.RemoveBackground);
        Assert.Equal(100, preset.Image.BackgroundTolerance);
        Assert.True(preset.Image.KeepEnclosedBackground);
    }

    [AvaloniaFact]
    public void Preset_editor_shows_and_saves_pdf_compression()
    {
        TestServices.EnsureInitialized("ko");
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "compress-pdf");
        var vm = new PresetEditorViewModel(preset, AppHost.Get<Filee.Core.Localization.ILocalizer>());
        Assert.True(vm.ShowPdfCompression);
        Assert.True(vm.ShowPdfCompressionOptions);
        Assert.Equal(Filee.Core.Presets.PdfCompression.Ebook, vm.CompressionLevel.Value);
        Assert.Equal(["None", "Screen", "Ebook", "Print"], vm.CompressionLevels.Select(c => c.Value.ToString()));
        var window = new PresetEditorWindow { DataContext = vm, Height = 900 };
        window.Show();
        Pump();
        Save(window, "preset-compress-pdf-ko.png");
        window.Close();

        vm.CompressionLevel = vm.CompressionLevels.Single(c => c.Value == Filee.Core.Presets.PdfCompression.Screen);
        vm.CompressWithGhostscript = true;
        vm.KeepMetadata = false;
        Assert.True(vm.Apply());
        Assert.Equal(Filee.Core.Presets.PdfCompression.Screen, preset.Pdf.Compression);
        Assert.True(preset.Pdf.CompressWithGhostscript);
        Assert.False(preset.Image.KeepMetadata);

        // Off: Ghostscript and metadata have nothing to do. Other targets don't compress PDFs at all.
        vm.CompressionLevel = vm.CompressionLevels[0];
        Assert.False(vm.ShowPdfCompressionOptions);
        vm.Target = vm.Targets.Single(t => t.Value == "png");
        Assert.False(vm.ShowPdfCompression);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void Sidebar_shows_the_version_and_a_new_release(string language)
    {
        TestServices.EnsureInitialized(language);
        var updates = AppHost.Get<UpdateService>();
        using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), updates);
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        var button = window.FindControl<Button>("UpdateButton")!;
        var version = window.FindControl<TextBlock>("VersionLabel")!;
        Assert.False(button.IsVisible);
        Assert.Contains(UpdateService.CurrentVersion, version.Text);

        try
        {
            updates.Notes = [ReleaseNotes.Parse("9.9.9", """
                <!-- update
                level: recommended
                ko: 엑셀에서 정한 페이지 나누기가 PDF에 반영돼요
                ko: 한글 문서의 보기 상자와 수식이 PDF에서 제대로 나와요
                en: Excel page breaks carry over to PDF
                en: Boxes and equations of 한글 documents come out right in PDFs
                zh: Excel 中设置的分页符会保留到 PDF 中
                zh: 韩文 Hangul 文档中的框和公式在 PDF 中正确显示
                -->
                """)];
            updates.LatestVersion = "9.9.9";
            Pump();
            Assert.True(button.IsVisible);
            Assert.Contains("9.9.9", vm.UpdateText);
            Save(window, $"sidebar-update-{language}.png");

            var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
            var notice = new UpdateNoticeWindow(updates, loc, loc.Format("update.notice_title", "9.9.9"));
            notice.Show();
            Pump();
            Save(notice, $"update-notice-{language}.png");

            // A required update: red pill on the card and in the sidebar; an optional one: plain button, grey pill.
            updates.Notes = [ReleaseNotes.Parse("9.9.9", """
                <!-- update
                level: required
                ko: 변환 결과가 잘못 나오던 문제를 고쳤어요
                en: Fixes wrong conversion results
                zh: 修复了转换结果错误的问题
                -->
                """)];
            Pump();
            Save(notice, $"update-notice-required-{language}.png");
            Save(window, $"sidebar-update-required-{language}.png");
            updates.Notes = [ReleaseNotes.Parse("9.9.9", "<!-- update\nlevel: optional\nko: 새 기능\nen: A new feature\n-->")];
            Pump();
            Save(window, $"sidebar-update-optional-{language}.png");

            // While the installer downloads, the card, the sidebar button and the tray show the progress.
            updates.Step = UpdateStep.Downloading;
            updates.DownloadPercent = 42;
            Pump();
            Assert.Contains("42", vm.UpdateText);
            Save(notice, $"update-notice-downloading-{language}.png");
            Save(window, $"sidebar-update-downloading-{language}.png");
            updates.Step = UpdateStep.Idle;
            notice.Close();

            var feedback = new FeedbackNoticeWindow();
            feedback.Show();
            Pump();
            Save(feedback, $"feedback-notice-{language}.png");
            feedback.Close();
        }
        finally
        {
            updates.Step = UpdateStep.Idle;
            updates.LatestVersion = null;
            updates.Notes = [];
            window.Close();
        }
    }

    /// <summary>
    /// The extension chips of a profile that shares extensions with "Images" (warning chips), has one Filee doesn't
    /// know (muted chip) and shows suggestions for what is being typed (dark: a refused duplicate instead).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", false)]
    [InlineData("zh-CN", false)]
    [InlineData("en", true)]
    [InlineData("ko", true)]
    [InlineData("ru", false)]
    [InlineData("ar", true)]
    public void Toolbar_extension_editor_renders(string language, bool dark)
    {
        var photos = new ToolbarProfile
        {
            Id = "render-photos",
            Name = language == "ko" ? "사진" : "Photos",
            Extensions = ["jpg", "png", "heic", "dng", "webp", "xyz"],
            PresetIds = ["to-jpg", "to-webp", "half-size", "to-pdf"],
        };
        WithProfiles(language, dark, 1240, profiles => profiles.Insert(profiles.FindIndex(p => p.IsFallback), photos), (window, vm) =>
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile == photos);
            Pump();
            var editor = window.GetVisualDescendants().OfType<TagEditor>().Single();
            editor.FocusInput();
            Pump();
            if (dark)
            {
                window.KeyTextInput("PNG");
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                Assert.NotNull(vm.ExtensionTags!.InputError);
            }
            else
            {
                window.KeyTextInput("we");
                Pump();
                Assert.True(editor.IsSuggestionListOpen);
            }
            Pump();

            Assert.Equal(6, vm.ExtensionTags!.Tags.Count);
            Assert.Equal(2, vm.ExtensionTags.Notes.Count); // shared with "Images" + unknown
            Save(window, $"toolbar-extensions-{language}{(dark ? "-dark" : "")}.png");
        });
    }

    /// <summary>A second mixed-selection donut limited to images and PDF, two groups expanded.</summary>
    [AvaloniaTheory]
    [InlineData("en", false, "render-scans")]
    [InlineData("ko", false, "render-scans")]
    [InlineData("zh-CN", false, "render-scans")]
    [InlineData("ko", true, "render-scans")]
    [InlineData("en", false, "mixed")]
    [InlineData("en", true, "mixed")]
    public void Toolbar_mixed_selection_editor_renders(string language, bool dark, string profileId)
    {
        var scans = new ToolbarProfile
        {
            Id = "render-scans",
            Name = language == "ko" ? "스캔 모음" : "Scans",
            IsFallback = true,
            Extensions = ["jpg", "png", "tiff", "pdf"],
            PresetIds = ["to-pdf", "merge-pdf", "zip-all"],
        };
        WithProfiles(language, dark, 1600, profiles => profiles.Add(scans), (window, vm) =>
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == profileId);
            Pump();
            var mixed = vm.MixedExtensions!;
            if (profileId != "mixed")
                mixed.Groups[0].IsExpanded = true; // Images: partly checked, many chips
            mixed.Groups[1].IsExpanded = true; // PDF: all checked (Scans)
            Pump();

            Assert.True(vm.IsMixed);
            Assert.Null(vm.ExtensionTags);
            Assert.Equal(profileId == "mixed" ? false : null, mixed.Groups[0].State);
            Assert.True(vm.CanDelete); // two mixed profiles: either may go
            Save(window, $"toolbar-mixed-{profileId.Replace("render-", "")}-{language}{(dark ? "-dark" : "")}.png");
        });
    }

    /// <summary>The preset popup with the video/audio and archive sections, and the target list.</summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", false)]
    [InlineData("zh-CN", false)]
    [InlineData("ko", true)]
    [InlineData("ru", false)]
    [InlineData("ar", true)]
    [InlineData("pt-BR", false)]
    [InlineData("fr", false)]
    public void Preset_editor_shows_media_and_archive_options(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var suffix = $"{language}{(dark ? "-dark" : "")}";

            var media = new PresetEditorViewModel(store.FindPreset("mp4-720")!, loc);
            Assert.True(media.ShowMedia && media.ShowMaxHeight && media.ShowRemoveAudio);
            Assert.False(media.ShowPdf || media.ShowImage || media.ShowArchive);
            Assert.Equal(720, media.MaxHeight.Value);
            var window = new PresetEditorWindow { DataContext = media, Height = 900 };
            window.Show();
            Pump();
            Save(window, $"preset-media-{suffix}.png");

            // The target list: categories next to the names. (Headless popups live in the window's overlay layer,
            // so the open list is part of the window's frame.)
            var box = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "TargetBox");
            box.IsDropDownOpen = true;
            Pump();
            Assert.True(box.GetVisualDescendants().OfType<Popup>().Single().IsOpen);
            Save(window, $"preset-targets-{suffix}.png");
            box.IsDropDownOpen = false;
            window.Close();

            // HEVC preset: the codec choice next to the other video options.
            var hevc = new PresetEditorViewModel(store.FindPreset("mp4-hevc")!, loc);
            Assert.True(hevc.ShowVideoCodec && !hevc.ShowAudioCodec);
            Assert.Equal(Filee.Core.Presets.VideoCodec.Hevc, hevc.VideoCodecChoice.Value);
            window = new PresetEditorWindow { DataContext = hevc, Height = 900 };
            window.Show();
            Pump();
            Save(window, $"preset-codec-{suffix}.png");
            window.Close();

            var archive = new PresetEditorViewModel(store.FindPreset("zip-all")!, loc);
            Assert.True(archive.ShowArchive && archive.ShowArchiveOptions && archive.CombineIntoOne);
            Assert.False(archive.ShowMedia || archive.ShowPdf);
            window = new PresetEditorWindow { DataContext = archive, Height = 760 };
            window.Show();
            Pump();
            Save(window, $"preset-archive-{suffix}.png");
            window.Close();

            // Audio targets have no picture options; lossless ones only channels, WAV also sample rate and bit depth (#37);
            // AMR is fixed mono 8 kHz; "Extract" only explains.
            archive.Target = archive.Targets.Single(t => t.Value == "mp3");
            Assert.True(archive.ShowMedia && archive.ShowAudioBitrate && archive.ShowAudioChannels && !archive.ShowMaxHeight && !archive.ShowRemoveAudio);
            Assert.False(archive.ShowWav);
            archive.Target = archive.Targets.Single(t => t.Value == "flac");
            Assert.True(archive.ShowMedia && archive.ShowAudioChannels && !archive.ShowAudioBitrate && !archive.ShowWav);
            archive.Target = archive.Targets.Single(t => t.Value == "wav");
            Assert.True(archive.ShowMedia && archive.ShowAudioChannels && archive.ShowWav);
            window = new PresetEditorWindow { DataContext = archive, Height = 760 };
            window.Show();
            Pump();
            Save(window, $"preset-wav-{suffix}.png");
            window.Close();
            archive.Target = archive.Targets.Single(t => t.Value == "amr");
            Assert.False(archive.ShowAudioChannels || archive.ShowWav);
            archive.Target = archive.Targets.Single(t => t.Value == Filee.Core.Formats.FormatRegistry.Folder);
            Assert.True(archive.ShowArchive && archive.ShowExtractNote && !archive.ShowArchiveOptions);
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
    }

    /// <summary>The preset popup of "Trace to SVG": mode, colours and detail; colours only in colour mode.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void Preset_editor_shows_tracing_options(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var vm = new PresetEditorViewModel(store.FindPreset("trace-svg")!.Clone(), AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowTrace && vm.ShowTraceColors);
        Assert.False(vm.ShowImage || vm.ShowMedia || vm.ShowArchive);

        var window = new PresetEditorWindow { DataContext = vm, Height = 760 };
        window.Show();
        Pump();
        Assert.True(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TraceCard").IsVisible);
        Save(window, $"preset-trace-{language}.png");
        window.Close();

        vm.TraceMode = vm.TraceModes.Single(m => m.Value == Filee.Core.Presets.TracingMode.BlackAndWhite);
        Assert.False(vm.ShowTraceColors);
        vm.Target = vm.Targets.Single(t => t.Value == "dxf");
        Assert.True(vm.ShowTrace);
        vm.Target = vm.Targets.Single(t => t.Value == "png");
        Assert.False(vm.ShowTrace);
    }

    [AvaloniaFact]
    public void Preset_editor_saves_tracing_options()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "svg", Name = "Logo" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.Equal(["Colour", "Black and white (line art)"], vm.TraceModes.Select(m => m.Label));

        vm.TraceMode = vm.TraceModes.Single(m => m.Value == Filee.Core.Presets.TracingMode.BlackAndWhite);
        vm.TraceColors = 200;
        vm.TraceDetail = 80;
        Assert.True(vm.Apply());

        Assert.Equal((Filee.Core.Presets.TracingMode.BlackAndWhite, 64, 80), (preset.Trace.Mode, preset.Trace.Colors, preset.Trace.Detail));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void Preset_editor_offers_markdown_without_pictures(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var preset = store.FindPreset("to-md")!.Clone();
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowLeaveOutPictures);
        Assert.False(vm.LeaveOutPictures);

        var window = new PresetEditorWindow { DataContext = vm, Height = 760 };
        window.Show();
        Pump();
        Assert.True(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DocumentCard").IsVisible);
        Save(window, $"preset-markdown-{language}.png");
        window.Close();

        vm.LeaveOutPictures = true;
        Assert.True(vm.Apply());
        Assert.True(preset.Document.LeaveOutPictures);

        // Other targets don't show it, and don't keep it.
        vm.Target = vm.Targets.Single(t => t.Value == "docx");
        Assert.False(vm.ShowLeaveOutPictures);
        Assert.True(vm.Apply());
        Assert.False(preset.Document.LeaveOutPictures);
    }

    [AvaloniaFact]
    public void Preset_editor_offers_the_autocad_version_for_dwg_and_dxf_only()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "dwg", Name = "Old DWG" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowCad);
        Assert.Equal(["AutoCAD 2018", "AutoCAD 2013", "AutoCAD 2010", "AutoCAD 2007", "AutoCAD 2004", "AutoCAD 2000", "Same as the source"],
            vm.CadVersions.Select(v => v.Label));

        vm.CadVersion = vm.CadVersions.Single(v => v.Value == Filee.Core.Presets.CadVersion.AutoCad2004);
        Assert.True(vm.Apply());
        Assert.Equal(Filee.Core.Presets.CadVersion.AutoCad2004, preset.Cad.Version);
        Assert.Equal(Filee.Core.Presets.CadVersion.AutoCad2004, new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>()).CadVersion.Value);

        // Not for other targets: the choice isn't kept behind them.
        vm.Target = vm.Targets.Single(t => t.Value == "png");
        Assert.False(vm.ShowCad);
        Assert.True(vm.Apply());
        Assert.Equal(Filee.Core.Presets.CadVersion.AutoCad2018, preset.Cad.Version);
    }

    [AvaloniaFact]
    public void Preset_editor_offers_save_as_zip_for_geospatial_targets_only()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "shp", Name = "Shapefile" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowZipResult);
        Assert.Equal("Geospatial", vm.Target.Detail);

        vm.ZipResult = true;
        Assert.True(vm.Apply());
        Assert.True(preset.Output.ZipResult);

        vm.Target = vm.Targets.Single(t => t.Value == "kml");
        Assert.True(vm.ShowZipResult);
        vm.Target = vm.Targets.Single(t => t.Value == "png");
        Assert.False(vm.ShowZipResult);
        // The box ticked for Shapefile, hidden for PNG, doesn't zip the PNG.
        Assert.True(vm.Apply());
        Assert.False(preset.Output.ZipResult);
    }

    [AvaloniaFact]
    public void Preset_editor_saves_replace_the_original_off_by_default()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = Filee.Core.Presets.BuiltInData.SameAsSource, Name = "Trim" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.False(vm.ReplaceOriginal);

        vm.ReplaceOriginal = true;
        Assert.True(vm.Apply());
        Assert.True(preset.Output.ReplaceOriginal);
        Assert.True(new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>()).ReplaceOriginal);
        Assert.Contains("Recycle Bin / Trash", AppHost.Get<ILocalizer>()["presets.replace_original_hint"]);
    }

    [AvaloniaFact]
    public void Preset_editor_saves_media_and_archive_options()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "mp4", Name = "Clip" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.Equal("*", vm.Targets[0].Value); // "Same as source" stays first
        Assert.Equal([0, 32, 48, 64, 96, 128, 192, 256, 320], vm.AudioBitrates.Select(b => b.Value)); // 32-64 for speech (#37)
        Assert.All(vm.Targets.Skip(1), t => Assert.True(Filee.Core.Formats.FormatRegistry.Get(t.Value).Writable));
        Assert.Equal("Video", vm.Target.Detail);

        vm.MediaQuality = vm.MediaQualities.Single(q => q.Value == Filee.Core.Presets.MediaQuality.Small);
        vm.MaxHeight = vm.MaxHeights.Single(h => h.Value == 1080);
        vm.AudioBitrate = vm.AudioBitrates.Single(b => b.Value == 192);
        vm.RemoveAudio = true;
        vm.AudioChannels = vm.AudioChannelChoices.Single(c => c.Value == 1);
        vm.SampleRate = vm.SampleRates.Single(r => r.Value == 16000);
        vm.WavFormat = vm.WavFormats.Single(f => f.Value == Filee.Core.Presets.WavSampleFormat.Pcm24);
        Assert.Equal("16 kHz", vm.SampleRate.Label);
        vm.ArchiveLevel = vm.ArchiveLevels.Single(l => l.Value == Filee.Core.Presets.ArchiveLevel.Maximum);
        vm.CombineIntoOne = true;
        Assert.True(vm.Apply());

        Assert.Equal(Filee.Core.Presets.MediaQuality.Small, preset.Media.Quality);
        Assert.Equal(1080, preset.Media.MaxHeight);
        Assert.Equal(192, preset.Media.AudioBitrateKbps);
        Assert.True(preset.Media.RemoveAudio);
        Assert.Equal((1, 16000, Filee.Core.Presets.WavSampleFormat.Pcm24),
            (preset.Media.AudioChannels, preset.Media.AudioSampleRate, preset.Media.WavFormat));
        Assert.Equal(Filee.Core.Presets.ArchiveLevel.Maximum, preset.Archive.Level);
        Assert.True(preset.Archive.CombineIntoOne);

        // A value set outside the editor is offered instead of being lost.
        preset.Media.MaxHeight = 600;
        Assert.Equal("600p", new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>()).MaxHeight.Label);
    }

    [AvaloniaFact]
    public void Preset_editor_offers_hardware_encoding_for_h264_hevc_and_av1()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "mp4", Name = "Fast" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowHardwareEncoding);
        Assert.False(vm.HardwareEncoding); // off unless asked for
        vm.HardwareEncoding = true;

        // HEVC and AV1 have a hardware path; VP9 (WebM's usual codec) has none.
        vm.VideoCodecChoice = vm.VideoCodecs.Single(c => c.Value == Filee.Core.Presets.VideoCodec.Av1);
        Assert.True(vm.ShowHardwareEncoding);
        vm.VideoCodecChoice = vm.VideoCodecs.Single(c => c.Value == Filee.Core.Presets.VideoCodec.Hevc);
        Assert.True(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "webm");
        Assert.False(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "avi");
        Assert.False(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "mp3");
        Assert.False(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "ts");
        Assert.True(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "mov");
        Assert.True(vm.Apply());
        Assert.True(preset.Media.HardwareEncoding);
        Assert.Equal(Filee.Core.Presets.VideoCodec.Hevc, preset.Media.VideoCodec);

        Assert.True(new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>()).HardwareEncoding);

        // WebM written as AV1 (the "MP4 AV1" kind of preset, in WebM) offers it too.
        var webm = new PresetEditorViewModel(new Filee.Core.Presets.Preset { TargetFormat = "webm", Name = "AV1" }, AppHost.Get<ILocalizer>());
        Assert.False(webm.ShowHardwareEncoding);
        webm.VideoCodecChoice = webm.VideoCodecs.Single(c => c.Value == Filee.Core.Presets.VideoCodec.Av1);
        Assert.True(webm.ShowHardwareEncoding);
    }

    [AvaloniaFact]
    public void Preset_editor_offers_the_codecs_each_target_can_hold()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "mp4", Name = "Codec" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowVideoCodec && !vm.ShowAudioCodec);
        Assert.Equal([Filee.Core.Presets.VideoCodec.Default, Filee.Core.Presets.VideoCodec.Hevc, Filee.Core.Presets.VideoCodec.Av1],
            vm.VideoCodecs.Select(c => c.Value));
        Assert.StartsWith("H.264", vm.VideoCodecChoice.Label);
        vm.VideoCodecChoice = vm.VideoCodecs.Single(c => c.Value == Filee.Core.Presets.VideoCodec.Hevc);

        // WebM has no HEVC: its list is VP9 / AV1 and the choice shows as VP9; MKV takes HEVC again.
        vm.Target = vm.Targets.Single(t => t.Value == "webm");
        Assert.Equal(["VP9", "AV1 (smallest, slow)"], vm.VideoCodecs.Select(c => c.Label));
        Assert.Equal(Filee.Core.Presets.VideoCodec.Default, vm.VideoCodecChoice.Value);
        vm.Target = vm.Targets.Single(t => t.Value == "mkv");
        Assert.Equal(Filee.Core.Presets.VideoCodec.Hevc, vm.VideoCodecChoice.Value);
        vm.Target = vm.Targets.Single(t => t.Value == "avi");
        Assert.False(vm.ShowVideoCodec);

        // M4A: AAC or ALAC; ALAC is lossless, so quality and bitrate go away.
        vm.Target = vm.Targets.Single(t => t.Value == "m4a");
        Assert.True(vm.ShowAudioCodec && vm.ShowAudioBitrate && vm.ShowMediaQuality && !vm.ShowVideoCodec);
        vm.AudioCodecChoice = vm.AudioCodecs.Single(c => c.Value == Filee.Core.Presets.AudioCodec.Alac);
        Assert.False(vm.ShowAudioBitrate || vm.ShowMediaQuality);
        Assert.True(vm.ShowMedia && vm.ShowAudioChannels);
        Assert.True(vm.Apply());
        Assert.Equal((Filee.Core.Presets.VideoCodec.Default, Filee.Core.Presets.AudioCodec.Alac), (preset.Media.VideoCodec, preset.Media.AudioCodec));

        // New lossless targets and W64 (WAV options); subtitles have no media or PDF options.
        vm.Target = vm.Targets.Single(t => t.Value == "wv");
        Assert.False(vm.ShowAudioBitrate || vm.ShowAudioCodec);
        vm.Target = vm.Targets.Single(t => t.Value == "w64");
        Assert.True(vm.ShowWav);
        vm.Target = vm.Targets.Single(t => t.Value == "smi");
        Assert.False(vm.ShowMedia || vm.ShowPdf || vm.ShowImage);
        Assert.Equal("Subtitle", vm.Target.Detail);
    }

    /// <summary>The geospatial donut in the toolbar editor: GeoJSON, Shapefile, KML, CSV, GPX and KMZ for geo files.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Toolbar_geospatial_donut_renders(string language)
    {
        WithProfiles(language, false, 1240, _ => { }, (window, vm) =>
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "geo");
            Pump();
            Assert.Equal(["to-geojson", "to-shp", "to-kml", "to-csv", "to-gpx", "to-kmz"], vm.SelectedProfile.Profile.PresetIds);
            Assert.Contains("shp", vm.ExtensionTags!.Tags.Select(t => t.Text.TrimStart('.')));
            Save(window, $"toolbar-geospatial-{language}.png");
        });
    }

    /// <summary>
    /// The palette tabs of the mixed donut (no extensions, so every kind and no judging) wrap inside the 230 px column:
    /// every tab whole, in rows, reachable with the arrow keys, the last one too.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("ko", false)]
    [InlineData("ko", true)]
    [InlineData("es", false)]
    [InlineData("es", true)]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    public void Palette_tabs_fit_narrow_column(string language, bool dark)
    {
        WithProfiles(language, dark, 820, _ => { }, (window, vm) =>
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "mixed");
            Pump();
            Assert.Empty(vm.SelectedProfile.Profile.Extensions);
            var column = window.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "PaletteColumn");
            var tabs = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PaletteTabs");
            Assert.True(tabs.IsEffectivelyVisible);
            Assert.Equal(Enum.GetValues<PaletteTab>(), tabs.Items.Cast<PaletteTabItem>().Select(t => t.Id));

            Avalonia.Rect InWindow(Avalonia.Visual v) =>
                new Avalonia.Rect(v.Bounds.Size).TransformToAABB(Avalonia.VisualExtensions.TransformToVisual(v, window)!.Value);
            var columnBox = InWindow(column);
            var rowBox = InWindow(tabs);
            Assert.Equal(230, columnBox.Width, 0.5);
            var items = Enumerable.Range(0, tabs.ItemCount).Select(i => (ListBoxItem)tabs.ContainerFromIndex(i)!).ToList();
            var boxes = items.Select(InWindow).ToList();
            for (var i = 0; i < items.Count; i++)
            {
                var box = boxes[i];
                Assert.True(box.Left >= columnBox.Left - 0.5 && box.Right <= columnBox.Right + 0.5, $"tab {i} {box} outside {columnBox}");
                Assert.True(box.Top >= rowBox.Top - 0.5 && box.Bottom <= rowBox.Bottom + 0.5, $"tab {i} {box} outside {rowBox}");
                var text = items[i].GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.Single(text.TextLayout.TextLines);
                Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 0.5, text.Text);
                if (i == 0)
                    continue;
                // Reading order: right of the previous tab on its row, or on a row below it.
                var previous = boxes[i - 1];
                Assert.True(Math.Abs(box.Top - previous.Top) < 0.5 ? box.Left >= previous.Right - 0.5 : box.Top >= previous.Bottom - 0.5,
                    $"tab {i} {box} overlaps or precedes {previous}");
            }
            Assert.True(boxes.Select(b => Math.Round(b.Top)).Distinct().Count() >= 2);
            var suffix = dark ? "dark" : "light";
            Save(window, $"toolbar-tabs-{language}-{suffix}.png");

            // Right along a row, Down at its end to the tab on the next row nearest below, up to the last tab.
            void Press(Key key, PhysicalKey physical)
            {
                window.KeyPress(key, RawInputModifiers.None, physical, null);
                window.KeyRelease(key, RawInputModifiers.None, physical, null);
                Pump();
            }
            bool SameRow(int a, int b) => Math.Abs(boxes[a].Top - boxes[b].Top) < 0.5;
            int Nearest(int from, bool below)
            {
                var beyond = Enumerable.Range(0, boxes.Count)
                    .Where(i => below ? boxes[i].Top >= boxes[from].Bottom - 0.5 : boxes[i].Bottom <= boxes[from].Top + 0.5).ToList();
                var row = below ? beyond.Min(i => boxes[i].Top) : beyond.Max(i => boxes[i].Top);
                return beyond.Where(i => Math.Abs(boxes[i].Top - row) < 0.5).MinBy(i => Math.Abs(boxes[i].Center.X - boxes[from].Center.X));
            }
            var visited = new HashSet<int>();
            void Expect(int index)
            {
                Assert.Same(tabs.Items[index], tabs.SelectedItem);
                Assert.Same(vm.Tabs[index], vm.SelectedTab);
                Assert.True(items[index].IsFocused, $"tab {index} not focused");
                visited.Add(index);
            }
            Assert.True(items[0].Focus());
            // The first row has nothing above it: Up stays.
            Press(Key.Up, PhysicalKey.ArrowUp);
            Expect(0);
            var current = 0;
            var downs = 0;
            while (current < items.Count - 1)
            {
                if (SameRow(current, current + 1))
                {
                    Press(Key.Right, PhysicalKey.ArrowRight);
                    current++;
                }
                else
                {
                    Press(Key.Down, PhysicalKey.ArrowDown);
                    current = Nearest(current, below: true);
                    downs++;
                }
                Expect(current);
            }
            Assert.True(downs >= 1);
            // The last row has nothing below it: Down stays.
            Press(Key.Down, PhysicalKey.ArrowDown);
            Expect(items.Count - 1);
            var last = items[^1];
            last.BringIntoView();
            Pump();
            var scroller = window.FindControl<ScrollViewer>("PageScroller")!;
            Assert.True(last.IsEffectivelyVisible);
            Assert.True(InWindow(scroller).Intersects(InWindow(last)));
            Assert.True(InWindow(last).Right <= InWindow(column).Right + 0.5);
            var chips = window.GetVisualDescendants().OfType<Border>().Where(b => b.DataContext is PaletteChip && b.Classes.Contains("chip"))
                .Select(b => ((PaletteChip)b.DataContext!).PresetId).ToList();
            // The last tab is Font: font presets, no image or archive ones.
            Assert.Equal(PaletteTab.Font, vm.SelectedTab!.Id);
            Assert.Contains("to-ttf", chips);
            Assert.DoesNotContain("to-zip", chips);
            Assert.DoesNotContain("to-png", chips);
            Save(window, $"toolbar-tabs-{language}-{suffix}-font.png");

            // Up goes back to the row above, to the tab nearest above.
            var above = Nearest(items.Count - 1, below: false);
            Press(Key.Up, PhysicalKey.ArrowUp);
            Expect(above);
            Assert.False(SameRow(above, items.Count - 1));

            // From there Right reaches the tabs a Down skipped, then Left walks back over every tab, across the row ends,
            // to the first.
            for (current = above; current < items.Count - 1; current++)
            {
                Press(Key.Right, PhysicalKey.ArrowRight);
                Expect(current + 1);
            }
            for (current = items.Count - 1; current > 0; current--)
            {
                Press(Key.Left, PhysicalKey.ArrowLeft);
                Expect(current - 1);
            }
            Assert.Equal(Enumerable.Range(0, items.Count), visited.Order());
        });
    }

    /// <summary>A search shows a preset this donut can't run: dimmed, with its reason, under its own tab.</summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", true)]
    public void Palette_search_reveals_unusable(string language, bool dark)
    {
        using var f = new PresetAvailabilityTests.Fixture();
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        f.Preset("test-wav").Name = "Hidden WAV sample";
        f.Preset("test-jpg").Name = "Hidden JPG sample";
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        using var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        var page = new ToolbarPage { DataContext = vm };
        var window = new Window { Width = 1080, Height = 820, Content = page };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("PageBrush"));
        try
        {
            window.Show();
            Pump();
            var search = page.FindControl<TextBox>("PaletteSearchBox")!;
            Assert.True(search.Focus());
            window.KeyTextInput("Hidden");
            Pump();
            var tabs = page.FindControl<ListBox>("PaletteTabs")!;
            Assert.True(tabs.IsEffectivelyVisible);
            Assert.Equal([PaletteTab.All, PaletteTab.Image, PaletteTab.Media], tabs.Items.Cast<PaletteTabItem>().Select(t => t.Id));
            var media = (ListBoxItem)tabs.ContainerFromIndex(2)!;
            var point = Avalonia.VisualExtensions.TranslatePoint(media, new Avalonia.Point(media.Bounds.Width / 2, media.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Pump();
            Assert.Same(vm.Tabs[2], tabs.SelectedItem);
            var chip = page.GetVisualDescendants().OfType<Border>().Single(b => b.DataContext is PaletteChip { PresetId: "test-wav" });
            Assert.Contains("dimmed", chip.Classes);
            Assert.Equal(f.Loc["error.no_route"], ToolTip.GetTip(chip));
            var suffix = dark ? "dark" : "light";
            Save(window, $"toolbar-search-unusable-{language}-{suffix}.png");
            ToolTip.SetIsOpen(chip, true);
            Pump();
            Save(window, $"toolbar-search-unusable-{language}-{suffix}-tooltip.png");
            ToolTip.SetIsOpen(chip, false);
        }
        finally
        {
            window.Close();
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
    }

    /// <summary>Shows the toolbar page (tall, so the profile card is visible) with temporarily changed profiles.</summary>
    private static void WithProfiles(string language, bool dark, double height, Action<List<ToolbarProfile>> change, Action<Window, ToolbarPageViewModel> act)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var theme = AppHost.Get<ThemeService>();
        var snapshot = store.Profiles.Select(p => p.Clone()).ToList();
        Window? window = null;
        try
        {
            change(store.Profiles);
            if (dark)
                theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("toolbar");
            window = new MainWindow { DataContext = vm, Width = 1080, Height = height };
            window.Show();
            Pump();
            act(window, (ToolbarPageViewModel)vm.CurrentPage);
        }
        finally
        {
            window?.Close();
            if (dark)
                theme.Apply(new ThemeSettings(), language);
            store.Profiles.Clear();
            store.Profiles.AddRange(snapshot);
            store.SaveLibrary();
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void Excluded_apps_render_as_tags(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.ExcludedProcesses.ToList();
        try
        {
            store.Settings.ExcludedProcesses.Clear();
            store.Settings.ExcludedProcesses.AddRange(["Photoshop", "code"]);
            var vm = new TriggersPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>());
            var window = new Window { Content = new Filee.App.Views.Pages.TriggersPage { DataContext = vm }, Width = 820, Height = 1500 };
            window.Show();
            Pump();
            Assert.Equal(["Photoshop", "code"], vm.ExcludedApps.Tags.Select(t => t.Value));
            Save(window, $"triggers-excluded-{language}.png");
            window.Close();
        }
        finally
        {
            store.Settings.ExcludedProcesses.Clear();
            store.Settings.ExcludedProcesses.AddRange(before);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void Close_dialog_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var dialog = new CloseAskWindow();
        dialog.Show();
        Pump();
        Save(dialog, $"window-close-{language}.png");
        dialog.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    [InlineData("ru")]
    [InlineData("ar")]
    public void Format_request_notice_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        var notice = new FormatRequestNoticeWindow([".ncm"], loc.Format("format_request.notice_title", ".ncm"));
        notice.Show();
        Pump();
        Save(notice, $"window-format-request-{language}.png");
        notice.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Extract_live_photo_video_option_renders_and_saves_for_mp4_and_mov_only(string language)
    {
        TestServices.EnsureInitialized(language);
        var preset = Filee.Core.Presets.BuiltInData.MotionPhotoPresets().Single();
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowExtractMotionPhoto);
        Assert.True(vm.ExtractMotionPhoto);
        var window = new PresetEditorWindow { DataContext = vm, Height = 980 };
        window.Show();
        Pump();
        Save(window, $"preset-extract-motion-{language}.png");
        window.Close();

        vm.Target = vm.Targets.Single(t => t.Value == "mov");
        Assert.True(vm.ShowExtractMotionPhoto);
        Assert.True(vm.Apply());
        Assert.True(preset.Media.ExtractMotionPhoto);
        // A box ticked for MP4 and left behind by another target saves nothing.
        var restored = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        restored.Target = restored.Targets.Single(t => t.Value == "webm");
        Assert.False(restored.ShowExtractMotionPhoto);
        Assert.True(restored.Apply());
        Assert.False(preset.Media.ExtractMotionPhoto);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("ja")]
    [InlineData("zh-CN")]
    [InlineData("ru")]
    [InlineData("ar")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    [InlineData("fr")]
    public void Live_photo_options_render_for_jpg_and_heic_and_save_only_there(string language)
    {
        TestServices.EnsureInitialized(language);
        var preset = Filee.Core.Presets.BuiltInData.LivePhotoPresets().First();
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowLivePhotoChoice);
        Assert.True(vm.IsLivePhoto);
        Assert.False(vm.ShowImage);
        Assert.False(vm.ShowSaveEveryFrame);
        Assert.Equal(3, vm.LivePhotoKinds.Count);
        var window = new PresetEditorWindow { DataContext = vm, Height = 980 };
        window.Show();
        Pump();
        Save(window, $"preset-live-photo-{language}.png");
        window.Close();

        // HEIC is an iPhone's: no Motion Photo there.
        vm.Target = vm.Targets.Single(t => t.Value == "heic");
        Assert.Equal(2, vm.LivePhotoKinds.Count);
        Assert.True(vm.IsLivePhoto);
        vm.LivePhotoCodecChoice = vm.LivePhotoCodecs.Single(c => c.Value == Filee.Core.Presets.VideoCodec.Hevc);
        vm.LivePhotoLength = 60;
        Assert.True(vm.Apply());
        Assert.Equal(("heic", Filee.Core.Presets.LivePhotoKind.Apple), (preset.TargetFormat, preset.Media.LivePhoto));
        Assert.Equal(Filee.Core.Presets.VideoCodec.Hevc, preset.Media.VideoCodec);
        Assert.Equal(Filee.Core.Presets.MediaOptions.MaxLivePhotoLength, preset.Media.LivePhotoLength);

        // A kind left behind by a target that can't be one saves nothing.
        var restored = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        restored.Target = restored.Targets.Single(t => t.Value == "png");
        Assert.False(restored.ShowLivePhotoChoice);
        Assert.True(restored.ShowImage);
        Assert.True(restored.Apply());
        Assert.Equal(Filee.Core.Presets.LivePhotoKind.None, preset.Media.LivePhoto);
    }

    public static TheoryData<string, bool> ClipLooks => new()
    {
        { "en", false }, { "en", true }, { "ru", false }, { "ru", true }, { "ar", false }, { "ar", true }, { "ja", false }, { "hi", true },
        { "pt-PT", false }, { "pt-PT", true }, { "pt-BR", false }, { "pt-BR", true },
    };

    /// <summary>
    /// "Cut a clip": the frame at the active handle large, start / length / end, the timeline with the clip between its
    /// handles and the toolbar, in light and dark, short and long languages and right to left.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(ClipLooks))]
    public void Clip_window_renders(string language, bool dark)
    {
        ClipWindowTests.WithClipWindow(language, dark, ["Match highlights.mp4"], (window, vm, frames, _) =>
        {
            vm.MoveHandle(ClipHandle.Start, 95.3);
            vm.SetLength(30);
            // Cut in three: the middle piece out, the last one faster and louder, a fade in.
            vm.MovePlayhead(103);
            vm.Split();
            vm.MovePlayhead(110);
            vm.Split();
            vm.MovePlayhead(106);
            vm.Delete(closeUp: false); // a gap, with its button
            vm.MovePlayhead(118);
            vm.SelectedSpeed = vm.Speeds.Single(s => s.Value == 2);
            vm.SelectedVolume = 1.5;
            vm.HasFadeIn = true;
            vm.IsZoomed = true;
            var adding = vm.AddMusicAsync(Path.Combine("music", "Summer song.mp3"));
            ClipWindowTests.WaitFor(() => vm.MusicLoudness.Count > 0);
            vm.MoveMusic(3);
            vm.MovePlayhead(98);
            vm.SelectPiece(0, Filee.App.Controls.ClipLane.Audio);
            vm.IsCurrentMuted = false;
            // The sequence's own pictures of the clip, taken again once its ends rest.
            ClipWindowTests.WaitFor(() => vm.DetailStart == vm.Start && vm.DetailEnd == vm.End && vm.DetailLoudness.Count > 0);
            ClipWindowTests.WaitFor(() => frames.Times.Contains(vm.PreviewTime));
            Pump();

            Assert.Equal(("1:35.3", "2:05.3"), (vm.StartText, vm.EndText));
            Assert.Equal(7.7 + 7 + 15.3 / 2, vm.Length, 6); // the gap stays until closed
            Assert.True(window.ClipTimeline.Bounds.Width > 600);
            Assert.True(window.ClipTimeline.XOf(vm.Start) < window.ClipTimeline.XOf(vm.End));
            Save(window, $"clip-{language}-{(dark ? "dark" : "light")}.png");
        });
    }

    internal static void Pump()
    {
        // Let layout, bindings and a few animation frames run.
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    internal static void Save(TopLevel window, string name)
    {
        // Disposed at the end, so the frame stays alive while Skia encodes it: a collection in between (the idle memory
        // trim runs one on another thread) would otherwise finalize its image under the encoder.
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, name));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
