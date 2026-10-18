using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

/// <summary>Modal preset editor. Returns true from ShowDialog when the user saved.</summary>
public partial class PresetEditorWindow : Window
{
    public PresetEditorWindow()
    {
        InitializeComponent();
        Motion.Track(this);
    }

    /// <summary>True after the user saved (also available when the window was not shown as a dialog).</summary>
    public bool Saved { get; private set; }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PresetEditorViewModel vm || !vm.Apply())
            return;
        Saved = true;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
