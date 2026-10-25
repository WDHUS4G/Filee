// Opens windows and dialogs: the main (settings) window, the preset popup, confirmations and toasts.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class WindowService(ILocalizer loc, IServiceProvider services, MemoryTrimmer trimmer)
{
    private MainWindow? _main;
    private MainWindowPlacement? _mainPlacement;
    private ToastWindow? _toast;
    private EngineSetupWindow? _engineSetup;
    private UpdateNoticeWindow? _updateNotice;
    private UpdateProgressWindow? _updateProgress;
    internal UpdateProgressWindow? UpdateProgress => _updateProgress;
    private EngineUpdateNoticeWindow? _engineNotice;
    private FeedbackNoticeWindow? _feedbackNotice;
    private FormatRequestNoticeWindow? _formatRequestNotice;
    private WhatsNewWindow? _whatsNew;

    internal FormatRequestNoticeWindow? FormatRequestNotice => _formatRequestNotice;

    private CropOverlayWindow? _cropOverlay;
    private ClipWindow? _clipWindow;

    internal ClipWindow? ClipWindow => _clipWindow;
    private readonly HashSet<SpreadsheetPrintStudioWindow> _spreadsheetStudios = [];

    internal IReadOnlyCollection<SpreadsheetPrintStudioWindow> SpreadsheetStudios => _spreadsheetStudios;
    private MergeOrderWindow? _mergeOrder;
    private PdfPageEditorWindow? _pdfPageEditor;

    internal MergeOrderWindow? MergeOrder => _mergeOrder;
    internal PdfPageEditorWindow? PdfPageEditor => _pdfPageEditor;

    private void PrepareWindow(Window window) => services.GetService<ThemeService>()?.ApplyWindowClass(window);

    /// <summary>Reviews the order of a PDF merge. Closing or cancelling starts no conversion.</summary>
    public async Task<IReadOnlyList<string>?> ChooseMergeOrderAsync(IReadOnlyList<string> files)
    {
        _mergeOrder?.Close();
        var dialog = new MergeOrderWindow { DataContext = new MergeOrderViewModel(files) };
        PrepareWindow(dialog);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) =>
        {
            if (_mergeOrder == dialog) _mergeOrder = null;
            closed.TrySetResult();
        };
        _mergeOrder = dialog;
        dialog.Show();
        dialog.Activate();
        await closed.Task;
        return dialog.Result;
    }

    public MainWindow? Main => _main;

    /// <summary>
    /// Opens the window of a tool preset (crop, cut a clip, the spreadsheet print studio: one per workbook, the PDF page
    /// editor) on the files. A tool this version doesn't know opens nothing.
    /// </summary>
    public void ShowTool(IReadOnlyList<string> files, Preset preset, PixelPoint? near = null)
    {
        if (preset.IsClipTool)
            ShowClipWindow(files, preset, near);
        // Before the crop: the print studio and the PDF page tools have no Tool of their own, which reads as a crop
        // ("Crop PDF" is one and opens the crop overlay).
        else if (preset.Id == "tool-sheet-studio")
        {
            foreach (var workbook in files.Where(file => Path.GetExtension(file).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)))
                ShowSpreadsheetPrintStudio(workbook);
        }
        else if (preset.Id == "tool-pdf-pages")
            ShowPdfPageEditor(files, preset);
        else if (preset.Id == "tool-pdf-add")
            ShowPdfPageEditor(files, preset, addPages: true);
        else if (preset.IsCropTool)
            ShowCropOverlay(files, preset, near);
    }

    /// <summary>
    /// Opens the "Cut a clip" window on the videos among the files. Each clip is written by an ordinary conversion,
    /// shown in the toast.
    /// </summary>
    public void ShowClipWindow(IReadOnlyList<string> files, Preset preset, PixelPoint? near = null)
    {
        var videos = files.Where(f => FormatRegistry.Detect(f)?.Category == FormatCategory.Video).ToList();
        if (videos.Count == 0)
            return;
        _clipWindow?.Close();
        var store = services.GetRequiredService<UserDataStore>();
        var catalog = services.GetRequiredService<ConverterCatalog>();
        var conversions = services.GetRequiredService<ConversionService>();
        var log = services.GetService<ILogger<ClipWindowViewModel>>();
        var vm = new ClipWindowViewModel(videos, preset, loc, store, new FfmpegFrameSource(),
            path => ClipTool.TargetFor(FormatRegistry.Detect(path)?.Id ?? ClipTool.FallbackTarget, catalog.CreatePlanner()),
            (path, conversion) => conversions.Start([path], conversion), log);
        var win = new ClipWindow { DataContext = vm };
        win.PositionOnScreen(near);
        win.Closed += (_, _) =>
        {
            if (_clipWindow == win)
                _clipWindow = null;
            trimmer.Request();
        };
        _clipWindow = win;
        win.Show();
        win.Activate();
    }

    /// <summary>Opens the interactive crop overlay for the files.</summary>
    public void ShowCropOverlay(IReadOnlyList<string> files, Preset preset, PixelPoint? near = null)
    {
        if (files.Count == 0)
            return;
        _cropOverlay?.Close();
        var store = services.GetRequiredService<UserDataStore>();
        var log = services.GetService<ILogger<CropOverlayViewModel>>();
        var vm = new CropOverlayViewModel(files, preset, loc, store, log);
        var win = new CropOverlayWindow { DataContext = vm };
        PrepareWindow(win);
        win.PositionOnScreen(near);
        win.Closed += (_, _) =>
        {
            if (_cropOverlay == win)
                _cropOverlay = null;
        };
        _cropOverlay = win;
        win.Show();
        win.Activate();
    }

    /// <summary>Opens the lossless PDF page organiser. The Add-pages action starts with its file picker open.</summary>
    public void ShowPdfPageEditor(IReadOnlyList<string> files, Preset preset, bool addPages = false)
    {
        if (files.Count == 0)
            return;
        _pdfPageEditor?.Close();
        var store = services.GetRequiredService<UserDataStore>();
        var log = services.GetService<ILogger<PdfPageEditorViewModel>>();
        var vm = new PdfPageEditorViewModel(files, preset, loc, store, log);
        var win = new PdfPageEditorWindow { DataContext = vm };
        PrepareWindow(win);
        win.Closed += (_, _) =>
        {
            if (_pdfPageEditor == win)
                _pdfPageEditor = null;
        };
        if (addPages)
            win.Opened += async (_, _) => await win.PickAndAddAsync();
        _pdfPageEditor = win;
        win.Show();
        win.Activate();
    }

    /// <summary>Opens the non-destructive XLSX print studio. One workbook owns one window and print plan.</summary>
    public void ShowSpreadsheetPrintStudio(string file)
    {
        SpreadsheetPrintStudioViewModel vm;
        try
        {
            vm = ActivatorUtilities.CreateInstance<SpreadsheetPrintStudioViewModel>(services, file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            services.GetService<ILogger<WindowService>>()?.LogWarning(ex, "Could not open spreadsheet print studio for {File}", file);
            _ = MessageAsync(null, ex.Message);
            return;
        }
        var window = new SpreadsheetPrintStudioWindow { DataContext = vm };
        window.Closed += (_, _) =>
        {
            _spreadsheetStudios.Remove(window);
            trimmer.Request();
        };
        _spreadsheetStudios.Add(window);
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Shows (or brings to front) the main window, optionally on a page. Going to the tray closes it
    /// (<see cref="MainWindow.HideToTray"/>): a new one opens where and on the page the last one was. There is never
    /// more than one: a call made while one is being built (a tray click handled meanwhile) goes to that one, and one
    /// that is being released is left to close while a new one opens.
    /// </summary>
    public void ShowMain(string? page = null)
    {
        if (_openingMain)
        {
            _openingMainPage = page ?? _openingMainPage;
            return;
        }
        Motion.Refresh();
        if (_main is { IsReleased: true })
            _main = null; // closing for real (or closed): it can't be shown again
        if (_main is null)
        {
            _openingMain = true;
            _openingMainPage = null;
            try
            {
                _main = CreateMain(page);
            }
            finally
            {
                _openingMain = false;
            }
            page = _openingMainPage ?? page;
        }
        var main = _main;
        // Any other main window still alive goes to the tray (and is released) instead of staying next to this one.
        foreach (var other in _mains.ToArray())
            if (other != main && !other.IsReleased)
                other.HideToTray();
        if (page is not null && main.DataContext is MainWindowViewModel vm)
            vm.Navigate(page);

        if (!main.IsVisible)
            main.Show();
        if (main.WindowState == WindowState.Minimized)
            main.WindowState = WindowState.Normal;
        main.Activate();
        MainShown?.Invoke(this, EventArgs.Empty);
    }

    private MainWindow CreateMain(string? page)
    {
        var model = ActivatorUtilities.CreateInstance<MainWindowViewModel>(services);
        var main = new MainWindow { DataContext = model };
        PrepareWindow(main);
        if (_mainPlacement is { } placement)
        {
            placement.ApplyTo(main);
            if (page is null)
                model.Navigate(placement.Page);
        }
        main.Closing += (_, _) =>
        {
            // While it is still on screen: the release closes it hidden, when its position may no longer be known.
            if (main.IsVisible)
                _mainPlacement = MainWindowPlacement.Of(main, model, _mainPlacement);
        };
        main.Closed += (_, _) =>
        {
            _mains.Remove(main);
            if (_main == main)
                _main = null;
            model.Dispose();
            trimmer.Request();
        };
        _mains.Add(main);
        return main;
    }

    // Every main window that isn't closed yet: normally just _main, plus one that is being released.
    private readonly List<MainWindow> _mains = [];
    private bool _openingMain;
    private string? _openingMainPage;

    /// <summary>Raised whenever <see cref="ShowMain"/> showed the main window (the user opened Filee).</summary>
    public event EventHandler? MainShown;

    /// <summary>
    /// Shows a version's "What's new" slides, centred on the main window when it is open; a second call replaces the
    /// open window's slides.
    /// </summary>
    public void ShowWhatsNew(Controls.SlideDeck deck)
    {
        _whatsNew?.Close();
        Motion.Refresh();
        var window = new WhatsNewWindow(deck, loc);
        PrepareWindow(window);
        window.Closed += (_, _) =>
        {
            if (_whatsNew == window)
                _whatsNew = null;
            trimmer.Request();
        };
        _whatsNew = window;
        if (_main is { IsVisible: true } main && main.WindowState != WindowState.Minimized)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Show(main);
        }
        else
            window.Show();
        window.Activate();
    }

    internal WhatsNewWindow? WhatsNew => _whatsNew;

    /// <summary>Opens the optional-engine choice (first run); a second call brings the open window to front.</summary>
    /// <summary>
    /// Shows the engine choice. With <paramref name="install"/> (packages picked in the installer) the downloads start
    /// right away, and nothing opens when all of them are installed already.
    /// </summary>
    public void ShowEngineSetup(IReadOnlyCollection<string>? install = null)
    {
        if (_engineSetup is not null)
        {
            if (install is not null && _engineSetup.DataContext is EngineSetupViewModel open)
                open.InstallNow(install);
            _engineSetup.Activate();
            return;
        }
        var vm = ActivatorUtilities.CreateInstance<EngineSetupViewModel>(services);
        if (install is not null && !vm.InstallNow(install))
            return;
        _engineSetup = new EngineSetupWindow { DataContext = vm };
        PrepareWindow(_engineSetup);
        _engineSetup.Closed += (_, _) => _engineSetup = null;
        _engineSetup.Show();
    }

    /// <summary>Creates the toast window once; it shows and hides itself with the job list.</summary>
    public void EnsureToast()
    {
        if (_toast is null)
        {
            _toast = new ToastWindow { DataContext = services.GetRequiredService<ConversionService>() };
            PrepareWindow(_toast);
        }
    }

    /// <summary>Shows the "new version available" card in the bottom-right corner (above a visible toast).</summary>
    public void ShowUpdateNotice(string version)
    {
        _updateNotice?.Close();
        var notice = new UpdateNoticeWindow(services.GetRequiredService<UpdateService>(), loc, loc.Format("update.notice_title", version))
        {
            BottomOffset = _toast is { IsVisible: true } toast
                ? (int)Math.Ceiling(toast.Bounds.Height * (toast.Screens.Primary?.Scaling ?? 1))
                : 0,
        };
        PrepareWindow(notice);
        notice.Closed += (_, _) =>
        {
            if (_updateNotice == notice)
                _updateNotice = null;
        };
        _updateNotice = notice;
        notice.Show();
    }

    /// <summary>
    /// Starts downloading and installing an update, and opens the progress dialog.
    /// Used by the sidebar, About page, and tray menu (the update notice has its own progress bar).
    /// </summary>
    public async Task StartUpdateAsync(UpdateService updates)
    {
        if (updates.IsBusy)
        {
            ShowUpdateProgress(updates);
            return;
        }

        var update = updates.UpdateAsync();
        ShowUpdateProgress(updates);
        await update;
    }

    /// <summary>Shows the update download progress dialog with a progress bar.</summary>
    public void ShowUpdateProgress(UpdateService updates)
    {
        if (!updates.IsBusy && updates.Step == UpdateStep.Idle)
            return;

        if (_updateProgress is not null)
        {
            _updateProgress.Activate();
            return;
        }

        _updateNotice?.Close();
        var dialog = new UpdateProgressWindow(updates, loc);
        if (_main is { IsVisible: true } main && main.WindowState != WindowState.Minimized)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Show(main);
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
            dialog.Show();
        }

        dialog.Closed += (_, _) =>
        {
            if (_updateProgress == dialog)
                _updateProgress = null;
        };
        _updateProgress = dialog;
        dialog.Activate();
    }

    /// <summary>
    /// Says that engines the user downloaded have a newer version. "Update" queues them (one after another) and opens
    /// Settings → Engines to show the progress.
    /// </summary>
    public void ShowEngineUpdateNotice(IReadOnlyList<EnginePackageState> outdated)
    {
        _engineNotice?.Close();
        var downloads = services.GetRequiredService<EngineDownloadService>();
        var caption = string.Join(" · ", outdated.Select(p => $"{p.Name.Split(" (")[0]} {p.Version}"));
        var notice = new EngineUpdateNoticeWindow(caption, () =>
        {
            foreach (var package in outdated)
                _ = downloads.InstallAsync(package);
            ShowMain("engines");
        })
        {
            BottomOffset = _updateNotice is { IsVisible: true } update
                ? (int)Math.Ceiling(update.Bounds.Height * (update.Screens.Primary?.Scaling ?? 1))
                : 0,
        };
        PrepareWindow(notice);
        notice.Closed += (_, _) =>
        {
            if (_engineNotice == notice)
                _engineNotice = null;
        };
        _engineNotice = notice;
        notice.Show();
    }

    /// <summary>Shows the one-time "Is Filee helping you?" card in the bottom-right corner.</summary>
    public void ShowFeedbackNotice()
    {
        if (_feedbackNotice is not null)
            return;
        _feedbackNotice = new FeedbackNoticeWindow();
        PrepareWindow(_feedbackNotice);
        _feedbackNotice.Closed += (_, _) => _feedbackNotice = null;
        _feedbackNotice.Show();
    }

    /// <summary>Shows the "Filee is running as administrator" card in the bottom-right corner (once per run).</summary>
    public void ShowElevationNotice()
    {
        if (_elevationNoticeShown)
            return;
        _elevationNoticeShown = true;
        var elevation = new ElevationNoticeWindow(services.GetRequiredService<ElevationService>());
        PrepareWindow(elevation);
        elevation.Show();
    }

    private bool _elevationNoticeShown;

    /// <summary>
    /// Shows the "Filee can't convert .ncm files yet" card with "Request this format", replacing an earlier one.
    /// </summary>
    public void ShowFormatRequestNotice(IReadOnlyList<string> extensions)
    {
        _formatRequestNotice?.Close();
        var notice = new FormatRequestNoticeWindow(extensions, loc.Format("format_request.notice_title", FormatRequest.Label(extensions)));
        PrepareWindow(notice);
        notice.Closed += (_, _) =>
        {
            if (_formatRequestNotice == notice)
                _formatRequestNotice = null;
        };
        _formatRequestNotice = notice;
        notice.Show();
    }

    /// <summary>Opens the preset popup. Returns true when the user saved (the preset object was updated).</summary>
    public async Task<bool> EditPresetAsync(Preset preset, Window? owner)
    {
        // A group converts nothing: it gets its own editor (name and the presets it fans out).
        if (preset.IsGroup)
            return await EditGroupAsync(preset, owner);
        var dialog = new PresetEditorWindow
        {
            DataContext = new PresetEditorViewModel(preset, loc),
            Topmost = owner?.Topmost ?? false,
        };
        PrepareWindow(dialog);
        owner ??= _main ?? MainWindowOrNull();
        if (owner is null || !owner.IsVisible)
        {
            // No visible owner (e.g. the donut was opened from the tray): show as a normal window.
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            await closed.Task;
            return dialog.Saved;
        }
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// Opens the group editor. Returns true when the user saved (the group was updated) or deleted it (it was removed
    /// from the presets and every donut). <paramref name="isNew"/>: not in the store yet, so it can't be deleted.
    /// </summary>
    public async Task<bool> EditGroupAsync(Preset group, Window? owner, bool isNew = false)
    {
        owner ??= _main ?? MainWindowOrNull();
        var visibleOwner = owner is { IsVisible: true } ? owner : null;
        var dialog = new GroupEditorWindow
        {
            DataContext = new GroupEditorViewModel(group, services.GetRequiredService<UserDataStore>(), loc, isNew),
            Topmost = owner?.Topmost ?? false,
            Confirm = (editor, message) => ConfirmAsync(editor, message, destructive: true),
        };
        PrepareWindow(dialog);
        if (visibleOwner is null)
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            await closed.Task;
            return dialog.Changed;
        }
        return await dialog.ShowDialog<bool>(visibleOwner);
    }

    /// <summary>Simple yes/no confirmation.</summary>
    public Task<bool> ConfirmAsync(Window owner, string message, bool destructive = false) =>
        ShowMessageAsync(owner, message, destructive, withCancel: true);

    /// <summary>A message with only an OK button. Without a visible owner it shows on its own, centred on the screen.</summary>
    public Task MessageAsync(Window? owner, string message) => ShowMessageAsync(owner, message, destructive: false, withCancel: false);

    /// <summary>The in-app update failed: say so, then open the website to download the new version there.</summary>
    public async Task ShowUpdateFailedAsync()
    {
        _updateNotice?.Close();
        _updateProgress?.Close();
        await MessageAsync(_main is { IsVisible: true } main ? main : null, loc["update.failed_alert"]);
        services.GetRequiredService<UpdateService>().OpenDownloadPage();
    }

    /// <summary>
    /// The last run's in-app update (Linux) failed while installing and the installer put the old version back: say so,
    /// with the log that has the details, then open the website to download the new version there.
    /// </summary>
    public async Task ShowUpdateRolledBackAsync(string? logPath)
    {
        _updateNotice?.Close();
        var message = loc["update.rolled_back_alert"];
        if (!string.IsNullOrWhiteSpace(logPath))
            message += "\n\n" + loc.Format("update.rolled_back_log", logPath);
        await MessageAsync(_main is { IsVisible: true } main ? main : null, message);
        services.GetRequiredService<UpdateService>().OpenDownloadPage();
    }

    private async Task<bool> ShowMessageAsync(Window? owner, string message, bool destructive, bool withCancel)
    {
        var dialog = new Window
        {
            Title = loc["app.name"],
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        PrepareWindow(dialog);
        Motion.Track(dialog);
        var yes = new Button { Content = destructive ? loc["common.delete"] : loc["common.ok"], IsDefault = true };
        yes.Classes.Add("accent");
        var no = new Button { Content = loc["common.cancel"], IsCancel = true };
        var answer = false;
        yes.Click += (_, _) => { answer = true; dialog.Close(true); };
        no.Click += (_, _) => dialog.Close(false);
        no.IsVisible = withCancel;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { no, yes },
                },
            },
        };
        UiZoom.Track(dialog);
        if (owner is { IsVisible: true })
            return await dialog.ShowDialog<bool>(owner);

        // From the tray or the bottom-right notice there is no window to sit on: a normal window on top instead.
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowInTaskbar = true;
        dialog.Topmost = true;
        dialog.Show();
        dialog.Activate();
        await closed.Task;
        return answer;
    }

    private static Window? MainWindowOrNull() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>Where the main window was and which page it showed, for the next one.</summary>
    private sealed record MainWindowPlacement(PixelPoint? Position, Size? Size, bool Maximized, string Page)
    {
        /// <summary>The window's placement. Maximized or minimized, it keeps the position and size it had before.</summary>
        public static MainWindowPlacement Of(MainWindow window, MainWindowViewModel vm, MainWindowPlacement? last) =>
            window.WindowState switch
            {
                WindowState.Normal => new(window.Position, window.ClientSize, false, vm.SelectedItem.Key),
                WindowState.Maximized => (last ?? new(null, null, false, "")) with { Maximized = true, Page = vm.SelectedItem.Key },
                _ => (last ?? new(null, null, false, "")) with { Page = vm.SelectedItem.Key },
            };

        public void ApplyTo(MainWindow window)
        {
            // Only on a screen that is still there (a monitor may have been unplugged since).
            if (Position is { } position && window.Screens.ScreenFromPoint(position) is not null)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = position;
            }
            if (Size is { } size)
            {
                window.Width = size.Width;
                window.Height = size.Height;
            }
            if (Maximized)
                window.WindowState = WindowState.Maximized;
        }
    }
}
