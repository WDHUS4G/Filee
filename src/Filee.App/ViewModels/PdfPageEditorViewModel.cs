// Lightweight PDF page organiser opened from the PDF donut's Edit group. Pages stay as PDF objects: saving copies
// them in the chosen order and only changes /Rotate where requested.

using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Pdf;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class PdfPageItem(PdfPreviewSource previewSource, int pageIndex, ILocalizer loc) : ObservableObject, IDisposable
{
    public PdfPreviewSource PreviewSource { get; } = previewSource;
    public string Path => PreviewSource.Path;
    public int PageIndex { get; } = pageIndex;
    public int PageNumber => PageIndex + 1;
    public string PageText => loc.Format("pdf_editor.page", PageIndex + 1);
    public string SourceName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RotationText))]
    [NotifyPropertyChangedFor(nameof(PreviewRotation))]
    private int _rotation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isThumbnailLoading;

    public string RotationText
    {
        get
        {
            var signed = Rotation > 180 ? Rotation - 360 : Rotation;
            return signed == 0 ? "" : loc.Format("pdf_editor.rotation", signed);
        }
    }

    public double PreviewRotation => Rotation > 180 ? Rotation - 360 : Rotation;
    public bool HasThumbnail => Thumbnail is not null;
    public PdfPageReference Reference => new(Path, PageIndex, Rotation);

    internal void SetThumbnail(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var previous = Thumbnail;
        Thumbnail = new Bitmap(stream);
        previous?.Dispose();
    }

    public void Dispose()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}

public sealed partial class PdfPageEditorViewModel : ObservableObject, IDisposable
{
    private readonly Preset _preset;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly ILogger? _log;
    private readonly string _primarySource;
    private readonly IReadOnlyList<string> _initialFiles;
    private readonly Dictionary<string, PdfPreviewSource> _previewSources = new(FileSystemPaths.Comparer);
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private Task? _initialLoad;
    private int _previewRequest;
    private bool _closed;

    public PdfPageEditorViewModel(IReadOnlyList<string> files, Preset preset, ILocalizer loc, UserDataStore store,
        ILogger? log = null)
    {
        _preset = preset;
        _loc = loc;
        _store = store;
        _log = log;
        _primarySource = files.First();
        _initialFiles = files;
    }

    public ObservableCollection<PdfPageItem> Pages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand))]
    [NotifyCanExecuteChangedFor(nameof(RotateRightCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedPreviewRotation))]
    private PdfPageItem? _selectedPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPreview))]
    private Bitmap? _selectedPreview;

    [ObservableProperty]
    private bool _isPreviewLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand))]
    [NotifyCanExecuteChangedFor(nameof(RotateRightCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool CanSave => !IsBusy && Pages.Count > 0;
    public bool HasSelectedPreview => SelectedPreview is not null;
    public double SelectedPreviewRotation => SelectedPage?.PreviewRotation ?? 0;

    partial void OnSelectedPageChanged(PdfPageItem? value) => _ = LoadSelectedPreviewAsync(value);

    /// <summary>Opens the PDFs and renders the page strip after the window is visible.</summary>
    public async Task LoadThumbnailsAsync()
    {
        await EnsureInitialFilesAsync();
        await LoadThumbnailsAsync(Pages.ToList());
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<PdfPageItem> items)
    {
        await _thumbnailGate.WaitAsync();
        try
        {
            foreach (var item in items.Where(item => item.Thumbnail is null))
            {
                if (_closed || !Pages.Contains(item))
                    return;
                item.IsThumbnailLoading = true;
                try
                {
                    var preview = await Task.Run(() => item.PreviewSource.Preview(item.PageIndex, 260));
                    if (!_closed && Pages.Contains(item))
                        item.SetThumbnail(preview.Png);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log?.LogDebug(ex, "Could not render thumbnail for page {Page} of {Path}", item.PageNumber, item.Path);
                }
                finally
                {
                    item.IsThumbnailLoading = false;
                }
            }
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private async Task LoadSelectedPreviewAsync(PdfPageItem? item)
    {
        var request = Interlocked.Increment(ref _previewRequest);
        IsPreviewLoading = item is not null;
        SelectedPreview?.Dispose();
        SelectedPreview = null;
        if (item is null)
            return;
        try
        {
            var preview = await Task.Run(() => item.PreviewSource.Preview(item.PageIndex, 1400));
            using var stream = new MemoryStream(preview.Png);
            var bitmap = new Bitmap(stream);
            if (_closed || request != _previewRequest || !ReferenceEquals(item, SelectedPage))
            {
                bitmap.Dispose();
                return;
            }
            SelectedPreview = bitmap;
            if (item.Thumbnail is null)
                item.SetThumbnail(preview.Png);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "Could not render preview for page {Page} of {Path}", item.PageNumber, item.Path);
        }
        finally
        {
            if (request == _previewRequest)
                IsPreviewLoading = false;
        }
    }

    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        await EnsureInitialFilesAsync();
        if (paths.Count == 0 || IsBusy || _closed)
            return;
        var added = await AddFilesCoreAsync(paths);
        _ = LoadThumbnailsAsync(added);
    }

    private Task EnsureInitialFilesAsync() => _initialLoad ??= AddFilesCoreAsync(_initialFiles);

    private async Task<IReadOnlyList<PdfPageItem>> AddFilesCoreAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || _closed)
            return [];
        IsBusy = true;
        Status = _loc["pdf_editor.loading"];
        var added = new List<PdfPageItem>();
        try
        {
            var missing = paths.Distinct(FileSystemPaths.Comparer)
                .Where(path => !_previewSources.ContainsKey(path)).ToList();
            var opened = await Task.Run(() => missing.Select(path =>
            {
                try { return (Path: path, Source: PdfPreviewSource.Open(path), Error: (Exception?)null); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return (Path: path, Source: (PdfPreviewSource?)null, Error: ex); }
            }).ToList());
            var failed = false;
            foreach (var (path, source, error) in opened)
            {
                if (error is not null)
                {
                    failed = true;
                    _log?.LogError(error, "Could not add {Path} to the PDF editor", path);
                    continue;
                }
                _previewSources[path] = source!;
            }
            foreach (var path in paths)
            {
                if (!_previewSources.TryGetValue(path, out var source))
                    continue;
                for (var page = 0; page < source.PageCount; page++)
                {
                    var item = new PdfPageItem(source, page, _loc);
                    Pages.Add(item);
                    added.Add(item);
                }
            }
            SelectedPage ??= Pages.FirstOrDefault();
            Status = failed ? _loc["error.conversion_failed"] : "";
            NotifyCollectionChanged();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not add pages to the PDF editor");
            Status = _loc["error.conversion_failed"];
        }
        finally
        {
            IsBusy = false;
        }
        return added;
    }

    private bool CanMoveUp() => !IsBusy && SelectedPage is not null && Pages.IndexOf(SelectedPage) > 0;
    private bool CanMoveDown() => !IsBusy && SelectedPage is not null && Pages.IndexOf(SelectedPage) is >= 0 and var index && index < Pages.Count - 1;
    private bool CanChangePage() => !IsBusy && SelectedPage is not null;
    private bool CanDelete() => CanChangePage() && Pages.Count > 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private void Move(int direction)
    {
        if (SelectedPage is not { } selected)
            return;
        var index = Pages.IndexOf(selected);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Pages.Count)
            return;
        Pages.Move(index, target);
        SelectedPage = selected;
        NotifyCollectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedPage is not { } selected || Pages.Count <= 1)
            return;
        var index = Pages.IndexOf(selected);
        Pages.RemoveAt(index);
        selected.Dispose();
        SelectedPage = Pages[Math.Min(index, Pages.Count - 1)];
        NotifyCollectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanChangePage))]
    private void RotateLeft() => Rotate(-90);

    [RelayCommand(CanExecute = nameof(CanChangePage))]
    private void RotateRight() => Rotate(90);

    private void Rotate(int degrees)
    {
        if (SelectedPage is not { } selected)
            return;
        selected.Rotation = ((selected.Rotation + degrees) % 360 + 360) % 360;
        OnPropertyChanged(nameof(SelectedPreviewRotation));
    }

    public Task<bool> SaveAsync() => WriteAsync(Pages, _preset, closeWhenDone: true);

    public Task<bool> ExtractSelectedAsync()
    {
        if (SelectedPage is not { } selected)
            return Task.FromResult(false);
        var extract = _preset.Clone();
        extract.Id = "pdf-extract-page";
        extract.NameKey = "preset.pdf_extract";
        extract.Output.FileNamePattern = "{name}_extracted";
        return WriteAsync([selected], extract, closeWhenDone: false);
    }

    private async Task<bool> WriteAsync(IEnumerable<PdfPageItem> items, Preset preset, bool closeWhenDone)
    {
        if (IsBusy)
            return false;
        var pages = items.Select(item => item.Reference).ToList();
        if (pages.Count == 0)
            return false;
        var applied = _store.Settings.WithDefaultOutput(preset);
        var tokens = new OutputPathResolver.Tokens(_primarySource, _loc.DisplayName(applied), 1, DateTime.Now);
        var output = OutputPathResolver.Resolve(applied.Output, tokens, "pdf", File.Exists);
        if (output is null)
        {
            Status = _loc["toast.skipped"];
            return false;
        }

        IsBusy = true;
        Status = _loc["pdf_editor.saving"];
        try
        {
            var folder = System.IO.Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            await Task.Run(() => PdfPageTools.Save(pages, output));
            PreserveDates(output, applied);
            _store.AddHistory(new HistoryEntry
            {
                JobId = Guid.NewGuid().ToString("N")[..10],
                FinishedAt = DateTime.Now,
                PresetId = preset.Id,
                PresetName = _loc.DisplayName(preset),
                State = JobState.Completed,
                Sources = pages.Select(p => p.Path).Distinct(FileSystemPaths.Comparer).ToList(),
                Outputs = [output],
            });
            Status = _loc.Format("pdf_editor.saved", System.IO.Path.GetFileName(output));
            return closeWhenDone;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not save edited PDF");
            Status = _loc["error.conversion_failed"];
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PreserveDates(string output, Preset preset)
    {
        if (!preset.Output.KeepDates && !_store.Settings.KeepFileDates)
            return;
        try
        {
            File.SetCreationTimeUtc(output, File.GetCreationTimeUtc(_primarySource));
            File.SetLastWriteTimeUtc(output, File.GetLastWriteTimeUtc(_primarySource));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogDebug(ex, "Could not preserve file timestamps for {Path}", output);
        }
    }

    private void NotifyCollectionChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        Interlocked.Increment(ref _previewRequest);
        SelectedPreview?.Dispose();
        SelectedPreview = null;
        foreach (var page in Pages)
            page.Dispose();
    }
}
