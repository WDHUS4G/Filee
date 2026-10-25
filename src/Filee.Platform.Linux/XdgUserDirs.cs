// The user's folders as xdg-user-dirs names them (~/.config/user-dirs.dirs): a localized desktop has "다운로드" or
// "Téléchargements" instead of "Downloads". Pure parsing, so it can be tested anywhere.

namespace Filee.Platform.Linux;

public static class XdgUserDirs
{
    /// <summary>The XDG_DOWNLOAD_DIR line of a user-dirs.dirs file, with $HOME expanded; null when there is none.</summary>
    public static string? Download(string text, string home)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal))
                continue;
            var value = line["XDG_DOWNLOAD_DIR=".Length..].Trim().Trim('"');
            value = value.Replace("$HOME", home, StringComparison.Ordinal);
            // "$HOME/" alone means the home folder itself, which the spec says turns the folder off.
            return value.StartsWith('/') && value.TrimEnd('/') != home.TrimEnd('/') ? value : null;
        }
        return null;
    }
}
