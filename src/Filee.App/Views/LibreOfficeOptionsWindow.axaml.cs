using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>Modal LibreOffice switches of the Engines page (its view model is the DataContext).</summary>
public partial class LibreOfficeOptionsWindow : Window
{
    public LibreOfficeOptionsWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
