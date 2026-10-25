using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Localization;

namespace Filee.App.Views;

/// <summary>The slides of one version (<see cref="SlideDeck"/>) with their buttons and keys.</summary>
public partial class WhatsNewWindow : Window
{
    private readonly ILocalizer? _loc;
    private readonly SlideDeck? _deck;

    public WhatsNewWindow() => InitializeComponent();

    public WhatsNewWindow(SlideDeck deck, ILocalizer loc) : this()
    {
        _deck = deck;
        _loc = loc;
        Motion.Track(this);
        UiZoom.Track(this);
        Slides.PlayAnimations = Motion.Enabled;
        Slides.IndexChanged += (_, _) => UpdateButtons();
        Slides.Show(deck.Slides, key => loc[key], () => loc.Language);
        ShowTexts();
        Motion.Changed += OnMotionChanged;
        loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            Motion.Changed -= OnMotionChanged;
            loc.LanguageChanged -= OnLanguageChanged;
        };
        Header.PointerPressed += OnHeaderPressed;
        Opened += (_, _) => PrimaryButton.Focus();
    }

    /// <summary>The slideshow (tests).</summary>
    internal SlideShow SlideShow => Slides;

    private void ShowTexts()
    {
        if (_loc is null || _deck is null)
            return;
        Title = HeaderText.Text = _loc.Format("whats_new.title", _deck.DisplayVersion);
        Slides.RefreshTexts();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (_loc is null)
            return;
        // Hidden rather than collapsed, so "Next" stays where it is.
        PreviousButton.Opacity = Slides.IsFirst ? 0 : 1;
        PreviousButton.IsEnabled = !Slides.IsFirst;
        PrimaryButton.Content = Slides.IsLast ? _loc["whats_new.done"] : _loc["whats_new.next"];
    }

    private void OnMotionChanged(object? sender, EventArgs e) => Slides.PlayAnimations = Motion.Enabled;

    private void OnLanguageChanged(object? sender, EventArgs e) => ShowTexts();

    private void OnPrevious(object? sender, RoutedEventArgs e) => Slides.Previous();

    /// <summary>Next slide; on the last one it closes.</summary>
    private void OnPrimary(object? sender, RoutedEventArgs e)
    {
        if (!Slides.Next())
            Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;
        // The arrow pointing the way the text reads goes on: Left in Arabic, where Next sits on the left.
        var rightToLeft = FlowDirection == FlowDirection.RightToLeft;
        switch (e.Key)
        {
            case Key.Left or Key.Right when (e.Key == Key.Right) != rightToLeft:
                Slides.Next();
                e.Handled = true;
                break;
            case Key.Left or Key.Right:
                Slides.Previous();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
            BeginMoveDrag(e);
    }
}
