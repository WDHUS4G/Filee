using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Filee.App.Services;
using Filee.Core.Localization;

namespace Filee.App.Views;

/// <summary>One row of the window: a <see cref="SponsorLink"/> with its note in the current language.</summary>
public sealed record SponsorWay(string Name, string Note, string Url, Geometry? Icon, IBrush Color);

/// <summary>Modal list of the ways to sponsor Filee. Choosing one opens it in the browser and closes the window.</summary>
public partial class SponsorWindow : Window
{
    public SponsorWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
    }

    /// <summary>The window with the ways for <paramref name="loc"/>'s language, the one that suits it first.</summary>
    public static SponsorWindow Create(ILocalizer loc)
    {
        var window = new SponsorWindow();
        window.Ways.ItemsSource = SponsorLinks.For(loc.Language)
            .Select(l => new SponsorWay(l.Name, loc[$"sponsor.note.{l.Id}"], l.Url,
                window.TryFindResource(l.Icon, out var icon) ? icon as Geometry : null,
                new SolidColorBrush(Color.Parse(l.Color))))
            .ToList();
        return window;
    }

    private async void OnChoose(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string url)
        {
            await Launcher.LaunchUriAsync(new Uri(url));
            Close();
        }
    }
}
