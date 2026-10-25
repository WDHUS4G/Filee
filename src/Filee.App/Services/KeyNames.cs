// Key names as the global hook (SharpHook) and the settings use them: KeyCode without "Vc" ("Space", "F2", "Q").

using Avalonia.Input;

namespace Filee.App.Services;

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

    /// <summary>A key name as people read it: "Tab", "F2", "Q", "`" for "BackQuote".</summary>
    public static string Display(string hookName) => hookName switch
    {
        "BackQuote" => "`",
        "Minus" => "-",
        "Equals" => "=",
        "OpenBracket" => "[",
        "CloseBracket" => "]",
        "Backslash" => "\\",
        "Semicolon" => ";",
        "Quote" => "'",
        "Comma" => ",",
        "Period" => ".",
        "Slash" => "/",
        _ => hookName,
    };
}
