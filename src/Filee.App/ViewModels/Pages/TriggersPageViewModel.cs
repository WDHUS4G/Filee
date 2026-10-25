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

    // Runs before OnPropertyChanged below, which reads every choice.
    partial void OnKindChanged(Choice<TriggerKind>? oldValue, Choice<TriggerKind> newValue) => Choices.Restore(oldValue, newValue, v => Kind = v);
    partial void OnButtonChanged(Choice<TriggerMouseButton>? oldValue, Choice<TriggerMouseButton> newValue) => Choices.Restore(oldValue, newValue, v => Button = v);
    partial void OnScopeChanged(Choice<TriggerScope>? oldValue, Choice<TriggerScope> newValue) => Choices.Restore(oldValue, newValue, v => Scope = v);

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

/// <summary>
/// How the open donut flips to its other face: a key (Tab unless the user records another; cleared, none) and the
/// mouse wheel. Both work while dragging and in click mode.
/// </summary>
public sealed partial class FlipOptionsViewModel : ObservableObject
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly Action _save;
    private readonly bool _loading;

    public FlipOptionsViewModel(UserDataStore store, ILocalizer loc, Action save)
    {
        _store = store;
        _loc = loc;
        _save = save;
        _loading = true;
        FlipWithWheel = store.Settings.Donut.FlipWithWheel;
        _loading = false;
    }

    private DonutSettings Donut => _store.Settings.Donut;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeyText))] private bool _isRecording;

    /// <summary>Why the last key pressed while recording wasn't taken, or null.</summary>
    [ObservableProperty] private string? _error;

    [ObservableProperty] private bool _flipWithWheel;

    /// <summary>The key that flips now ("Tab", "F2"), "Press a key…" while recording, or "—" for none.</summary>
    public string KeyText => IsRecording ? _loc["triggers.recording_key"]
        : Donut.EffectiveFlipKey(_store.Settings.Triggers) is { } key ? KeyNames.Display(key) : "—";

    partial void OnIsRecordingChanged(bool value)
    {
        if (value)
            Error = null;
    }

    partial void OnFlipWithWheelChanged(bool value)
    {
        if (_loading)
            return;
        Donut.FlipWithWheel = value;
        _save();
    }

    [RelayCommand]
    private void ResetKey() => SetKey(DonutSettings.DefaultFlipKey);

    [RelayCommand]
    private void ClearKey() => SetKey("");

    private void SetKey(string key)
    {
        IsRecording = false;
        Error = null;
        Donut.FlipKey = key;
        _save();
        OnPropertyChanged(nameof(KeyText));
    }

    /// <summary>
    /// Called by the view while recording: the key pressed becomes the flip key (its modifiers don't count: a drag holds
    /// one anyway). Modifier keys alone wait for a real key; Esc, Enter, Space, the arrows, Backspace and the key of a
    /// keyboard shortcut are refused with the reason. Returns true when the key was handled.
    /// </summary>
    public bool Record(Key key)
    {
        if (!IsRecording || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
                or Key.RightShift or Key.LWin or Key.RWin)
            return false;
        if (KeyNames.ToHookName(key) is not { } name)
        {
            Error = _loc["triggers.flip_key_unknown"];
            IsRecording = false;
            return true;
        }
        if (!DonutSettings.IsFlipKeyAllowed(name, _store.Settings.Triggers))
        {
            Error = _loc.Format("triggers.flip_key_taken", KeyNames.Display(name));
            IsRecording = false;
            return true;
        }
        SetKey(name);
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
        Flip = new FlipOptionsViewModel(store, loc, Save);
    }

    /// <summary>The key and the wheel that flip the open donut.</summary>
    public FlipOptionsViewModel Flip { get; }

    public ObservableCollection<TriggerGestureViewModel> Gestures { get; } = [];

    public bool IsMacOS => _platform is MacOSPlatformServices;
    public bool CanRetryHook => _triggers is not null && (_platform?.SupportsGlobalPointerGestures == true || IsMacOS);
    public string? IntegrationError => _accessibilityError ?? _integration?.Error;
    public string? PlatformNotice => _platform switch
    {
        MacOSPlatformServices => _loc["triggers.macos_permissions"],
        // FILEE_WAYLAND_GESTURES=0 turns the GNOME bridge off; the plain Wayland notice applies then.
        LinuxPlatformServices when OperatingSystem.IsLinux() && LinuxPlatformServices.UsesGnomePointerBridge =>
            _loc["triggers.linux_gnome_wayland"],
        LinuxPlatformServices linux when OperatingSystem.IsLinux() && LinuxPlatformServices.IsWaylandSession =>
            _loc[linux.SupportsGlobalPointerGestures ? "triggers.linux_wayland_experimental" : "triggers.linux_wayland"],
        LinuxPlatformServices => _loc["triggers.linux_x11"],
        _ => null,
    };
    public string? HookStatus => _triggers?.Error is { } error ? HookFailure(_loc, error, _triggers.LinuxBackendMissing)
        : _triggers?.GnomeExtensionStatus is { } gnome ? _loc[GnomeStatusKey(gnome)]
        : _triggers is null ? null : _loc[_triggers.IsRunning ? "triggers.hook_running" : "triggers.hook_stopped"];

    internal static string GnomeStatusKey(GnomeBridgeStatus status) => status switch
    {
        GnomeBridgeStatus.Connected => "triggers.hook_running",
        GnomeBridgeStatus.VersionMismatch => "triggers.gnome_version_mismatch",
        _ => "triggers.gnome_waiting",
    };

    /// <summary>
    /// A Linux back end that doesn't load is explained (X11 session, X libraries) instead of showing libuiohook's
    /// result code; the code is in the log.
    /// </summary>
    internal static string HookFailure(ILocalizer loc, string error, bool linuxBackendMissing) =>
        linuxBackendMissing ? loc["triggers.hook_linux_backend"] : loc.Format("triggers.hook_failed", error);

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
