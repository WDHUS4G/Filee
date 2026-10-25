// State of the interactive crop overlay.

using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class CropOverlayViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<string> _files;
    private readonly Preset _preset;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly ILogger? _log;

    private readonly List<string> _processedSources = [];
    private readonly List<string> _outputs = [];
    private readonly List<HistoryFailure> _failures = [];
    private bool _historyRecorded;

    // Set when the window closes. A crop or preview load still running then finishes without touching the closed
    // view (its bitmap disposed, no next file) and records the history with its output.
    private bool _closed;

    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private string _currentFileName = "";
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private bool _hasMultipleFiles;
    [ObservableProperty] private Bitmap? _previewImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginalRatio))]
    [NotifyPropertyChangedFor(nameof(PixelSize))]
    private int _originalWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginalRatio))]
    [NotifyPropertyChangedFor(nameof(PixelSize))]
    private int _originalHeight;

    [ObservableProperty] private string _dimensionsText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _aspectRatio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AspectLabel))]
    [NotifyPropertyChangedFor(nameof(AspectToolTip))]
    [NotifyPropertyChangedFor(nameof(SelectedAspectIndex))]
    [NotifyPropertyChangedFor(nameof(CanSwapOrientation))]
    [NotifyPropertyChangedFor(nameof(IsAspectFree))]
    private string _selectedAspect = "free";

    /// <summary>The fixed ratios (5:4, 4:3, 3:2, 16:9) turned upright: 4:5, 3:4, 2:3, 9:16.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AspectLabel))]
    [NotifyPropertyChangedFor(nameof(AspectToolTip))]
    [NotifyPropertyChangedFor(nameof(OrientationText))]
    private bool _isPortrait;

    [ObservableProperty] private bool _isAspectDialOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusNotice))]
    private string _statusNotice = "";

    public bool HasStatusNotice => !string.IsNullOrWhiteSpace(StatusNotice);

    /// <summary>
    /// All files are done and some failed or were skipped: the overlay stays open on <see cref="Summary"/> and
    /// <see cref="Problems"/> until the user closes it, so a problem with the last (or only) file is seen too.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private bool _isFinished;

    public bool IsEditing => !IsFinished;

    [ObservableProperty] private string _summary = "";

    /// <summary>"file: reason" for each file that failed or was skipped.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>Every file of the run with how it went, for the strip of thumbnails.</summary>
    public ObservableCollection<CropQueueItem> Queue { get; } = [];

    /// <summary>Counts for the end summary's ring.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDone))]
    private int _doneCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkipped))]
    private int _skippedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    private int _failedCount;

    public bool HasDone => DoneCount > 0;
    public bool HasSkipped => SkippedCount > 0;
    public bool HasFailed => FailedCount > 0;

    /// <summary>The primary button: "Crop &amp; next" while more files follow, else "Crop &amp; save".</summary>
    [ObservableProperty] private string _saveText = "";

    public bool IsAspectFree => SelectedAspect == "free";
    public bool CanSwapOrientation => CropMath.CanSwap(SelectedAspect);
    public int SelectedAspectIndex => Math.Max(0, CropMath.AspectIndex(SelectedAspect));
    public double OriginalRatio => OriginalWidth > 0 && OriginalHeight > 0 ? (double)OriginalWidth / OriginalHeight : 0;
    public PixelSize PixelSize => new(OriginalWidth, OriginalHeight);

    /// <summary>The ratio in use, as on the dial's button: "Free", "Original", "1:1", "3:4"…</summary>
    public string AspectLabel => SelectedAspect switch
    {
        "free" => _loc["crop.aspect_free"],
        "original" => _loc["crop.aspect_original"],
        _ => CropMath.Numbers(SelectedAspect, IsPortrait),
    };

    public string AspectToolTip => $"{_loc["crop.aspect_title"]} {AspectLabel}";

    public string OrientationText => _loc[IsPortrait ? "crop.portrait" : "crop.landscape"];

    public event Action? CloseRequested;
    public event Action? ResetRequested;
    public event Action<double>? AspectRatioChanged;

    public Rect CurrentCropRect { get; set; } = new(0.05, 0.05, 0.9, 0.9);

    public CropOverlayViewModel(IReadOnlyList<string> files, Preset preset, ILocalizer loc, UserDataStore store, ILogger? log = null)
    {
        _files = files;
        _preset = preset;
        _loc = loc;
        _store = store;
        _log = log;
        _hasMultipleFiles = files.Count > 1;
        for (var i = 0; i < files.Count; i++)
            Queue.Add(new CropQueueItem(loc, Path.GetFileName(files[i]), i));
        loc.LanguageChanged += OnLanguageChanged;

        _ = LoadFileAsync(0);
    }

    public async Task LoadFileAsync(int index)
    {
        if (index < 0 || index >= _files.Count)
        {
            FinishAndClose();
            return;
        }

        IsBusy = true;
        CurrentIndex = index;
        var path = _files[index];
        CurrentFileName = Path.GetFileName(path);
        CounterText = _loc.Format("crop.page_counter", index + 1, _files.Count);
        SaveText = _loc[index < _files.Count - 1 ? "crop.save_next" : "crop.save"];
        Queue[index].State = CropItemState.Current;

        try
        {
            var (bitmap, origW, origH) = await CropService.LoadPreviewAsync(path);
            if (_closed)
            {
                bitmap.Dispose();
                RecordHistoryOnce();
                return;
            }
            var old = PreviewImage;
            PreviewImage = bitmap;
            old?.Dispose();
            Queue[index].SetThumbnail(bitmap);

            OriginalWidth = origW;
            OriginalHeight = origH;
            SelectedAspect = "free";
            IsPortrait = origH > origW;
            IsAspectDialOpen = false;
            AspectRatio = 0;
            AspectRatioChanged?.Invoke(0);
            CurrentCropRect = new Rect(0.05, 0.05, 0.9, 0.9);
            UpdateDimensions(CurrentCropRect);
            ResetRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to load preview for {File}", path);
            if (_closed)
            {
                RecordHistoryOnce();
                return;
            }
            _processedSources.Add(path);
            // The animated / multi-frame check throws NotSupportedException: its English message stays in the log.
            var unsupported = ex is NotSupportedException;
            AddFailure(path, unsupported ? "error.unsupported_source" : "error.conversion_failed", unsupported ? null : ex.Message);

            if (index < _files.Count - 1)
                await LoadFileAsync(index + 1);
            else
                FinishAndClose();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void UpdateDimensions(Rect norm)
    {
        CurrentCropRect = norm;
        var size = CropMath.PixelSize(norm, OriginalWidth, OriginalHeight);
        DimensionsText = $"{size.Width} × {size.Height} px";
    }

    [RelayCommand]
    public async Task CropAndSaveAsync()
    {
        if (IsBusy || IsFinished || _closed || CurrentIndex >= _files.Count)
            return;

        IsBusy = true;
        IsAspectDialOpen = false;
        var path = _files[CurrentIndex];
        var item = Queue[CurrentIndex];
        _processedSources.Add(path);

        var x = (int)Math.Round(CurrentCropRect.X * OriginalWidth);
        var y = (int)Math.Round(CurrentCropRect.Y * OriginalHeight);
        var w = (int)Math.Round(CurrentCropRect.Width * OriginalWidth);
        var h = (int)Math.Round(CurrentCropRect.Height * OriginalHeight);

        try
        {
            var result = await CropService.CropFileAsync(path, x, y, w, h, _preset, _store, _loc, _log,
                OriginalWidth, OriginalHeight);
            if (result.Status == CropStatus.Success && result.OutputPath is not null)
            {
                _outputs.Add(result.OutputPath);
                item.State = CropItemState.Done;
            }
            else if (result.Status == CropStatus.SkippedExists)
            {
                AddProblem(path, "toast.skipped"); // as in the job queue: skipped counts as done, not as a failure
                item.State = CropItemState.Skipped;
            }
            else
            {
                AddFailure(path, result.ErrorKey ?? "error.conversion_failed", result.ErrorDetail);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to crop {File}", path);
            AddFailure(path, "error.conversion_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (_closed)
        {
            RecordHistoryOnce();
            return;
        }
        if (CurrentIndex < _files.Count - 1)
        {
            await LoadFileAsync(CurrentIndex + 1);
        }
        else
        {
            FinishAndClose();
        }
    }

    [RelayCommand]
    public async Task SkipAsync()
    {
        if (IsBusy || IsFinished || _closed)
            return;

        Queue[CurrentIndex].State = CropItemState.Skipped;
        if (CurrentIndex < _files.Count - 1)
            await LoadFileAsync(CurrentIndex + 1);
        else
            FinishAndClose();
    }

    [RelayCommand]
    public void Reset()
    {
        SelectedAspect = "free";
        IsPortrait = OriginalHeight > OriginalWidth;
        AspectRatio = 0;
        AspectRatioChanged?.Invoke(0);
        ResetRequested?.Invoke();
    }

    /// <summary>Cancel, or Close once finished. A crop still saving finishes and goes into the history.</summary>
    [RelayCommand]
    public void Cancel()
    {
        Shutdown();
        CloseRequested?.Invoke();
    }

    /// <summary>Locks the frame to a ratio of the dial ("free", "original", "1:1", "5:4", "4:3", "3:2", "16:9") and closes the dial.</summary>
    [RelayCommand]
    public void SetAspect(string mode)
    {
        IsAspectDialOpen = false;
        ApplyAspect(mode);
    }

    /// <summary>Turns a fixed ratio between landscape and portrait (4:3 ↔ 3:4); the dial stays open.</summary>
    [RelayCommand]
    public void SwapOrientation()
    {
        if (!CanSwapOrientation || IsFinished)
            return;
        IsPortrait = !IsPortrait;
        ApplyAspect(SelectedAspect);
    }

    /// <summary>The next (or previous) ratio of the dial, round and round.</summary>
    public void CycleAspect(int step)
    {
        var modes = CropMath.AspectModes;
        var next = ((SelectedAspectIndex + step) % modes.Count + modes.Count) % modes.Count;
        ApplyAspect(modes[next]);
    }

    [RelayCommand]
    public void ToggleAspectDial()
    {
        if (IsEditing && !IsBusy)
            IsAspectDialOpen = !IsAspectDialOpen;
    }

    private void ApplyAspect(string mode)
    {
        SelectedAspect = mode;
        AspectRatio = CropMath.Ratio(mode, IsPortrait, OriginalWidth, OriginalHeight);
        AspectRatioChanged?.Invoke(AspectRatio);
    }

    private void AddFailure(string path, string errorKey, string? detail)
    {
        _failures.Add(new HistoryFailure { Source = path, ErrorKey = errorKey, ErrorDetail = detail });
        AddProblem(path, errorKey);
        Queue[CurrentIndex].State = CropItemState.Failed;
    }

    private void AddProblem(string path, string key)
    {
        var line = $"{Path.GetFileName(path)}: {_loc[key]}";
        Problems.Add(line);
        StatusNotice = line;
    }

    /// <summary>Closes after the last file, or stays open on the summary when a file failed or was skipped.</summary>
    private void FinishAndClose()
    {
        RecordHistoryOnce();
        if (Problems.Count == 0)
        {
            CloseRequested?.Invoke();
            return;
        }
        Summary = _failures.Count > 0
            ? _loc.Format("toast.done_errors", _failures.Count, Queue.Count)
            : _loc["toast.done"];
        DoneCount = Queue.Count(q => q.State == CropItemState.Done);
        SkippedCount = Queue.Count(q => q.State == CropItemState.Skipped);
        FailedCount = Queue.Count(q => q.State == CropItemState.Failed);
        StatusNotice = "";
        HasMultipleFiles = false;
        IsAspectDialOpen = false;
        var old = PreviewImage;
        PreviewImage = null;
        old?.Dispose();
        IsFinished = true;
    }

    private void Shutdown()
    {
        _closed = true;
        if (!IsBusy)
            RecordHistoryOnce();
    }

    private void RecordHistoryOnce()
    {
        if (_historyRecorded || !_store.Settings.KeepHistory || _processedSources.Count == 0)
            return;

        _historyRecorded = true;
        var state = _failures.Count == 0
            ? JobState.Completed
            : (_outputs.Count > 0 ? JobState.CompletedWithErrors : JobState.Failed);

        _store.AddHistory(new HistoryEntry
        {
            JobId = Guid.NewGuid().ToString("N")[..12],
            FinishedAt = DateTime.Now,
            PresetId = _preset.Id,
            PresetName = _loc.DisplayName(_preset),
            State = state,
            Sources = [.. _processedSources],
            Outputs = [.. _outputs],
            Failures = [.. _failures],
            // No Retry on Home: it would run the preset through ConversionService, which doesn't run tools.
            Preset = null,
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!IsFinished && CurrentIndex < _files.Count)
        {
            CounterText = _loc.Format("crop.page_counter", CurrentIndex + 1, _files.Count);
            SaveText = _loc[CurrentIndex < _files.Count - 1 ? "crop.save_next" : "crop.save"];
        }
        OnPropertyChanged(nameof(AspectLabel));
        OnPropertyChanged(nameof(AspectToolTip));
        OnPropertyChanged(nameof(OrientationText));
        foreach (var item in Queue)
            item.RefreshTexts();
    }

    public void Dispose()
    {
        Shutdown();
        _loc.LanguageChanged -= OnLanguageChanged;
        PreviewImage?.Dispose();
        PreviewImage = null;
        foreach (var item in Queue)
            item.Dispose();
    }
}

public enum CropItemState
{
    Waiting,
    Current,
    Done,
    Skipped,
    Failed,
}

/// <summary>One file of the crop overlay's strip: a small thumbnail once it has been opened, and how it went.</summary>
public sealed partial class CropQueueItem(ILocalizer loc, string name, int index) : ObservableObject, IDisposable
{
    /// <summary>Longest side of a thumbnail in the strip (at twice its size on screen, for high-DPI displays).</summary>
    private const int ThumbnailSide = 72;

    public string Name { get; } = name;
    public string Number { get; } = (index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrent))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsSkipped))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private CropItemState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool IsCurrent => State == CropItemState.Current;
    public bool IsDone => State == CropItemState.Done;
    public bool IsSkipped => State == CropItemState.Skipped;
    public bool IsFailed => State == CropItemState.Failed;
    public bool HasBadge => IsDone || IsSkipped || IsFailed;
    public bool HasThumbnail => Thumbnail is not null;

    public string StateText => loc[State switch
    {
        CropItemState.Current => "crop.state_current",
        CropItemState.Done => "crop.state_done",
        CropItemState.Skipped => "crop.state_skipped",
        CropItemState.Failed => "crop.state_failed",
        _ => "crop.state_waiting",
    }];

    /// <summary>"photo.jpg: Cropped", for the tooltip and screen readers.</summary>
    public string AccessibleName => $"{Name}: {StateText}";

    /// <summary>Keeps a small copy of the preview (made once, from the already downscaled preview).</summary>
    internal void SetThumbnail(Bitmap preview)
    {
        if (Thumbnail is not null)
            return;
        try
        {
            var size = preview.PixelSize;
            var scale = Math.Min(1.0, (double)ThumbnailSide / Math.Max(size.Width, size.Height));
            Thumbnail = preview.CreateScaledBitmap(
                new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale))),
                BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Thumbnail = null; // the strip shows the file's number instead
        }
    }

    internal void RefreshTexts()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(AccessibleName));
    }

    public void Dispose()
    {
        var thumbnail = Thumbnail;
        Thumbnail = null;
        thumbnail?.Dispose();
    }
}
