using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Filee.App.Views.Pages;

public partial class ThemePage : UserControl
{
    public ThemePage() => InitializeComponent();

    /// <summary>Plays the donut's open animation so the "reduce animations" setting can be felt right away.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PreviewDonut.PlayOpenAnimation();
    }
}
