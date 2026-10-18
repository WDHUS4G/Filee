// Shortcut / gesture settings. Every gesture is fully configurable: kind, modifiers, mouse button,
// extra key (recorded), drag distance, hold time and where it is active.

using System.Collections.ObjectModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Filee.Platform.Linux;
using Filee.Platform.MacOS;
using ModifierKeys = Filee.Core.Settings.ModifierKeys;

namespace Filee.App.ViewModels.Pages;

public sealed partial class TriggerGestureViewModel : ObservableObject
{
    private readonly TriggerGesture _gesture;
    private readonly Action _save;
    private bool _loading = true;

    public TriggerGestureViewModel(TriggerGesture gesture, ILocalizer loc, Action save)
    {
        _gesture = gesture;
        _save = save;
        Kinds = Enum.GetValues<TriggerKind>().Select(k => new Choice<TriggerKind>(k, loc[$"triggers.kind.{k}"])).ToList();
        Buttons = Enum.GetValues<TriggerMouseButton>().Where(b => b != TriggerMouseButton.None)
            .Select(b => new Choice<TriggerMouseButton>(b, loc[$"triggers.button.{b}"])).ToList();
        // The file manager has its own name on each system: Explorer, Finder, the Linux desktop's file manager.
        var platform = OperatingSystem.IsMacOS() ? "_macos" : OperatingSystem.IsLinux() ? "_linux" : "";
        Scopes = Enum.GetValues<TriggerScope>()
            .Select(s => new Choice<TriggerScope>(s, loc[s == TriggerScope.FileManager ? $"triggers.scope.{s}{platform}" : $"triggers.scope.{s}"]))
            .ToList();
        RecordLabel = loc["triggers.record"];
        RecordingLabel = loc["triggers.recording"];
        SystemLabel = loc["triggers.threshold_system"];

        Enabled = gesture.Enabled;
        Kind = Kinds.First(k => k.Value == gesture.Kind);
        Ctrl = gesture.Modifiers.HasFlag(ModifierKeys.Ctrl);
        Alt = gesture.Modifiers.HasFlag(ModifierKeys.Alt);
        Shift = gesture.Modifiers.HasFlag(ModifierKeys.Shift);
        Meta = gesture.Modifiers.HasFlag(ModifierKeys.Meta);
        Button = Buttons.FirstOrDefault(b => b.Value == gesture.Button) ?? Buttons[0];
        Key = gesture.Key;
        DragThreshold = gesture.DragThreshold;
        HoldMilliseconds = gesture.HoldMilliseconds;
        Scope = Scopes.First(s => s.Value == gesture.Scope);
        _loading = false;
    }

    public TriggerGesture Gesture => _gesture;
    public IReadOnlyList<Choice<TriggerKind>> Kinds { get; }
    public IReadOnlyList<Choice<TriggerMouseButton>> Buttons { get; }
    public IReadOnlyList<Choice<TriggerScope>> Scopes { get; }
    public string RecordLabel { get; }
    public string RecordingLabel { get; }
    public string SystemLabel { get; }
    public string MetaLabel => OperatingSystem.IsMacOS() ? "⌘ Cmd" : "Win";

    [ObservableProperty] private bool _enabled;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UsesMouse), nameof(UsesKey), nameof(UsesThreshold), nameof(UsesHold))]
    private Choice<TriggerKind> _kind = null!;

    [ObservableProperty] private bool _ctrl;
    [ObservableProperty] private bool _alt;
    [ObservableProperty] private bool _shift;
    [ObservableProperty] private bool _meta;
    [ObservableProperty] private Choice<TriggerMouseButton> _button = null!;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeyText))] private string? _key;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ThresholdText))] private int _dragThreshold;
    [ObservableProperty] private int _holdMilliseconds;
    [ObservableProperty] private Choice<TriggerScope> _scope = null!;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeyText))] private bool _isRecording;

    public bool UsesMouse => Kind.Value != TriggerKind.KeyChord;
    public bool UsesKey => Kind.Value == TriggerKind.KeyChord;
    public bool UsesThreshold => Kind.Value == TriggerKind.Drag;
    public bool UsesHold => Kind.Value == TriggerKind.Hold;
    public string KeyText => IsRecording ? RecordingLabel : string.IsNullOrEmpty(Key) ? "—" : Key;
    public string ThresholdText => DragThreshold == 0 ? SystemLabel : $"{DragThreshold}px";
    public string Summary => GestureText.Describe(_gesture);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(IsRecording) or nameof(KeyText) or nameof(ThresholdText) or nameof(Summary)
            or nameof(UsesMouse) or nameof(UsesKey) or nameof(UsesThreshold) or nameof(UsesHold))
            return;

        _gesture.Enabled = Enabled;
        _gesture.Kind = Kind.Value;
        _gesture.Modifiers = (Ctrl ? ModifierKeys.Ctrl : 0) | (Alt ? ModifierKeys.Alt : 0) |
                             (Shift ? ModifierKeys.Shift : 0) | (Meta ? ModifierKeys.Meta : 0);
        _gesture.Button = Kind.Value == TriggerKind.KeyChord ? TriggerMouseButton.None : Button.Value;
        _gesture.Key = Key;
        _gesture.DragThreshold = Math.Clamp(DragThreshold, 0, 200);
        _gesture.HoldMilliseconds = Math.Clamp(HoldMilliseconds, 150, 3000);
        _gesture.Scope = Scope.Value;
        OnPropertyChanged(nameof(Summary));
        _save();
    }

    [RelayCommand]
    private void ToggleRecording() => IsRecording = !IsRecording;

    [RelayCommand]
    private void ClearKey() => Key = null;

    /// <summary>Called by the view while recording. Returns true when the key was accepted.</summary>
    public bool Record(Key key, KeyModifiers modifiers)
    {
        if (!IsRecording || key is Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl or Avalonia.Input.Key.LeftAlt
                or Avalonia.Input.Key.RightAlt or Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift
                or Avalonia.Input.Key.LWin or Avalonia.Input.Key.RWin)
            return false;

        var name = KeyNames.ToHookName(key);
        if (name is null)
            return false;

        _loading = true;
        Ctrl = modifiers.HasFlag(KeyModifiers.Control);
        Alt = modifiers.HasFlag(KeyModifiers.Alt);
        Shift = modifiers.HasFlag(KeyModifiers.Shift);
        Meta = modifiers.HasFlag(KeyModifiers.Meta);
        _loading = false;
        IsRecording = false;
        Key = name; // triggers save
        return true;
    }
}

public sealed partial class TriggersPageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices? _platform;
    private readonly TriggerService? _triggers;
    private readonly SystemIntegrationService? _integration;
    private string? _accessibilityError;

    public TriggersPageViewModel(UserDataStore store, ILocalizer loc, IPlatformServices? platform = null, TriggerService? triggers = null,
        SystemIntegrationService? integration = null)
    {
        _store = store;
        _loc = loc;
        _platform = platform;
        _triggers = triggers;
        _integration = integration;
        store.SettingsChanged += OnTriggerSettingsChanged;
        if (triggers is not null)
            triggers.StatusChanged += OnTriggerStatusChanged;
        foreach (var gesture in store.Settings.Triggers)
            Gestures.Add(new TriggerGestureViewModel(gesture, loc, Save));
        ExcludedApps = new ExcludedAppsViewModel(store.Settings.ExcludedProcesses, platform, Save);
    }

    public ObservableCollection<TriggerGestureViewModel> Gestures { get; } = [];

    public bool IsMacOS => _platform is MacOSPlatformServices;
    public bool CanRetryHook => _triggers is not null && (_platform?.SupportsGlobalPointerGestures == true || IsMacOS);
    public string? IntegrationError => _accessibilityError ?? _integration?.Error;
    public string? PlatformNotice => _platform switch
    {
        MacOSPlatformServices => _loc["triggers.macos_permissions"],
        LinuxPlatformServices when OperatingSystem.IsLinux() && LinuxPlatformServices.IsWaylandSession => _loc["triggers.linux_wayland"],
        LinuxPlatformServices => _loc["triggers.linux_x11"],
        _ => null,
    };
    public string? HookStatus => _triggers?.Error is { } error ? _loc.Format("triggers.hook_failed", error)
        : _triggers is null ? null : _loc[_triggers.IsRunning ? "triggers.hook_running" : "triggers.hook_stopped"];

    private void OnTriggerStatusChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(HookStatus));
    private void OnTriggerSettingsChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(IntegrationError));

    [RelayCommand]
    private void OpenAccessibility()
    {
        try
        {
            _accessibilityError = null;
            if (OperatingSystem.IsMacOS() && _platform is MacOSPlatformServices mac)
                mac.OpenAccessibilitySettings();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _accessibilityError = ex.Message;
        }
        OnPropertyChanged(nameof(IntegrationError));
    }

    [RelayCommand]
    private void RetryHook()
    {
        _triggers?.Dispose();
        _triggers?.Start();
        OnPropertyChanged(nameof(HookStatus));
    }

    public void Dispose()
    {
        _store.SettingsChanged -= OnTriggerSettingsChanged;
        if (_triggers is not null)
            _triggers.StatusChanged -= OnTriggerStatusChanged;
    }

    /// <summary>"Never trigger in these apps" as tags.</summary>
    public ExcludedAppsViewModel ExcludedApps { get; }

    [RelayCommand]
    private void Add()
    {
        var gesture = new TriggerGesture { Modifiers = ModifierKeys.Alt, Button = TriggerMouseButton.Left };
        _store.Settings.Triggers.Add(gesture);
        Gestures.Add(new TriggerGestureViewModel(gesture, _loc, Save));
        Save();
    }

    [RelayCommand]
    private void Remove(TriggerGestureViewModel item)
    {
        _store.Settings.Triggers.Remove(item.Gesture);
        Gestures.Remove(item);
        Save();
    }

    private void Save() => _store.SaveSettings();
}

/// <summary>Maps Avalonia keys to SharpHook key names (KeyCode without "Vc").</summary>
public static class KeyNames
{
    public static string? ToHookName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "NumPad" + (int)(key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Escape => "Escape",
        Key.Back => "Backspace",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.OemTilde => "BackQuote",
        Key.OemMinus => "Minus",
        Key.OemPlus => "Equals",
        Key.OemOpenBrackets => "OpenBracket",
        Key.OemCloseBrackets => "CloseBracket",
        Key.OemPipe => "Backslash",
        Key.OemSemicolon => "Semicolon",
        Key.OemQuotes => "Quote",
        Key.OemComma => "Comma",
        Key.OemPeriod => "Period",
        Key.OemQuestion => "Slash",
        Key.PrintScreen => "PrintScreen",
        Key.Pause => "Pause",
        _ => null,
    };
}
