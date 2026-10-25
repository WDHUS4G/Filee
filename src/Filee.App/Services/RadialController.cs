// Opens, fills and closes the live donut toolbar.
//
// Drag mode  (modifier + drag in Explorer):
//   gesture detected → an invisible window waits at the cursor (not activated) → DragEnter brings the file list →
//   the donut pops out → drop on a slice converts / drop in the hole or release elsewhere closes. Without files
//   (text, a rubber-band selection) nothing ever shows.
// Files from a web browser (a picture, a link to a PDF) stand as placeholder paths named like the files will be, so the
// donut shows their slices at once; they are saved when the conversion starts (WebDrop, ConversionService).
// Click mode (keyboard shortcut, context menu, drop zone):
//   files are known up front → donut appears activated → click a slice to convert, right-click to edit,
//   Esc / click outside closes.
// The flip key (Tab) and the mouse wheel flip the open donut to its other face (DonutMenu.Flip.cs): in click mode the
// focused window gets them, in drag mode (no focus during an OS drag) the global hook reports them while the donut
// shows files.
// After a drop / click the donut becomes a progress ring at the same spot (DonutMenu.Progress.cs):
//   finished quickly → ✓ pops up in the centre, then the ring fades out;
//   still running after HandOffAfter, failed, Esc, click elsewhere or a new gesture → the toast takes over.

using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services.Triggers;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Filee.Core.Web;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class RadialController(
    RadialViewModel viewModel,
    ConversionService conversions,
    WindowService windows,
    UserDataStore store,
    IPlatformServices platform,
    TriggerService triggers,
    ILogger<RadialController> log)
{
    /// <summary>
    /// How long the ring shows a running job before the toast window takes over. Long enough for a typical office
    /// document (LibreOffice needs a few seconds), so the user sees it finish where they dropped it.
    /// </summary>
    private static readonly TimeSpan HandOffAfter = TimeSpan.FromSeconds(5);

    private RadialWindow? _window;
    private bool _visible;
    private bool _dropped;
    private bool _editing;
    private JobViewModel? _progressJob;   // job currently shown by the ring (null once finished / handed off)
    private JobViewModel? _lastRingJob;   // for "click the ✓ to open the folder"
    private IDisposable? _handOffTimer;
    private int _dragGeneration;
    private bool _dragReachedDonut;
    private bool _fileDragsAccepted;
    private readonly ActivationGrace _grace = new();

    /// <summary>A gesture over something that isn't a file drag (a rubber-band selection) is common: a line now and then.</summary>
    private static readonly LogThrottle Throttle = new(TimeSpan.FromMinutes(1));
    private bool _converted;   // a conversion started from the files on the donut now
    private readonly HashSet<string> _formatRequestsOffered = new(StringComparer.OrdinalIgnoreCase);
    private WebDrop? _web;      // the browser drop the click-mode donut's files stand for, until a conversion takes it

    /// <summary>
    /// How long a drag that never brought files may keep the invisible window up, in case the button release is
    /// missed (the system dropped the input hook, a UAC prompt took the desktop): the window catches clicks.
    /// </summary>
    private static readonly TimeSpan EmptyDragLimit = TimeSpan.FromSeconds(10);

    public bool IsVisible => _visible;

    internal RadialWindow? Window => _window;

    /// <summary>
    /// Drag gesture started at the given physical screen point. The window is placed under the cursor but stays
    /// invisible until files are dragged into it (FilesEntered); a gesture without files (text, a rubber-band
    /// selection, nothing at all) never shows anything and closes when the button is released.
    /// </summary>
    public void ShowForDrag(int x, int y)
    {
        ReleaseProgressToToast();
        _grace.Cancel();
        ReplaceWeb(null);
        var window = EnsureWindow();
        // A ring from an earlier conversion (finished or handed to the toast) must not keep this gesture's window up.
        window.DonutControl.LeaveProgressMode();
        _dropped = false;
        _dragReachedDonut = false;
        _converted = false;
        viewModel.ResetForDrag();
        window.DonutControl.AllowContextEdit = false;
        PlaceAndShow(window, x, y, activate: false);
        WatchFlipKey();

        var generation = ++_dragGeneration;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == _dragGeneration && _visible && !viewModel.HasFiles && !viewModel.IsClickMode)
                Close(animate: false);
        }, EmptyDragLimit);
    }

    /// <summary>
    /// Opens the donut for known files (click mode). Shortcuts among them stand for the files they point to.
    /// <paramref name="forAnotherProcess"/>: the files came from a later launch or the Services menu, so on macOS the
    /// donut may lose focus once while Filee becomes the active app. <paramref name="web"/>: the files are placeholders
    /// of this browser drop (the drop zone), which the donut now owns.
    /// </summary>
    public void ShowForFiles(int x, int y, IReadOnlyList<string> files, bool forAnotherProcess = false, WebDrop? web = null)
    {
        if (web is null)
            files = platform.ResolveShortcuts(files);
        if (files.Count == 0)
        {
            web?.Dispose();
            return;
        }
        ReleaseProgressToToast();
        ReplaceWeb(web);
        if (forAnotherProcess && OperatingSystem.IsMacOS())
            _grace.Start();
        else
            _grace.Cancel();
        var window = EnsureWindow();
        _dropped = false;
        _converted = false;
        viewModel.Load(files, clickMode: true);
        viewModel.FitToThisDonut();
        window.DonutControl.AllowContextEdit = true;
        PlaceAndShow(window, x, y, activate: true);
        WatchFlipKey();
        window.DonutControl.PlayOpenAnimation();
    }

    /// <summary>The mouse button of the drag gesture was released somewhere.</summary>
    public void OnDragGestureEnded()
    {
        if (!_visible || viewModel.IsClickMode)
            return;
        // No drag ever entered the donut's window: nothing was dragged (a rubber-band selection), or Windows kept
        // Explorer's drag out (Filee runs as administrator). Tells "the donut didn't react" reports apart.
        if (!_dragReachedDonut && Throttle.TryEnter("no-drag-enter", out var held))
            log.LogInformation("Drag gesture ended without a drag entering the donut{Elevation}{Held}",
                Program.Elevation.ShellCannotReachFilee ? " (Filee runs as administrator: Windows blocks drops from Explorer)" : "",
                LogThrottle.HeldText(held));
        // Nothing was brought to the donut: the window is invisible but catches clicks, so go right away.
        if (!viewModel.HasFiles && !_window!.DonutControl.IsInProgressMode)
        {
            Close(animate: false);
            return;
        }
        // A drop on the donut arrives around the same time as the button release; give it a moment.
        DispatcherTimer.RunOnce(() =>
        {
            if (_visible && !_dropped && !viewModel.IsClickMode)
                Close(animate: true);
        }, TimeSpan.FromMilliseconds(250));
    }

    public void Close(bool animate = true)
    {
        if (!_visible || _window is null)
            return;
        if (_window.DonutControl.IsInProgressMode)
        {
            // Esc / click elsewhere while the ring is showing: let the toast carry on (a finishing ✓ just fades).
            if (_progressJob is not null)
            {
                HandOffToToast();
                return;
            }
            if (_window.DonutControl.IsFinishingProgress)
                return;
            // A ring left over after it finished: nothing is showing any more, so close like any donut.
            _window.DonutControl.LeaveProgressMode();
        }
        _visible = false;
        WatchFlipKey();
        _grace.Cancel();
        ReplaceWeb(null);
        var window = _window;
        // A group about to open (its dwell timer running) must not open on a closing donut.
        window.DonutControl.CollapseGroup();
        if (animate)
            window.DonutControl.PlayCloseAnimation(() =>
            {
                if (!_visible)
                    window.Hide();
            });
        else
            window.Hide();
        OfferFormatRequest();
    }

    /// <summary>
    /// The donut closed on files Filee can't read at all (".ncm"): offer to request the format, once per extension
    /// while Filee runs. Files it reads, even mixed with unknown ones, get a conversion and the toast instead.
    /// </summary>
    private void OfferFormatRequest()
    {
        if (_converted || viewModel.Files.Count == 0 || ProfileSelector.DetectFormats(viewModel.Files).Count > 0)
            return;
        var extensions = FormatRequest.UnknownExtensions(viewModel.Files).Where(_formatRequestsOffered.Add).ToList();
        if (extensions.Count == 0)
            return;
        log.LogInformation("Donut closed on files Filee can't read: {Extensions}", FormatRequest.Label(extensions));
        windows.ShowFormatRequestNotice(extensions);
    }

    private RadialWindow EnsureWindow()
    {
        if (_window is not null)
            return _window;

        var window = new RadialWindow { DataContext = viewModel };
        var donut = window.DonutControl;

        // On native Wayland, hiding an xdg_toplevel surface causes the compositor to destroy its surface,
        // and re-showing raises an InvalidOperationException. Track Closed to re-create on the next gesture.
        if (Filee.Platform.Linux.WaylandSupport.NativeWaylandRequested)
        {
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window))
                    _window = null;
            };
        }

        window.DragEntered += () => _dragReachedDonut = true;
        // Tells "the drag reached the donut but brought no files" apart, with what it brought (file promises, say).
        window.DragWithoutFiles += formats =>
        {
            if (Throttle.TryEnter("drag-without-files", out var held))
                log.LogInformation("A drag without files Filee reads entered the donut; it offered: {Formats}{Held}",
                    formats, LogThrottle.HeldText(held));
        };
        window.FilesEntered += files =>
        {
            if (viewModel.IsClickMode || SameFiles(files, viewModel.Files) || SameWebFiles(files, viewModel.Files))
                return;
            viewModel.Load(files, clickMode: false);
            donut.PlayOpenAnimation();
            WatchFlipKey();
        };
        window.Dropped += (hit, dropped) =>
        {
            _dropped = true;
            WatchFlipKey();
            if (hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment)
            {
                Convert(hit.Index, hit.Sub, dropped.Files.Count > 0 ? dropped.Files : viewModel.Files, dropped.Web);
            }
            else
            {
                dropped.Web?.Dispose();
                Close();
            }
        };
        // One line per drag, to tell "the drag never reached the donut" from "it did but nothing lit up" when someone
        // reports a donut that doesn't react.
        window.DragSummary += (events, hit, lit) =>
            log.LogInformation("Drag over the donut: {Events} move event(s), ended on {Kind} {Index}, drawn lit {Lit:0.00}",
                events, hit.Kind, hit.Index, lit);
        window.EscapePressed += () => Close();
        window.Deactivated += (_, _) =>
        {
            if (!viewModel.IsClickMode || _editing)
                return;
            if (_grace.Forgives())
            {
                log.LogInformation("The donut lost focus as it opened for another app's request; it stays open");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_visible && viewModel.IsClickMode && !window.IsActive)
                        window.Activate();
                });
                return;
            }
            Close();
        };

        donut.ActiveIndexChanged += (_, index) => viewModel.ShowHint(index, donut.ActiveSubIndex, donut.IsCenterHot);
        // Drag mode: the flip key and the wheel seen by the global hook (click mode gets them in the focused window).
        triggers.FlipKeyPressed += (_, _) =>
        {
            if (FlipsFromHook)
                donut.Flip();
        };
        triggers.FlipWheelTurned += (_, _) =>
        {
            if (FlipsFromHook)
                donut.FlipFromWheel(Environment.TickCount64);
        };
        donut.SegmentInvoked += (_, index) => Convert(index, -1, viewModel.Files, TakeWeb());
        donut.SubSegmentInvoked += (_, hit) => Convert(hit.Group, hit.Sub, viewModel.Files, TakeWeb());
        donut.CenterInvoked += (_, _) => Close();
        donut.SegmentContextRequested += async (_, index) => await EditPresetAsync(index);
        donut.SubSegmentContextRequested += async (_, hit) => await EditPresetAsync(hit.Group, hit.Sub);
        donut.ProgressCancelRequested += (_, _) => _progressJob?.CancelCommand.Execute(null);
        donut.ProgressClicked += (_, _) =>
        {
            if (_lastRingJob is { HasOutputs: true } job)
                job.OpenFolderCommand.Execute(null);
        };

        _window = window;
        return window;
    }

    /// <summary>
    /// Converts with slice <paramref name="index"/>, or with slice <paramref name="sub"/> of that group's ring.
    /// <paramref name="web"/>: the browser drop the files stand for, handed to the conversion (or disposed).
    /// </summary>
    private void Convert(int index, int sub, IReadOnlyList<string> files, WebDrop? web = null)
    {
        // Placeholders whose drop is gone (the drag listed files it didn't hand over in the end) can't be converted.
        var orphaned = web is null && files.Any(WebDrop.IsPlaceholder);
        if (viewModel.PresetAt(index, sub) is not { } preset || SourcesFor(preset, files) is not { Count: > 0 } sources || orphaned)
        {
            web?.Dispose();
            Close();
            return;
        }

        if (preset.IsTool)
        {
            var near = _window?.Position;
            Close(animate: false);
            if (web is null)
                windows.ShowTool(sources, preset, near);
            else
                _ = ShowToolForBrowserFilesAsync(web, preset, near);
            return;
        }

        // Merged PDFs, joined clips and slideshows follow the order the user picks.
        if ((preset.TargetFormat == "pdf" && preset.Pdf.MergeIntoSingle || preset.JoinsIntoOneMedia) && sources.Count > 1)
        {
            Close(animate: false);
            _ = MergeInOrderAsync(sources, preset.Clone(), web);
            return;
        }

        log.LogInformation("Converting {Count} file(s) with preset {Preset}{Web}", sources.Count, preset.Id,
            web is null ? "" : " (from a browser)");
        var job = conversions.Start(sources, preset, showInToast: false, web);
        if (job is null || _window is null)
        {
            Close();
            return;
        }
        _converted = true;
        ShowProgress(job);
    }

    private async Task MergeInOrderAsync(IReadOnlyList<string> files, Preset preset, WebDrop? web)
    {
        try
        {
            var ordered = await windows.ChooseMergeOrderAsync(files);
            if (ordered is not null)
            {
                conversions.Start(ordered, preset, web: web);
                web = null; // the conversion owns the browser drop now
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Could not start the ordered PDF merge");
        }
        finally
        {
            web?.Dispose();
        }
    }

    /// <summary>A tool (crop, clip) on files from a browser: they are saved first, then it opens on them.</summary>
    private async Task ShowToolForBrowserFilesAsync(WebDrop web, Filee.Core.Presets.Preset preset, PixelPoint? near)
    {
        var files = await conversions.SaveBrowserFilesAsync(web, (placeholder, ex) =>
            log.LogWarning(ex, "Saving {File} from a browser for {Preset} failed", Path.GetFileName(placeholder), preset.Id));
        if (files.Count > 0)
            windows.ShowTool(files, preset, near);
    }

    /// <summary>
    /// What <paramref name="preset"/> converts of <paramref name="files"/>: folders only go into "Compress into one
    /// archive"; any other preset converts the files beside them (issue #186).
    /// </summary>
    internal static IReadOnlyList<string> SourcesFor(Preset preset, IReadOnlyList<string> files) =>
        preset.PacksIntoOneArchive ? files : [.. files.Where(path => !Directory.Exists(path))];

    // ───────── Progress ring ─────────

    private void ShowProgress(JobViewModel job)
    {
        var donut = _window!.DonutControl;
        _dropped = true;
        _progressJob = job;
        viewModel.ProgressJob = job;
        _lastRingJob = job;
        donut.BeginProgress();
        donut.SetProgress(job.Progress / 100);
        job.PropertyChanged += OnProgressJobChanged;
        _handOffTimer = DispatcherTimer.RunOnce(() =>
        {
            if (_progressJob == job && job.IsRunning)
                HandOffToToast();
        }, HandOffAfter);
    }

    private void OnProgressJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not JobViewModel job || job != _progressJob || _window is null)
            return;
        var donut = _window.DonutControl;
        donut.SetProgress(job.Progress / 100);
        if (job.IsRunning || e.PropertyName != nameof(JobViewModel.IsRunning))
            return;

        var outcome = job.Job.State switch
        {
            JobState.Completed => DonutProgressOutcome.Succeeded,
            JobState.Cancelled => DonutProgressOutcome.Cancelled,
            _ => DonutProgressOutcome.Failed,
        };
        if (outcome == DonutProgressOutcome.Failed)
            conversions.ShowInToast(job); // the toast explains what went wrong
        DetachProgress();
        donut.CompleteProgress(outcome, HideWindow);
    }

    /// <summary>Hands the running job to the toast with a short "shrink away" of the ring.</summary>
    private void HandOffToToast()
    {
        if (_progressJob is null || _window is null)
            return;
        conversions.ShowInToast(_progressJob);
        DetachProgress();
        _window.DonutControl.HandOffProgress(HideWindow);
    }

    /// <summary>A new gesture needs the window right now: move the job to the toast without animation.</summary>
    private void ReleaseProgressToToast()
    {
        if (_progressJob is not null)
            conversions.ShowInToast(_progressJob);
        DetachProgress();
    }

    private void DetachProgress()
    {
        _handOffTimer?.Dispose();
        _handOffTimer = null;
        if (_progressJob is not null)
            _progressJob.PropertyChanged -= OnProgressJobChanged;
        _progressJob = null;
        viewModel.ProgressJob = null;
    }

    private void HideWindow()
    {
        _visible = false;
        WatchFlipKey();
        _window?.DonutControl.CollapseGroup();
        _window?.Hide();
    }

    /// <summary>Edits the preset of slice <paramref name="index"/>, or of slice <paramref name="sub"/> of its group's ring.</summary>
    private async Task EditPresetAsync(int index, int sub = -1)
    {
        if (_window is null || viewModel.PresetShownAt(index, sub) is not { } preset)
            return;
        _editing = true;
        try
        {
            if (await windows.EditPresetAsync(preset, _window))
            {
                store.SaveLibrary();
                viewModel.Load(viewModel.Files, viewModel.IsClickMode, viewModel.IsBackFace);
            }
        }
        finally
        {
            _editing = false;
        }
        if (_visible)
            _window.Activate();
    }

    /// <summary>
    /// Has the global hook report the flip key and the wheel only while a drag-mode donut shows files that could flip;
    /// they are left alone otherwise (see <see cref="TriggerService.WatchFlipKey"/>).
    /// </summary>
    private void WatchFlipKey()
    {
        triggers.WatchFlipKey = FlipsFromHook;
        // One source per press: the hook in drag mode, the window in click mode (#504).
        if (_window is not null)
            _window.DonutControl.FlipKeyFromHook = FlipsFromHook;
    }

    /// <summary>A drag-mode donut shows files and nothing was dropped yet: the hook's flip key and wheel flip it.</summary>
    private bool FlipsFromHook => _visible && !_dropped && !viewModel.IsClickMode && viewModel.HasFiles;

    /// <summary>
    /// Top-left corner of a <paramref name="sidePx"/> square window centred on <paramref name="cursor"/>, moved into
    /// <paramref name="area"/> when that would cross its edge. With group slices the window holds the ring they fan out
    /// (<see cref="DonutMenu.SideFor"/>), so near a screen edge the donut moves in far enough for every ring to fit.
    /// </summary>
    internal static PixelPoint WindowPosition(PixelPoint cursor, PixelRect? area, int sidePx)
    {
        var position = new PixelPoint(cursor.X - sidePx / 2, cursor.Y - sidePx / 2);
        if (area is not { } a)
            return position;
        return new PixelPoint(
            Math.Clamp(position.X, a.X, Math.Max(a.X, a.Right - sidePx)),
            Math.Clamp(position.Y, a.Y, Math.Max(a.Y, a.Bottom - sidePx)));
    }

    private void PlaceAndShow(RadialWindow window, int x, int y, bool activate)
    {
        var cursor = new PixelPoint(x, y);
        var screen = window.Screens.ScreenFromPoint(cursor) ?? window.Screens.Primary;
        var scaling = OperatingSystem.IsMacOS() ? 1 : screen?.Scaling ?? 1;

        // Window size follows the donut size (SizeToContent); compute it up front to centre on the cursor.
        var side = DonutMenu.SideFor(viewModel.OuterRadius, viewModel.ReserveSubRing);
        var sidePx = (int)Math.Ceiling(side * scaling);
        var position = WindowPosition(cursor, screen?.WorkingArea, sidePx);

        Motion.Refresh();
        window.Position = position;
        if (!window.IsVisible)
            window.Show();
        window.Position = position; // again, in case showing changed the DPI scale
        // Showing a window that is already marked topmost does not move it in the z-order, and Windows can push it
        // below normal windows while it keeps the mark (after the Photos app opened, for one). Then Explorer stays on
        // top of the donut and the drop never reaches it, so put it back on top every time.
        if (window.TryGetPlatformHandle()?.Handle is { } handle)
        {
            platform.RaiseTopmost(handle);
            if (!_fileDragsAccepted && OperatingSystem.IsMacOS() && platform is Filee.Platform.MacOS.MacOSPlatformServices mac)
            {
                mac.AcceptFileDrags(handle);
                _fileDragsAccepted = true;
            }
        }
        _visible = true;
        if (activate)
            window.Activate();
    }

    private static bool SameFiles(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.SequenceEqual(b, FileSystemPaths.Comparer);

    /// <summary>The same browser files dragged in again: each entry makes new placeholders, with the same names.</summary>
    private static bool SameWebFiles(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.All(WebDrop.IsPlaceholder) && b.All(WebDrop.IsPlaceholder)
        && a.Select(Path.GetFileName).SequenceEqual(b.Select(Path.GetFileName), StringComparer.Ordinal);

    /// <summary>Hands the click-mode donut's browser drop to a conversion.</summary>
    private WebDrop? TakeWeb()
    {
        var web = _web;
        _web = null;
        return web;
    }

    /// <summary>The click-mode donut now stands for <paramref name="web"/>'s files (or local ones): the old drop is done.</summary>
    private void ReplaceWeb(WebDrop? web)
    {
        if (!ReferenceEquals(_web, web))
            _web?.Dispose();
        _web = web;
    }
}
