// Chip ("tag") input: type a value and press Enter, Tab, comma, semicolon or space to turn it into a chip, paste a
// list to add several at once, Backspace in the empty box removes the last chip, suggestions pop up while typing.
// The control only handles input and keyboard/mouse gestures. Which values are valid, how chips are marked and
// where they are stored is decided by the view model behind AddCommand / RemoveCommand (see ExtensionTagsViewModel).
// Look: Styles/TagEditor.axaml.

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Filee.App.Controls;

/// <summary>How a <see cref="TagChip"/> is highlighted.</summary>
public enum TagChipKind
{
    Normal,
    /// <summary>Allowed but worth a look (e.g. an extension another profile already has).</summary>
    Warning,
    /// <summary>Allowed but probably useless (e.g. an extension no engine can read).</summary>
    Muted,
}

/// <summary>One chip of a <see cref="TagEditor"/>.</summary>
/// <param name="Value">The stored value, passed back to <see cref="TagEditor.RemoveCommand"/>.</param>
/// <param name="Text">What the chip shows (e.g. ".jpg").</param>
public sealed record TagChip(string Value, string Text, TagChipKind Kind = TagChipKind.Normal, string? ToolTip = null)
{
    public bool IsWarning => Kind == TagChipKind.Warning;
    public bool IsMuted => Kind == TagChipKind.Muted;
}

/// <summary>An autocomplete entry of a <see cref="TagEditor"/>.</summary>
/// <param name="Value">Passed to <see cref="TagEditor.AddCommand"/> when the suggestion is picked.</param>
/// <param name="Text">Main label (e.g. ".jpg").</param>
/// <param name="Detail">Secondary label (e.g. "JPG · Image").</param>
/// <param name="Note">Highlighted remark (e.g. the profile that already has this extension).</param>
public sealed record TagSuggestion(string Value, string Text, string? Detail = null, string? Note = null);

/// <summary>
/// Edits a list of short values as chips. Bind <see cref="Tags"/>, <see cref="Text"/> (the unfinished input),
/// <see cref="Suggestions"/>, <see cref="AddCommand"/> (parameter: one raw token) and <see cref="RemoveCommand"/>
/// (parameter: the <see cref="TagChip"/>).
/// </summary>
/// <remarks>
/// Keyboard: Enter / Tab / separators add the typed text (Tab only while there is text, so it still moves focus
/// otherwise); ↓ / ↑ pick a suggestion and Enter or Tab takes it; Esc closes the suggestions, then clears the text;
/// Backspace in the empty box removes the last chip; ← at the start of the box walks into the chips, where ← / → move,
/// Delete / Backspace remove and Esc / Tab return to the box.
/// </remarks>
[TemplatePart("PART_Input", typeof(TextBox))]
[TemplatePart("PART_Chips", typeof(ItemsControl))]
[TemplatePart("PART_Popup", typeof(Popup))]
[TemplatePart("PART_Suggestions", typeof(ListBox))]
public class TagEditor : TemplatedControl
{
    public static readonly StyledProperty<IReadOnlyList<TagChip>?> TagsProperty =
        AvaloniaProperty.Register<TagEditor, IReadOnlyList<TagChip>?>(nameof(Tags));

    public static readonly StyledProperty<IReadOnlyList<TagSuggestion>?> SuggestionsProperty =
        AvaloniaProperty.Register<TagEditor, IReadOnlyList<TagSuggestion>?>(nameof(Suggestions));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TagEditor, string?>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<TagEditor, string?>(nameof(PlaceholderText));

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<TagEditor, ICommand?>(nameof(AddCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<TagEditor, ICommand?>(nameof(RemoveCommand));

    /// <summary>Characters that end a tag while typing or pasting. Line breaks always do.</summary>
    public static readonly StyledProperty<string> SeparatorsProperty =
        AvaloniaProperty.Register<TagEditor, string>(nameof(Separators), ",; ");

    private TextBox? _input;
    private ItemsControl? _chips;
    private Popup? _popup;
    private ListBox? _list;
    private bool _splitting;
    private bool _suggestionsDismissed;

    public IReadOnlyList<TagChip>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    public IReadOnlyList<TagSuggestion>? Suggestions
    {
        get => GetValue(SuggestionsProperty);
        set => SetValue(SuggestionsProperty, value);
    }

    /// <summary>The text typed but not yet turned into a chip.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Executed once per finished token with the raw token text (trimmed, never empty).</summary>
    public ICommand? AddCommand
    {
        get => GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    /// <summary>Executed with the <see cref="TagChip"/> to remove.</summary>
    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public string Separators
    {
        get => GetValue(SeparatorsProperty);
        set => SetValue(SeparatorsProperty, value);
    }

    /// <summary>True while the suggestion list is shown.</summary>
    public bool IsSuggestionListOpen => _popup?.IsOpen == true;

    /// <summary>Moves keyboard focus into the text box.</summary>
    public void FocusInput() => _input?.Focus(NavigationMethod.Tab);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_input is not null)
        {
            _input.RemoveHandler(KeyDownEvent, OnInputKeyDown);
            _input.TextChanged -= OnInputTextChanged;
            _input.GotFocus -= OnInputFocusChanged;
            _input.LostFocus -= OnInputLostFocus;
        }
        if (_chips is not null)
        {
            _chips.RemoveHandler(Button.ClickEvent, OnChipButtonClick);
            _chips.RemoveHandler(KeyDownEvent, OnChipKeyDown);
        }
        if (_list is not null)
        {
            _list.RemoveHandler(PointerReleasedEvent, OnSuggestionReleased);
            _list.ContainerPrepared -= OnSuggestionContainerPrepared;
        }

        _input = e.NameScope.Find<TextBox>("PART_Input");
        _chips = e.NameScope.Find<ItemsControl>("PART_Chips");
        _popup = e.NameScope.Find<Popup>("PART_Popup");
        _list = e.NameScope.Find<ListBox>("PART_Suggestions");

        if (_input is not null)
        {
            // Tunnel: see Enter / Tab / arrows before the text box acts on them.
            _input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
            _input.TextChanged += OnInputTextChanged;
            _input.GotFocus += OnInputFocusChanged;
            _input.LostFocus += OnInputLostFocus;
        }
        if (_chips is not null)
        {
            _chips.AddHandler(Button.ClickEvent, OnChipButtonClick);
            _chips.AddHandler(KeyDownEvent, OnChipKeyDown);
        }
        if (_list is not null)
        {
            _list.AddHandler(PointerReleasedEvent, OnSuggestionReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            _list.ContainerPrepared += OnSuggestionContainerPrepared;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SuggestionsProperty)
            UpdateSuggestionList();
        else if (change.Property == TextProperty)
        {
            _suggestionsDismissed = false;
            UpdateSuggestionList();
        }
    }

    /// <summary>A click on the empty part of the box puts the caret into the text box.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || _input is null || ReferenceEquals(e.Source, _input) || _input.IsFocused)
            return;
        if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
            return;
        _input.Focus(NavigationMethod.Pointer);
        e.Handled = true;
    }

    // ───────── Typing ─────────

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_splitting || _input?.Text is not { } text || text.IndexOfAny(SeparatorChars()) < 0)
            return;

        // A separator was typed or a list was pasted: everything before the last separator becomes chips,
        // the rest stays in the box.
        var last = text.LastIndexOfAny(SeparatorChars());
        var done = text[..last];
        var rest = text[(last + 1)..];
        _splitting = true;
        try
        {
            AddTokens(done);
            _input.Text = rest;
            _input.CaretIndex = rest.Length;
        }
        finally
        {
            _splitting = false;
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_input is null)
            return;
        var hasText = !string.IsNullOrWhiteSpace(_input.Text);
        switch (e.Key)
        {
            case Key.Enter:
                // Always handled: a bare Enter must not insert a line break or press a dialog's default button.
                if (!AcceptHighlighted())
                    CommitInput();
                e.Handled = true;
                break;

            case Key.Tab when e.KeyModifiers == KeyModifiers.None && (hasText || HighlightedIndex >= 0):
                if (!AcceptHighlighted())
                    CommitInput();
                e.Handled = true;
                break;

            case Key.Back when string.IsNullOrEmpty(_input.Text):
                if (Tags is { Count: > 0 } tags)
                    RemoveCommand?.Execute(tags[^1]);
                e.Handled = true;
                break;

            case Key.Left when _input.CaretIndex == 0 && _input.SelectionStart == _input.SelectionEnd && Tags is { Count: > 0 }:
                FocusChip(Tags.Count - 1);
                e.Handled = true;
                break;

            case Key.Down when Suggestions is { Count: > 0 } && hasText:
                _suggestionsDismissed = false;
                UpdateSuggestionList();
                MoveHighlight(+1);
                e.Handled = true;
                break;

            case Key.Up when IsSuggestionListOpen:
                MoveHighlight(-1);
                e.Handled = true;
                break;

            case Key.Escape when IsSuggestionListOpen:
                _suggestionsDismissed = true;
                UpdateSuggestionList();
                e.Handled = true;
                break;

            case Key.Escape when hasText:
                _input.Text = "";
                e.Handled = true;
                break;
        }
    }

    private void OnInputFocusChanged(object? sender, RoutedEventArgs e) => UpdateSuggestionList();

    /// <summary>Leaving the box keeps what was typed, like pressing Enter.</summary>
    private void OnInputLostFocus(object? sender, RoutedEventArgs e)
    {
        CommitInput();
        UpdateSuggestionList();
    }

    /// <summary>Adds the typed text (possibly several tokens). Returns false when the box was empty.</summary>
    private bool CommitInput()
    {
        if (_input is null || string.IsNullOrWhiteSpace(_input.Text))
            return false;
        var text = _input.Text;
        _splitting = true;
        try
        {
            _input.Text = "";
        }
        finally
        {
            _splitting = false;
        }
        AddTokens(text);
        return true;
    }

    private void AddTokens(string text)
    {
        foreach (var token in text.Split(SeparatorChars(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (AddCommand is { } command && command.CanExecute(token))
                command.Execute(token);
        }
    }

    private char[] SeparatorChars() => [.. Separators, '\r', '\n', '\t'];

    // ───────── Suggestions ─────────

    private int HighlightedIndex => _list?.SelectedIndex ?? -1;

    private void UpdateSuggestionList()
    {
        if (_popup is null)
            return;
        var open = _input?.IsFocused == true
                   && !_suggestionsDismissed
                   && !string.IsNullOrWhiteSpace(Text)
                   && Suggestions is { Count: > 0 };
        if (open && _list is not null && _popup.Child is Control child)
            child.MinWidth = Math.Max(200, Bounds.Width);
        _popup.IsOpen = open;
        if (_list is not null && (!open || _list.SelectedIndex >= (Suggestions?.Count ?? 0)))
            _list.SelectedIndex = -1;
    }

    private void MoveHighlight(int delta)
    {
        if (_list is null || Suggestions is not { Count: > 0 } items || !IsSuggestionListOpen)
            return;
        var index = Math.Clamp(_list.SelectedIndex + delta, -1, items.Count - 1);
        _list.SelectedIndex = index;
        if (index >= 0)
            _list.ScrollIntoView(index);
    }

    /// <summary>Takes the highlighted suggestion, if any.</summary>
    private bool AcceptHighlighted()
    {
        if (!IsSuggestionListOpen || _list?.SelectedItem is not TagSuggestion suggestion)
            return false;
        Accept(suggestion);
        return true;
    }

    private void Accept(TagSuggestion suggestion)
    {
        if (_input is not null)
        {
            _splitting = true;
            try
            {
                _input.Text = "";
            }
            finally
            {
                _splitting = false;
            }
        }
        if (AddCommand is { } command && command.CanExecute(suggestion.Value))
            command.Execute(suggestion.Value);
        _input?.Focus(NavigationMethod.Pointer);
    }

    private void OnSuggestionReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: TagSuggestion suggestion })
        {
            Accept(suggestion);
            e.Handled = true;
        }
    }

    /// <summary>Suggestions never take focus, so typing continues while the list is used with the mouse.</summary>
    private static void OnSuggestionContainerPrepared(object? sender, ContainerPreparedEventArgs e) => e.Container.Focusable = false;

    // ───────── Chips ─────────

    private void OnChipButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is { DataContext: TagChip chip })
        {
            RemoveCommand?.Execute(chip);
            _input?.Focus(NavigationMethod.Pointer);
            e.Handled = true;
        }
    }

    private void OnChipKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not Control { DataContext: TagChip chip } || Tags is not { } tags)
            return;
        var index = IndexOf(tags, chip);
        switch (e.Key)
        {
            case Key.Left:
                FocusChip(Math.Max(0, index - 1));
                break;
            case Key.Right when index < tags.Count - 1:
                FocusChip(index + 1);
                break;
            case Key.Right or Key.Escape or Key.End or Key.Down:
                FocusInput();
                break;
            case Key.Delete or Key.Back:
                RemoveCommand?.Execute(chip);
                // The chip list may be rebuilt by the view model; focus the neighbour once it is.
                Dispatcher.UIThread.Post(() =>
                {
                    if (Tags is { Count: > 0 } now)
                        FocusChip(Math.Min(index, now.Count - 1));
                    else
                        FocusInput();
                }, DispatcherPriority.Loaded);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void FocusChip(int index)
    {
        if (_chips?.ContainerFromIndex(index) is not { } container)
            return;
        var chip = container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tagchip"));
        chip?.Focus(NavigationMethod.Directional);
    }

    private static int IndexOf(IReadOnlyList<TagChip> tags, TagChip chip)
    {
        for (var i = 0; i < tags.Count; i++)
        {
            if (Equals(tags[i], chip))
                return i;
        }
        return -1;
    }
}
