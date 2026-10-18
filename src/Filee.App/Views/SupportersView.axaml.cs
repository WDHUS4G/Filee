using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>A sponsor's logo (or its name when the logo is missing) and where it leads.</summary>
/// <param name="LightOnly">A logo without a dark version, shown in the dark theme: it keeps a light tile.</param>
public sealed record SupporterItem(string Name, string? Url, Bitmap? Logo, bool LightOnly = false);

/// <summary>The sponsors' logos and the backers' names shipped with this release; hidden while there are none.</summary>
public partial class SupportersView : UserControl
{
    public static readonly StyledProperty<double> LogoHeightProperty =
        AvaloniaProperty.Register<SupportersView, double>(nameof(LogoHeight), 28);

    public static readonly StyledProperty<bool> ShowTitleProperty =
        AvaloniaProperty.Register<SupportersView, bool>(nameof(ShowTitle), true);

    /// <summary>Height of the logos: small in the Sponsor window, larger on the About page.</summary>
    public double LogoHeight
    {
        get => GetValue(LogoHeightProperty);
        set => SetValue(LogoHeightProperty, value);
    }

    /// <summary>Whether the view shows its own "Thank you" line (the About page card has a heading instead).</summary>
    public bool ShowTitle
    {
        get => GetValue(ShowTitleProperty);
        set => SetValue(ShowTitleProperty, value);
    }

    public SupportersView()
    {
        InitializeComponent();
        Show(Supporters.Bundled, BundledLogo);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowTitleProperty)
            Title.IsVisible = ShowTitle;
    }

    /// <summary>Shows <paramref name="list"/>, the logos read by <paramref name="logo"/> (null when missing).</summary>
    public void Show(SupporterList list, Func<string, Bitmap?> logo)
    {
        IsVisible = !list.IsEmpty;
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        Logos.ItemsSource = list.Sponsors
            .Select(s => (s, Logo: (dark ? s.LogoDark ?? s.Logo : s.Logo) is { } file ? logo(file) : null))
            .Select(x => new SupporterItem(x.s.Name, x.s.Url, x.Logo, dark && x.Logo is not null && x.s.LogoDark is null))
            .ToList();
        Logos.IsVisible = list.Sponsors.Count > 0;
        Names.Text = string.Join(" · ", list.Backers.Select(b => b.Name));
        Names.IsVisible = list.Backers.Count > 0;
    }

    private static Bitmap? BundledLogo(string file)
    {
        var uri = new Uri(Supporters.Folder + file);
        if (!AssetLoader.Exists(uri))
            return null;
        using var stream = AssetLoader.Open(uri);
        return new Bitmap(stream);
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string url && TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(new Uri(url));
    }
}
