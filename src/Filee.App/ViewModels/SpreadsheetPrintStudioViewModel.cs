// State for the Excel → PDF print studio: a non-destructive plan per sheet and previews rendered by the same
// LibreOffice path as the final PDF.

using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Filee.Engines.Office.Sheets;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed record SpreadsheetChoice<T>(T Value, string Label);

public sealed partial class SpreadsheetPreviewPage(int number) : ObservableObject, IDisposable
{
    public int Number { get; } = number;
    [ObservableProperty] private Bitmap? _thumbnail;
    public void Dispose() => Thumbnail?.Dispose();
}

public sealed partial class SpreadsheetSheetPlan : ObservableObject
{
    private readonly Action _changed;
    private readonly ILocalizer _loc;
    private bool _suppressChanges;
    private bool _writePrintArea;
    public SpreadsheetSheetPlan(SpreadsheetSheetInfo sheet, ILocalizer loc, Action changed,
        SpreadsheetPrintRegion? initial = null, string? name = null, bool isAddedRegion = false)
    {
        Sheet = sheet;
        _loc = loc;
        _changed = changed;
        Name = name ?? sheet.Name;
        var source = initial ?? SpreadsheetPrintRegion.FromSource(sheet);
        _range = source.Range;
        _paper = source.Paper;
        _orientation = source.Orientation;
        _scaleMode = source.ScaleMode;
        _scalePercent = source.ScalePercent;
        _fitWidth = source.FitWidth;
        _fitHeight = source.FitHeight;
        _order = source.Order;
        _marginLeftMm = source.MarginLeftMm;
        _marginRightMm = source.MarginRightMm;
        _marginTopMm = source.MarginTopMm;
        _marginBottomMm = source.MarginBottomMm;
        _keepManualBreaks = source.KeepManualBreaks;
        _writePrintArea = isAddedRegion || source.WritePrintArea;
        IsAddedRegion = isAddedRegion;
    }

    public SpreadsheetSheetInfo Sheet { get; }
    public string Name { get; }
    public bool IsAddedRegion { get; }
    public string SourceSummary => $"{Sheet.UsedRange} · {Sheet.UsedRows} × {Sheet.UsedColumns}";
    public bool HasWarning => ScaleMode == SpreadsheetScaleMode.Source
        && (Sheet.HasSuspiciousScale || Sheet.HasWideUnboundedRange);
    public string Warning => !HasWarning ? "" : Sheet.HasSuspiciousScale
        ? _loc.Format("sheet_studio.warning_scale", Sheet.PageSetup.ScalePercent ?? 0)
        : Sheet.HasWideUnboundedRange ? _loc["sheet_studio.warning_wide"] : "";

    [ObservableProperty] private bool _included = true;
    [ObservableProperty] private string _range;
    [ObservableProperty] private SpreadsheetPaper _paper;
    [ObservableProperty] private SpreadsheetPageOrientation _orientation;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning), nameof(Warning))]
    private SpreadsheetScaleMode _scaleMode;
    [ObservableProperty] private int _scalePercent;
    [ObservableProperty] private int _fitWidth;
    [ObservableProperty] private int _fitHeight;
    [ObservableProperty] private SpreadsheetPageOrder _order;
    [ObservableProperty] private double _marginLeftMm;
    [ObservableProperty] private double _marginRightMm;
    [ObservableProperty] private double _marginTopMm;
    [ObservableProperty] private double _marginBottomMm;
    [ObservableProperty] private bool _includeHidden;
    [ObservableProperty] private bool _keepManualBreaks;

    partial void OnIncludedChanged(bool value) => _changed();
    partial void OnRangeChanged(string value)
    {
        if (!_suppressChanges)
            _writePrintArea = true;
        _changed();
    }
    partial void OnPaperChanged(SpreadsheetPaper value) => _changed();
    partial void OnOrientationChanged(SpreadsheetPageOrientation value) => _changed();
    partial void OnScaleModeChanged(SpreadsheetScaleMode value) => _changed();
    partial void OnScalePercentChanged(int value) => _changed();
    partial void OnFitWidthChanged(int value) => _changed();
    partial void OnFitHeightChanged(int value) => _changed();
    partial void OnOrderChanged(SpreadsheetPageOrder value) => _changed();
    partial void OnMarginLeftMmChanged(double value) => _changed();
    partial void OnMarginRightMmChanged(double value) => _changed();
    partial void OnMarginTopMmChanged(double value) => _changed();
    partial void OnMarginBottomMmChanged(double value) => _changed();
    partial void OnIncludeHiddenChanged(bool value) => _changed();
    partial void OnKeepManualBreaksChanged(bool value) => _changed();

    public SpreadsheetPrintRegion ToRegion() => new(Sheet, Range.Trim(), Paper, Orientation, ScaleMode,
        ScalePercent, FitWidth, FitHeight, Order, MarginLeftMm, MarginRightMm, MarginTopMm, MarginBottomMm,
        IncludeHidden, KeepManualBreaks, _writePrintArea);

    public void SetRangeExplicitly(string range)
    {
        _writePrintArea = true;
        if (Range == range)
            _changed();
        else
            Range = range;
    }

    public void Reset()
    {
        var source = SpreadsheetPrintRegion.FromSource(Sheet);
        _suppressChanges = true;
        try
        {
            Range = source.Range;
            Paper = source.Paper;
            Orientation = source.Orientation;
            ScaleMode = source.ScaleMode;
            ScalePercent = source.ScalePercent;
            FitWidth = source.FitWidth;
            FitHeight = source.FitHeight;
            Order = source.Order;
            MarginLeftMm = source.MarginLeftMm;
            MarginRightMm = source.MarginRightMm;
            MarginTopMm = source.MarginTopMm;
            MarginBottomMm = source.MarginBottomMm;
            IncludeHidden = source.IncludeHidden;
            KeepManualBreaks = source.KeepManualBreaks;
            _writePrintArea = source.WritePrintArea;
        }
        finally { _suppressChanges = false; }
        _changed();
    }
}

public sealed partial class SpreadsheetPrintStudioViewModel : ObservableObject, IDisposable
{
    private readonly SpreadsheetPrintService _service;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly ILogger? _log;
    private readonly string _file;
    private SpreadsheetWorkbookInfo? _workbook;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _pageCancellation;
    private CancellationTokenSource? _exportCancellation;
    private SpreadsheetRenderedRegion? _rendered;
    private bool _closed;

    public SpreadsheetPrintStudioViewModel(string file, SpreadsheetPrintService service, ILocalizer loc, UserDataStore store,
        ILogger<SpreadsheetPrintStudioViewModel>? log = null)
    {
        _service = service;
        _loc = loc;
        _store = store;
        _log = log;
        _file = file;
        Papers = SpreadsheetPaper.All.Select(paper => new SpreadsheetChoice<SpreadsheetPaper>(paper,
            paper == SpreadsheetPaper.Source ? loc["sheet_studio.from_workbook"] : paper.Name)).ToArray();
        Orientations =
        [
            new(SpreadsheetPageOrientation.Source, loc["sheet_studio.from_workbook"]),
            new(SpreadsheetPageOrientation.Portrait, loc["sheet_studio.portrait"]),
            new(SpreadsheetPageOrientation.Landscape, loc["sheet_studio.landscape"]),
        ];
        ScaleModes =
        [
            new(SpreadsheetScaleMode.Source, loc["sheet_studio.from_workbook"]),
            new(SpreadsheetScaleMode.FitToPages, loc["sheet_studio.fit_pages"]),
            new(SpreadsheetScaleMode.Percent, loc["sheet_studio.exact_scale"]),
        ];
        PageOrders =
        [
            new(SpreadsheetPageOrder.Source, loc["sheet_studio.from_workbook"]),
            new(SpreadsheetPageOrder.DownThenOver, loc["sheet_studio.down_over"]),
            new(SpreadsheetPageOrder.OverThenDown, loc["sheet_studio.over_down"]),
        ];
        FileName = Path.GetFileName(file);
        IsBusy = true;
        Status = loc["sheet_studio.rendering"];
        Initialization = InitializeAsync();
    }

    public string FileName { get; }
    internal Task Initialization { get; }
    public ObservableCollection<SpreadsheetSheetPlan> Sheets { get; } = [];
    public ObservableCollection<SpreadsheetPreviewPage> Pages { get; } = [];
    public IReadOnlyList<SpreadsheetChoice<SpreadsheetPaper>> Papers { get; private set; } = [];
    public IReadOnlyList<SpreadsheetChoice<SpreadsheetPageOrientation>> Orientations { get; private set; } = [];
    public IReadOnlyList<SpreadsheetChoice<SpreadsheetScaleMode>> ScaleModes { get; private set; } = [];
    public IReadOnlyList<SpreadsheetChoice<SpreadsheetPageOrder>> PageOrders { get; private set; } = [];

    [ObservableProperty] private SpreadsheetSheetPlan? _selectedSheet;
    [ObservableProperty] private SpreadsheetPreviewPage? _selectedPage;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _exportProgress;

    public bool CanExport => _workbook is not null && !IsBusy && !IsExporting && Sheets.Any(sheet => sheet.Included);
    public bool CanRemoveSelectedRegion => SelectedSheet is { } selected
        && selected.IsAddedRegion;

    partial void OnSelectedSheetChanged(SpreadsheetSheetPlan? value)
    {
        OnPropertyChanged(nameof(CanRemoveSelectedRegion));
        SchedulePreview();
    }
    partial void OnSelectedPageChanged(SpreadsheetPreviewPage? value) => ScheduleLargePreview(value);
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanExport));
    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    [RelayCommand]
    private void UseContentRange()
    {
        if (SelectedSheet is { } selected)
            selected.SetRangeExplicitly(selected.Sheet.UsedRange);
    }

    [RelayCommand]
    private void ResetSheet()
    {
        if (SelectedSheet is not { } selected)
            return;
        selected.Reset();
    }

    [RelayCommand]
    private void ApplySuggestedFix()
    {
        if (SelectedSheet is not { } selected)
            return;
        selected.SetRangeExplicitly(selected.Sheet.UsedRange);
        selected.ScaleMode = SpreadsheetScaleMode.FitToPages;
        selected.FitWidth = selected.Sheet.UsedColumns > 60 ? 2 : 1;
        selected.FitHeight = 0;
        if (selected.Sheet.UsedColumns > 20)
            selected.Orientation = SpreadsheetPageOrientation.Landscape;
        if (selected.Paper == SpreadsheetPaper.Source)
            selected.Paper = selected.Sheet.PageSetup.Paper == SpreadsheetPaper.Source
                ? SpreadsheetPaper.A4 : selected.Sheet.PageSetup.Paper;
        selected.KeepManualBreaks = false;
    }

    [RelayCommand]
    private void AddRegion()
    {
        if (SelectedSheet is not { } selected)
            return;
        var number = Sheets.Count(item => item.Sheet.Index == selected.Sheet.Index) + 1;
        var duplicate = new SpreadsheetSheetPlan(selected.Sheet, _loc, SchedulePreview, selected.ToRegion(),
            _loc.Format("sheet_studio.region_name", selected.Sheet.Name, number), isAddedRegion: true);
        var index = Sheets.IndexOf(selected) + 1;
        Sheets.Insert(index, duplicate);
        SelectedSheet = duplicate;
        OnPropertyChanged(nameof(CanRemoveSelectedRegion));
    }

    [RelayCommand]
    private void RemoveRegion()
    {
        if (SelectedSheet is not { IsAddedRegion: true } selected)
            return;
        var index = Sheets.IndexOf(selected);
        Sheets.Remove(selected);
        SelectedSheet = Sheets[Math.Min(index, Sheets.Count - 1)];
        OnPropertyChanged(nameof(CanRemoveSelectedRegion));
    }

    [RelayCommand]
    private void MoveRegionUp() => MoveRegion(-1);

    [RelayCommand]
    private void MoveRegionDown() => MoveRegion(1);

    private void MoveRegion(int direction)
    {
        if (SelectedSheet is not { } selected)
            return;
        var index = Sheets.IndexOf(selected);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Sheets.Count)
            return;
        Sheets.Move(index, target);
        SelectedSheet = selected;
    }

    public async Task ExportAsync(string outputPath)
    {
        if (IsExporting || _workbook is null)
            return;
        var regions = Sheets.Where(sheet => sheet.Included).Select(sheet => sheet.ToRegion()).ToArray();
        if (regions.Any(region => !SpreadsheetPrintInspector.IsValidRange(region.Range)))
        {
            Status = _loc["sheet_studio.invalid_range"];
            return;
        }
        IsExporting = true;
        _previewCancellation?.Cancel();
        _pageCancellation?.Cancel();
        _exportCancellation = new CancellationTokenSource();
        ExportProgress = 0;
        Status = _loc["sheet_studio.exporting"];
        try
        {
            var progress = new Progress<double>(value => ExportProgress = value * 100);
            await _service.ExportAsync(_workbook, regions, outputPath, progress, _exportCancellation.Token);
            Status = _loc.Format("sheet_studio.exported", Path.GetFileName(outputPath));
            try
            {
                _store.AddHistory(new HistoryEntry
                {
                    JobId = Guid.NewGuid().ToString("N")[..12],
                    FinishedAt = DateTime.Now,
                    PresetId = "tool-sheet-studio",
                    PresetName = _loc["preset.sheet_studio"],
                    State = JobState.Completed,
                    Sources = [_workbook.Path],
                    Outputs = [outputPath],
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The PDF is already safely in place; a history write must not turn that success into an error.
                _log?.LogWarning(ex, "Could not record spreadsheet studio export in history");
            }
        }
        catch (OperationCanceledException)
        {
            Status = _loc["common.cancel"];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not export spreadsheet print plan for {File}", _workbook.Path);
            Status = _loc["error.conversion_failed"];
        }
        finally
        {
            IsExporting = false;
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            if (!_closed)
                SchedulePreview();
        }
    }

    public void CancelExport() => _exportCancellation?.Cancel();

    private void SchedulePreview()
    {
        OnPropertyChanged(nameof(CanExport));
        if (_closed || IsExporting || _workbook is null || SelectedSheet is null)
            return;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _pageCancellation?.Cancel();
        _pageCancellation?.Dispose();
        _pageCancellation = null;
        IsBusy = false;
        _previewCancellation = new CancellationTokenSource();
        _ = PreviewAfterDelayAsync(SelectedSheet, _previewCancellation.Token);
    }

    private async Task PreviewAfterDelayAsync(SpreadsheetSheetPlan sheet, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            if (!SpreadsheetPrintInspector.IsValidRange(sheet.Range))
            {
                Status = _loc["sheet_studio.invalid_range"];
                return;
            }
            IsBusy = true;
            Status = _loc["sheet_studio.rendering"];
            var rendered = await _service.RenderAsync(_workbook!, sheet.ToRegion(), cancellationToken);
            if (_closed || cancellationToken.IsCancellationRequested)
            {
                SpreadsheetPrintService.Release(rendered);
                return;
            }
            ClearPages();
            SpreadsheetPrintService.Release(_rendered);
            _rendered = rendered;
            for (var pageNumber = 1; pageNumber <= rendered.Preview.PageCount; pageNumber++)
                Pages.Add(new SpreadsheetPreviewPage(pageNumber));
            SelectedPage = Pages.FirstOrDefault();
            Status = _loc.Format("sheet_studio.page_count", Pages.Count);
            await LoadThumbnailsAsync(rendered, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not render spreadsheet preview for {File}", _file);
            Status = _loc["error.conversion_failed"];
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                IsBusy = false;
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var workbook = await Task.Run(() => _service.Inspect(_file));
            if (_closed)
                return;
            _workbook = workbook;
            foreach (var sheet in workbook.Sheets.Where(sheet => !sheet.IsHidden))
                Sheets.Add(new SpreadsheetSheetPlan(sheet, _loc, SchedulePreview));
            SelectedSheet = Sheets.FirstOrDefault();
            if (SelectedSheet is null)
                Status = _loc["error.unsupported_source"];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not inspect spreadsheet {File}", _file);
            Status = _loc["error.conversion_failed"];
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanExport));
        }
    }

    private async Task LoadThumbnailsAsync(SpreadsheetRenderedRegion rendered, CancellationToken cancellationToken)
    {
        var bitmaps = await Task.Run(() => rendered.Preview.RenderThumbnails(220, cancellationToken).Select(page =>
        {
            using var stream = new MemoryStream(page.Png, writable: false);
            return (page.PageNumber, Image: new Bitmap(stream));
        }).ToArray(), cancellationToken);
        foreach (var (pageNumber, bitmap) in bitmaps)
        {
            if (_closed || cancellationToken.IsCancellationRequested || _rendered != rendered)
            {
                bitmap.Dispose();
                continue;
            }
            Pages.FirstOrDefault(page => page.Number == pageNumber)!.Thumbnail = bitmap;
        }
    }

    private void ScheduleLargePreview(SpreadsheetPreviewPage? page)
    {
        _pageCancellation?.Cancel();
        _pageCancellation?.Dispose();
        _pageCancellation = null;
        if (_closed || page is null || _rendered is not { } rendered)
            return;
        _pageCancellation = new CancellationTokenSource();
        _ = LoadLargePreviewAsync(rendered, page.Number, _pageCancellation.Token);
    }

    private async Task LoadLargePreviewAsync(SpreadsheetRenderedRegion rendered, int pageNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            var bitmap = await RenderBitmapAsync(rendered.Preview, pageNumber, 1400, cancellationToken);
            if (_closed || cancellationToken.IsCancellationRequested || _rendered != rendered
                || SelectedPage?.Number != pageNumber)
            {
                bitmap.Dispose();
                return;
            }
            var old = PreviewImage;
            PreviewImage = bitmap;
            old?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not render spreadsheet preview page {Page}", pageNumber);
        }
    }

    private static Task<Bitmap> RenderBitmapAsync(Filee.Engines.Pdf.PdfPreviewDocument document, int pageNumber,
        int maxLongEdge, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var rendered = document.RenderPage(pageNumber, maxLongEdge, cancellationToken);
        using var stream = new MemoryStream(rendered.Png, writable: false);
        return new Bitmap(stream);
    }, cancellationToken);

    private void ClearPages()
    {
        foreach (var page in Pages) page.Dispose();
        Pages.Clear();
        var preview = PreviewImage;
        PreviewImage = null;
        preview?.Dispose();
    }

    public void Dispose()
    {
        _closed = true;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _pageCancellation?.Cancel();
        _pageCancellation?.Dispose();
        _exportCancellation?.Cancel();
        ClearPages();
        SpreadsheetPrintService.Release(_rendered);
    }
}
