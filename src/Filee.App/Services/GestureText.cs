// Human readable gesture text such as "Ctrl + Alt".

using Filee.Core.Settings;

namespace Filee.App.Services;

public static class GestureText
{
    public static string Modifiers(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Ctrl)) parts.Add(OperatingSystem.IsMacOS() ? "⌃" : "Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add(OperatingSystem.IsMacOS() ? "⌥ Option" : "Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Meta)) parts.Add(OperatingSystem.IsMacOS() ? "⌘" : "Win");
        return parts.Count == 0 ? "—" : string.Join(" + ", parts);
    }

    public static string Describe(TriggerGesture gesture)
    {
        var mods = Modifiers(gesture.Modifiers);
        return gesture.Kind == TriggerKind.KeyChord
            ? gesture.Modifiers == ModifierKeys.None ? gesture.Key ?? "—" : $"{mods} + {gesture.Key ?? "?"}"
            : $"{mods} + {gesture.Button}";
    }
}
