// User-facing names of formats and format categories ("MP4 · Video", "폴더"), shared by the settings pages.

using Filee.Core.Formats;
using Filee.Core.Localization;

namespace Filee.App.ViewModels;

/// <summary>Localized labels for <see cref="FileFormat"/> and <see cref="FormatCategory"/>.</summary>
public static class FormatLabels
{
    /// <summary>"Image", "동영상", ... (keys <c>format.category.*</c>).</summary>
    public static string Category(ILocalizer loc, FormatCategory category) => loc[$"format.category.{category}"];

    /// <summary>The format's short name; the "folder" pseudo format (Extract) is translated.</summary>
    public static string Name(ILocalizer loc, FileFormat format) =>
        format.Id == FormatRegistry.Folder ? loc["format.folder"] : format.DisplayName;

    /// <summary>Name of the format with id <paramref name="formatId"/>, or null when it is unknown.</summary>
    public static string? NameOf(ILocalizer loc, string formatId) =>
        FormatRegistry.FindById(formatId) is { } format ? Name(loc, format) : null;

    /// <summary>"JPG · Image".</summary>
    public static string NameAndCategory(ILocalizer loc, FileFormat format) => $"{Name(loc, format)} · {Category(loc, format.Category)}";
}
