// One running / finished conversion as shown in the toast window.

using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;

namespace Filee.App.ViewModels;

public sealed partial class JobViewModel : ObservableObject
{
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly Action<JobViewModel> _dismiss;
    private readonly Action<JobViewModel>? _retry;
    private int _refreshPending;

    public JobViewModel(ConversionJob job, ILocalizer loc, IPlatformServices platform, Action<JobViewModel> dismiss,
        Action<JobViewModel>? retry = null)
    {
        Job = job;
        _loc = loc;
        _platform = platform;
        _dismiss = dismiss;
        _retry = retry;
        var what = job.Sources.Count == 1 ? Path.GetFileName(job.Sources[0]) : loc.Format("home.files", job.Sources.Count);
        Title = loc.Format("toast.converting", what, job.PresetDisplayName);
        Refresh();
        if (job.Fetcher is not null)
        {
            // Files from a browser are saved first: the speed and size badge, rewritten a few times a second.
            _badge = new DownloadBadge(loc);
            _badgeTimer = new DispatcherTimer(DownloadBadge.Interval, DispatcherPriority.Background, (_, _) => UpdateDownload(_badgeClock.Elapsed));
            _badgeTimer.Start();
        }
    }

    private readonly DownloadBadge? _badge;
    private readonly DispatcherTimer? _badgeTimer;
    private readonly Stopwatch _badgeClock = Stopwatch.StartNew();

    /// <summary>
    /// "2.4 MB/s · 12.3 / 48.0 MB" while the job downloads (saves files from a browser). The last text stays while the
    /// badge pops out, so it doesn't go blank as it shrinks.
    /// </summary>
    [ObservableProperty] private string? _downloadText;

    /// <summary>The download badge shows; false once the files are saved and converting starts.</summary>
    [ObservableProperty] private bool _isDownloading;

    /// <summary>Rewrites the download badge for <paramref name="elapsed"/> since the job was shown.</summary>
    internal void UpdateDownload(TimeSpan elapsed)
    {
        if (_badge is null)
            return;
        var text = _badge.Update(elapsed, Job.Transfer());
        if (text is not null)
            DownloadText = text;
        IsDownloading = text is not null;
        if (Job.State is not (JobState.Queued or JobState.Running))
            _badgeTimer?.Stop();
    }

    public ConversionJob Job { get; }

    public string Title { get; }

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private bool _hasErrors;
    [ObservableProperty] private bool _hasOutputs;
    [ObservableProperty] private string? _errorDetails;
    [ObservableProperty] private bool _canRetry;

    /// <summary>Notes on files that converted (e.g. a PDF that couldn't be made smaller and was copied), one per line.</summary>
    [ObservableProperty] private string? _notices;
    [ObservableProperty] private bool _hasNotices;
    /// <summary>Absent document font families, combined across this drop.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingFonts), nameof(CanGetFonts))]
    private IReadOnlyList<string> _missingFonts = [];
    [ObservableProperty] private string? _missingFontsText;
    public bool HasMissingFonts => MissingFonts.Count > 0;
    public bool CanGetFonts => MissingFonts.Any(family => FontSources.Url(family) is not null);
    /// <summary>Extensions of failed files Filee can't read (".ncm"), for "Request this format".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRequestFormat))]
    private IReadOnlyList<string> _unknownExtensions = [];

    public bool CanRequestFormat => UnknownExtensions.Count > 0;

    /// <summary>Source files that failed.</summary>
    public IEnumerable<string> FailedSources => Job.Files.Where(f => f.State == FileState.Failed).Select(f => f.SourcePath);

    /// <summary>Marks a refresh as pending; returns false if one is already queued (coalesces UI updates).</summary>
    internal bool TryMarkPending() => Interlocked.Exchange(ref _refreshPending, 1) == 0;

    /// <summary>Copies job state into bindable properties. UI thread only.</summary>
    public void Refresh()
    {
        Interlocked.Exchange(ref _refreshPending, 0);
        Progress = Job.Progress * 100;
        IsRunning = Job.State is JobState.Queued or JobState.Running;
        HasOutputs = Job.Outputs.Any();

        var failed = Job.Files.Where(f => f.State == FileState.Failed).ToList();
        HasErrors = failed.Count > 0;
        ErrorDetails = failed.Count == 0 ? null : string.Join(Environment.NewLine,
            failed.Select(f => $"{Path.GetFileName(f.SourcePath)} — {FailureText.Reason(_loc, f.ErrorKey, f.ErrorDetail)}"));
        // A file from a browser that couldn't be saved, or whose original wasn't kept, is gone: nothing to retry with.
        CanRetry = _retry is not null && !IsRunning && failed.Count > 0
                   && (Job.Fetcher is null || failed.Any(f => File.Exists(f.SourcePath)));
        IReadOnlyList<string> unknown = IsRunning ? [] : FormatRequest.UnknownExtensions(
            failed.Where(f => f.ErrorKey == "error.unsupported_source").Select(f => f.SourcePath));
        if (!unknown.SequenceEqual(UnknownExtensions))
            UnknownExtensions = unknown;

        var noted = Job.Files.Where(f => f.State == FileState.Done && f.NoticeKey is not null).ToList();
        HasNotices = noted.Count > 0;
        Notices = noted.Count == 0 ? null : string.Join(Environment.NewLine,
            noted.Select(f => $"{Path.GetFileName(f.SourcePath)} — {_loc.Notice(f)}"));

        var missingFonts = Job.Files.SelectMany(f => f.MissingFonts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!missingFonts.SequenceEqual(MissingFonts, StringComparer.OrdinalIgnoreCase))
            MissingFonts = missingFonts;
        MissingFontsText = missingFonts.Count == 0 ? null
            : _loc.Format("toast.missing_fonts", LocalizationService.LeftToRight(string.Join(", ", missingFonts)));

        Status = Job.State switch
        {
            JobState.Completed when Job.Files.All(f => f.State == FileState.Skipped) => _loc["toast.skipped"],
            JobState.Completed => _loc["toast.done"],
            JobState.CompletedWithErrors => _loc.Format("toast.done_errors", failed.Count, Job.Files.Count),
            JobState.Failed => _loc["toast.failed"],
            JobState.Cancelled => _loc["toast.cancelled"],
            _ => $"{Progress:0}%",
        };
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var first = Job.Outputs.FirstOrDefault();
        if (first is not null)
            _platform.RevealInFileManager(first);
    }

    [RelayCommand]
    private void Cancel() => Job.Cancel();

    [RelayCommand]
    private void Dismiss() => _dismiss(this);

    /// <summary>Converts the files that failed again (a new job in the toast in place of this one).</summary>
    [RelayCommand]
    private void Retry() => _retry?.Invoke(this);
}
