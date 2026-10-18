// Ready-made rules for the names of converted files (Watch folders → More options → Rename converted files → Edit
// rules). Each one adds a rule the user can change afterwards.

using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels.Pages;

/// <param name="TitleKey">Localization key of what it does.</param>
/// <param name="Step">The rule it adds.</param>
/// <param name="Example">A name it changes, shown with the result.</param>
public sealed record RenameRecipe(string TitleKey, RenameStep Step, string Example);

public static class RenameRecipes
{
    public static IReadOnlyList<RenameRecipe> All { get; } =
    [
        new("watch.recipe.camera_to_photo", new RenameStep { Kind = RenameKind.Replace, Find = "IMG_", Replace = "Photo_" }, "IMG_0412"),
        new("watch.recipe.drop_camera_prefix", new RenameStep { Kind = RenameKind.Remove, Find = "IMG_" }, "IMG_0412"),
        new("watch.recipe.spaces", new RenameStep { Kind = RenameKind.SpacesToUnderscores }, "trip to busan"),
        new("watch.recipe.copy_number", new RenameStep { Kind = RenameKind.RemoveCopyNumber }, "report (2)"),
        new("watch.recipe.brackets", new RenameStep { Kind = RenameKind.RemoveBrackets }, "song [remix] (live)"),
        new("watch.recipe.leading_number", new RenameStep { Kind = RenameKind.RemoveLeadingNumber }, "01 - intro"),
        new("watch.recipe.symbols", new RenameStep { Kind = RenameKind.RemoveSymbols }, "invoice#12@acme!"),
        new("watch.recipe.lowercase", new RenameStep { Kind = RenameKind.Lowercase }, "Report_FINAL"),
        new("watch.recipe.prefix", new RenameStep { Kind = RenameKind.Prefix, Replace = "new_" }, "report"),
        new("watch.recipe.suffix", new RenameStep { Kind = RenameKind.Suffix, Replace = "_final" }, "report"),
        new("watch.recipe.date_dashes", new RenameStep { Kind = RenameKind.Regex, Find = @"(\d{4})(\d{2})(\d{2})", Replace = "$1-$2-$3" }, "scan_20261011"),
    ];

    /// <summary>A name pattern with its parts in words: <c>{name}_{date}</c> → "[Original name]_[Date]".</summary>
    public static string Friendly(ILocalizer loc, string pattern)
    {
        foreach (var token in new[] { "name", "ext", "date", "time", "index", "preset" })
            pattern = pattern.Replace("{" + token + "}", $"[{loc[$"watch.token.{token}"]}]", StringComparison.Ordinal);
        return pattern;
    }

    /// <summary>A rule in one line for the watch folder card: "Replace text: IMG_ → Photo_", "Spaces to _".</summary>
    public static string Describe(ILocalizer loc, RenameStep step)
    {
        var kind = loc[$"watch.kind.{step.Kind}"];
        return step.Kind switch
        {
            RenameKind.Replace or RenameKind.Regex => $"{kind}: {step.Find} → {(step.Replace.Length == 0 ? loc["watch.recipe_removes"] : step.Replace)}",
            RenameKind.Remove => $"{kind}: {step.Find}",
            RenameKind.Prefix or RenameKind.Suffix => $"{kind}: {step.Replace}",
            _ => kind,
        };
    }
}
