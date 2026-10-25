using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class SpreadsheetPrintStudioWindow : Window
{
    public SpreadsheetPrintStudioWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpreadsheetPrintStudioViewModel vm)
            return;
        var name = Path.GetFileNameWithoutExtension(vm.FileName) + "_prepared.pdf";
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = this.TryFindResource("sheet_studio.export_pdf", out var title)
                ? title?.ToString() : "PDF",
            SuggestedFileName = name,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (target?.TryGetLocalPath() is { } path)
            await vm.ExportAsync(path);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        (DataContext as SpreadsheetPrintStudioViewModel)?.CancelExport();
        Close();
    }
}
