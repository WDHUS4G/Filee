// Describes what the user has to press to open the donut toolbar. Deliberately flexible:
// any modifier combination, any mouse button, optional extra key, drag / hold / key-only.

namespace Filee.Core.Settings;

/// <summary>Modifier keys. Left/right variants are treated the same.</summary>
[Flags]
public enum ModifierKeys
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    /// <summary>Windows key on Windows, Command on macOS.</summary>
    Meta = 8,
}

/// <summary>Mouse buttons a gesture can use.</summary>
public enum TriggerMouseButton
{
    None,
    Left,
    Right,
    Middle,
    X1,
    X2,
}

/// <summary>What physical action completes the gesture.</summary>
public enum TriggerKind
{
    /// <summary>Hold the modifiers and drag with the mouse button past the threshold. Used for file drags.</summary>
    Drag,
    /// <summary>Hold the modifiers and keep the mouse button pressed without moving for <see cref="TriggerGesture.HoldMilliseconds"/>.</summary>
    Hold,
    /// <summary>Press the modifiers + <see cref="TriggerGesture.Key"/> (no mouse). Uses the file manager selection.</summary>
    KeyChord,
}

/// <summary>Where the gesture is active.</summary>
public enum TriggerScope
{
    /// <summary>Only when the cursor is over the system file manager (Explorer / Finder) or the desktop.</summary>
    FileManager,
    /// <summary>Everywhere, except excluded applications.</summary>
    Anywhere,
}

/// <summary>A user-configurable gesture that opens the donut toolbar.</summary>
public sealed class TriggerGesture
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public bool Enabled { get; set; } = true;
    public TriggerKind Kind { get; set; } = TriggerKind.Drag;
    public ModifierKeys Modifiers { get; set; } = ModifierKeys.Ctrl;
    public TriggerMouseButton Button { get; set; } = TriggerMouseButton.Left;

    /// <summary>
    /// Extra non-modifier key (SharpHook <c>KeyCode</c> name without the "Vc" prefix, e.g. <c>"Space"</c>).
    /// Required for <see cref="TriggerKind.KeyChord"/>, optional otherwise.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>Distance in logical pixels the mouse must travel before a drag counts. 0 = system default.</summary>
    public int DragThreshold { get; set; }

    /// <summary>Press duration for <see cref="TriggerKind.Hold"/>.</summary>
    public int HoldMilliseconds { get; set; } = 450;

    public TriggerScope Scope { get; set; } = TriggerScope.FileManager;

    public TriggerGesture Clone() => (TriggerGesture)MemberwiseClone();

    /// <summary>Default gestures for the current OS.</summary>
    public static List<TriggerGesture> Defaults()
    {
        // On macOS Ctrl+click means right-click, and Option already means "copy" in Finder.
        var dragModifier = OperatingSystem.IsMacOS() ? ModifierKeys.Alt : ModifierKeys.Ctrl;
        return
        [
            new() { Kind = TriggerKind.Drag, Modifiers = dragModifier, Button = TriggerMouseButton.Left },
            new() { Kind = TriggerKind.KeyChord, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt, Button = TriggerMouseButton.None, Key = "Space" },
        ];
    }
}
