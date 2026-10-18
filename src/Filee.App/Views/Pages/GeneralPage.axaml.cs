using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;

namespace Filee.App.Views.Pages;

public partial class GeneralPage : UserControl
{
    private static readonly FilePickerFileType JsonType = new("Filee presets") { Patterns = ["*.json"] };

    public GeneralPage() => InitializeComponent();

    private GeneralPageViewModel? Vm => DataContext as GeneralPageViewModel;

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "filee-presets.json",
            DefaultExtension = "json",
            FileTypeChoices = [JsonType],
        });
        if (file?.TryGetLocalPath() is { } path)
            Vm.Export(path);
    }

    private async void OnBrowseDefaultFolder(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            Vm.DefaultFolder = path;
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { FileTypeFilter = [JsonType] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            Vm.Import(path);
    }

    private async void OnReset(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var loc = AppHost.Get<ILocalizer>();
        if (await AppHost.Get<WindowService>().ConfirmAsync(owner, loc["general.reset_confirm"]))
            Vm.ResetLibrary();
    }
}
