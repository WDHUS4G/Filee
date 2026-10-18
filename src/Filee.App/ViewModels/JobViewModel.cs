// One running / finished conversion as shown in the toast window.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        CanRetry = _retry is not null && !IsRunning && failed.Count > 0;

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
