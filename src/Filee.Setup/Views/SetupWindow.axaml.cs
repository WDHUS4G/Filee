using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Filee.App.Controls;
using Filee.Setup.Controls;
using Filee.Setup.ViewModels;

namespace Filee.Setup.Views;

public partial class SetupWindow : Window
{
    private readonly SetupViewModel _model;
    private bool _closing;

    /// <summary>For the XAML previewer.</summary>
    public SetupWindow() : this(null!, false)
    {
    }

    /// <param name="silent">/SILENT: install at once, show only the progress card and close when done.</param>
    public SetupWindow(SetupViewModel model, bool silent)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        if (model is null)
            return;
        model.Silent = silent;
        if (!SetupMotion.Enabled)
            Classes.Add("reduced-motion");
        else
            Host.PageTransition = new CrossFade(TimeSpan.FromMilliseconds(320));
        model.PickFolder = PickFolderAsync;
        model.CopyText = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        model.CloseRequested += (_, _) =>
        {
            _closing = true;
            Close();
        };
        model.PropertyChanged += OnModelChanged;
        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        // Installing can't be stopped halfway: the window stays until the files are in place (or back).
        Closing += (_, e) =>
        {
            if (_closing)
                return;
            if (model.IsProgress)
                e.Cancel = true;
            else
                model.WindowClosing();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                model.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        ApplyTheme();
        ShowState();
        Opened += async (_, _) =>
        {
            if (silent)
            {
                if (model.IsUninstall)
                    await model.UninstallAsync();
                else
                    await model.InstallAsync();
                SilentFinished?.Invoke(this, EventArgs.Empty);
                _closing = true;
                Close();
                return;
            }
            FocusPrimary();
        };
    }

    /// <summary>A /SILENT run ended (before the window closes): Program shows an error message if there was one.</summary>
    public event EventHandler? SilentFinished;

    /// <summary>The state shown now (tests).</summary>
    public Control? State => Host.Content as Control;

    private void ApplyTheme()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        // Links and the version pill: the deeper accent on light, the accent itself on dark (enough contrast on both).
        if (Application.Current?.FindResource(dark ? "AccentBrush" : "AccentStrongBrush") is { } link)
            Resources["Setup.LinkBrush"] = link;
        Backdrop.Intensity = dark ? 0.22 : 0.30;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SetupViewModel.Page):
                ShowState();
                break;
            case nameof(SetupViewModel.OptionsOpen) when _model.OptionsOpen:
                LanguageBox.Focus();
                break;
            case nameof(SetupViewModel.OptionsOpen):
                FocusPrimary();
                break;
            case nameof(SetupViewModel.SelectedLanguage):
                Find<SlideShow>("Slides")?.RefreshTexts();
                break;
        }
    }

    /// <summary>Builds the card for the current state (StateViews.axaml) and crossfades to it.</summary>
    private void ShowState()
    {
        if (this.FindResource("Setup." + _model.Page) is not IDataTemplate template)
            return;
        var state = template.Build(_model);
        if (state is null)
            return;
        state.DataContext = _model;
        Host.Content = state;
        ShowSlides();
        FocusPrimary();
    }

    private T? Find<T>(string name) where T : Control =>
        State?.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// <summary>The "What's new" slides on the progress card, fading in at each change.</summary>
    private void ShowSlides()
    {
        if (!_model.ShowShowcase || _model.Deck is not { } deck || Find<SlideShow>("Slides") is not { } slides)
            return;
        slides.PlayAnimations = SetupMotion.Enabled;
        if (SetupMotion.Enabled)
        {
            slides.Media.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(450) }];
            slides.IndexChanged += (_, _) =>
            {
                slides.Media.Opacity = 0;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => slides.Media.Opacity = 1, Avalonia.Threading.DispatcherPriority.Render);
            };
        }
        slides.Show(deck.Slides, _model.Text, () => _model.SelectedLanguage.Code);
    }

    private void FocusPrimary()
    {
        if (_model.OptionsOpen)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Find<Button>("PrimaryButton")?.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private async Task<string?> PickFolderAsync(string current)
    {
        IStorageFolder? start = null;
        try
        {
            var existing = current;
            while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing))
                existing = Path.GetDirectoryName(existing);
            if (!string.IsNullOrEmpty(existing))
                start = await StorageProvider.TryGetFolderFromPathAsync(existing);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation = start,
            Title = Title,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
