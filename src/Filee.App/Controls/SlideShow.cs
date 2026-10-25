// A deck of slides: the picture or animation on top, the title and a line or two below, dots for the position.
// Built in code (no XAML) so Filee.Setup can link it as it is; it uses only the Soft look's resources (SoftRadius,
// AccentBrush, SurfaceAltBrush, …) and the h2 / muted text classes, which both apps define.
//
// Filee's "What's new" window adds Previous / Next around it; Setup's progress page sets AutoAdvance instead.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Filee.App.Controls;

public sealed class SlideShow : UserControl
{
    public static readonly StyledProperty<bool> PlayAnimationsProperty =
        AvaloniaProperty.Register<SlideShow, bool>(nameof(PlayAnimations), true);

    public static readonly StyledProperty<double> MediaWidthProperty =
        AvaloniaProperty.Register<SlideShow, double>(nameof(MediaWidth), 480);

    public static readonly StyledProperty<double> MediaHeightProperty =
        AvaloniaProperty.Register<SlideShow, double>(nameof(MediaHeight), 270);

    public static readonly StyledProperty<TimeSpan?> AutoAdvanceProperty =
        AvaloniaProperty.Register<SlideShow, TimeSpan?>(nameof(AutoAdvance));

    private readonly Border _frame;
    private readonly AnimatedImage _media;
    private readonly Grid _texts;
    private readonly StackPanel _dots;
    private readonly List<(TextBlock Title, TextBlock Text, StackPanel Panel)> _slideTexts = [];
    private IReadOnlyList<Slide> _slides = [];
    private Func<string, string> _localize = key => key;
    private Func<string?> _language = () => null;
    private DispatcherTimer? _advance;
    private int _index;

    public SlideShow()
    {
        _media = new AnimatedImage { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(_media, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        _frame = new Border
        {
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _media,
        };
        _frame.Bind(Border.CornerRadiusProperty, _frame.GetResourceObservable("SoftRadius"));
        _frame.Bind(Border.BackgroundProperty, _frame.GetResourceObservable("SurfaceAltBrush"));
        _frame.Bind(Border.BorderBrushProperty, _frame.GetResourceObservable("SubtleBorderBrush"));
        _frame.BorderThickness = new Thickness(1);

        // Every slide's text sits in the same cell, only the current one visible: the height is the tallest slide's,
        // so the window doesn't jump from slide to slide.
        _texts = new Grid { Margin = new Thickness(8, 18, 8, 0) };
        _dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 6,
            Margin = new Thickness(0, 16, 0, 0),
        };
        Content = new StackPanel { Children = { _frame, _texts, _dots } };
        UpdateMediaSize();
    }

    /// <summary>False shows each animation's first frame (reduced motion).</summary>
    public bool PlayAnimations
    {
        get => GetValue(PlayAnimationsProperty);
        set => SetValue(PlayAnimationsProperty, value);
    }

    /// <summary>Size of the picture area in pixels (16:9 by default); the pictures are scaled to fit.</summary>
    public double MediaWidth
    {
        get => GetValue(MediaWidthProperty);
        set => SetValue(MediaWidthProperty, value);
    }

    public double MediaHeight
    {
        get => GetValue(MediaHeightProperty);
        set => SetValue(MediaHeightProperty, value);
    }

    /// <summary>Moves to the next slide after this long, back to the first after the last (Setup). Null: never.</summary>
    public TimeSpan? AutoAdvance
    {
        get => GetValue(AutoAdvanceProperty);
        set => SetValue(AutoAdvanceProperty, value);
    }

    public IReadOnlyList<Slide> Slides => _slides;

    /// <summary>The slide shown.</summary>
    public int Index => _index;

    public bool IsFirst => _index == 0;
    public bool IsLast => _index >= _slides.Count - 1;

    /// <summary>The picture of the current slide (for tests and the media generator).</summary>
    public AnimatedImage Media => _media;

    /// <summary>Raised after the slide changed.</summary>
    public event EventHandler? IndexChanged;

    /// <summary>Shows <paramref name="slides"/> from the first, their texts looked up with <paramref name="localize"/>.</summary>
    public void Show(IReadOnlyList<Slide> slides, Func<string, string> localize, Func<string?>? language = null)
    {
        _slides = slides;
        _localize = localize;
        _language = language ?? (() => null);
        _texts.Children.Clear();
        _slideTexts.Clear();
        foreach (var _ in slides)
        {
            var title = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            title.Classes.Add("h2");
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            text.Classes.Add("muted");
            var panel = new StackPanel { Children = { title, text } };
            _slideTexts.Add((title, text, panel));
            _texts.Children.Add(panel);
        }
        _dots.Children.Clear();
        for (var i = 0; i < slides.Count; i++)
        {
            var target = i;
            var dot = new Border { Height = 8, CornerRadius = new CornerRadius(4), Cursor = new Cursor(StandardCursorType.Hand) };
            dot.PointerPressed += (_, e) =>
            {
                GoTo(target);
                e.Handled = true;
            };
            _dots.Children.Add(dot);
        }
        _dots.IsVisible = slides.Count > 1;
        RefreshTexts();
        _index = -1;
        GoTo(0);
    }

    /// <summary>Looks the texts up again (the UI language changed).</summary>
    public void RefreshTexts()
    {
        for (var i = 0; i < _slideTexts.Count && i < _slides.Count; i++)
        {
            _slideTexts[i].Title.Text = _localize(_slides[i].TitleKey);
            _slideTexts[i].Text.Text = _localize(_slides[i].TextKey);
        }
        UpdateMedia();
    }

    public bool Next() => GoTo(_index + 1);

    public bool Previous() => GoTo(_index - 1);

    /// <summary>Shows slide <paramref name="index"/>; false when there is no such slide or it is already shown.</summary>
    public bool GoTo(int index)
    {
        if (index < 0 || index >= _slides.Count || index == _index)
            return false;
        _index = index;
        UpdateMedia();
        for (var i = 0; i < _slideTexts.Count; i++)
        {
            var current = i == index;
            var panel = _slideTexts[i].Panel;
            panel.Opacity = current ? 1 : 0;
            panel.IsHitTestVisible = current;
            Avalonia.Automation.AutomationProperties.SetAccessibilityView(panel,
                current ? Avalonia.Automation.AccessibilityView.Content : Avalonia.Automation.AccessibilityView.Raw);
        }
        UpdateDots();
        RestartAdvance();
        IndexChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void UpdateDots()
    {
        for (var i = 0; i < _dots.Children.Count; i++)
        {
            var dot = (Border)_dots.Children[i];
            var current = i == _index;
            dot.Width = current ? 22 : 8;
            dot.Bind(Border.BackgroundProperty, dot.GetResourceObservable(current ? "AccentBrush" : "SubtleBorderBrush"));
            dot.Opacity = current ? 1 : 0.9;
        }
    }

    private void UpdateMedia()
    {
        if (_index < 0 || _index >= _slides.Count)
            return;
        var media = _slides[_index].MediaFor(_language());
        _media.PosterFrame = media.PosterFrame;
        _media.Source = media.Source;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayAnimationsProperty)
            _media.AutoPlay = PlayAnimations;
        else if (change.Property == MediaWidthProperty || change.Property == MediaHeightProperty)
            UpdateMediaSize();
        else if (change.Property == AutoAdvanceProperty)
            RestartAdvance();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _advance?.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RestartAdvance();
    }

    private void UpdateMediaSize()
    {
        _frame.Width = MediaWidth;
        _frame.Height = MediaHeight;
    }

    private void RestartAdvance()
    {
        _advance?.Stop();
        if (AutoAdvance is not { } interval || interval <= TimeSpan.Zero || _slides.Count < 2 || TopLevel.GetTopLevel(this) is null)
            return;
        _advance ??= new DispatcherTimer();
        _advance.Interval = interval;
        _advance.Tick -= OnAdvance;
        _advance.Tick += OnAdvance;
        _advance.Start();
    }

    private void OnAdvance(object? sender, EventArgs e)
    {
        if (!Next())
            GoTo(0);
    }

    /// <summary>One automatic step, as the timer takes it (tests).</summary>
    internal void AdvanceNow() => OnAdvance(this, EventArgs.Empty);
}
