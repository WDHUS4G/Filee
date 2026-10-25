using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class PdfPageEditorWindow : Window
{
    private static readonly FilePickerFileType PdfFiles = new("PDF")
    {
        Patterns = ["*.pdf"],
        MimeTypes = ["application/pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
    };

    public PdfPageEditorWindow()
    {
        InitializeComponent();
        Motion.Track(this);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is PdfPageEditorViewModel vm)
            await vm.LoadThumbnailsAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is PdfPageEditorViewModel vm)
            vm.Dispose();
        base.OnClosed(e);
    }

    public async Task PickAndAddAsync()
    {
        if (DataContext is not PdfPageEditorViewModel vm)
            return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = this.FindResource("pdf_editor.add")?.ToString(),
            AllowMultiple = true,
            FileTypeFilter = [PdfFiles],
        });
        var paths = picked.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
        await vm.AddFilesAsync(paths);
    }

    private async void OnAdd(object? sender, RoutedEventArgs e) => await PickAndAddAsync();

    private async void OnExtract(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PdfPageEditorViewModel vm)
            await vm.ExtractSelectedAsync();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PdfPageEditorViewModel vm && await vm.SaveAsync())
            Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
