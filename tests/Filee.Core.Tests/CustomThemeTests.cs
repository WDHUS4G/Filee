// theme.json: reading, dropping what Filee cannot use, and leaving the user's file alone.

using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.Core.Tests;

public class CustomThemeTests
{
    [Fact]
    public void No_file_means_no_custom_theme()
    {
        using var dir = new TempDir();
        Assert.Null(CustomTheme.Load(dir.Path));
    }

    [Fact]
    public void Reads_both_variants_with_comments_and_any_name_casing()
    {
        using var dir = new TempDir();
        dir.File(CustomTheme.FileName, """
            {
              // Night
              "dark": { "page": "#101820", "TEXT": "#E8E4D8", "border": "#33FFFFFF" },
              "light": { "surfaceAlt": "#EEF1F4", },
            }
            """);

        var theme = CustomTheme.Load(dir.Path)!;

        Assert.Equal("#101820", theme.Dark["page"]);
        Assert.Equal("#E8E4D8", theme.Dark["text"]);
        Assert.Equal("#33FFFFFF", theme.Dark["border"]);
        Assert.Equal("#EEF1F4", Assert.Single(theme.Light).Value);
    }

    [Fact]
    public void Unknown_names_and_bad_colours_are_dropped()
    {
        using var dir = new TempDir();
        dir.File(CustomTheme.FileName, """
            { "dark": { "page": "#101820", "toolbarGlow": "#FFFFFF", "text": "white", "danger": "#F00" } }
            """);

        var theme = CustomTheme.Load(dir.Path)!;

        Assert.Equal("page", Assert.Single(theme.Dark).Key);
        Assert.Empty(theme.Light);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "dark": { "page": "red" } }""")]
    [InlineData("{}")]
    public void A_file_with_nothing_usable_keeps_the_built_in_colours_and_is_not_touched(string content)
    {
        using var dir = new TempDir();
        var path = dir.File(CustomTheme.FileName, content);

        Assert.Null(CustomTheme.Load(dir.Path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("""{ "Dark": { "page": "#101820" } }""")]
    [InlineData("{}")]
    [InlineData("null")]
    public void A_file_with_no_usable_colour_says_so_in_the_log(string content)
    {
        using var dir = new TempDir();
        dir.File(CustomTheme.FileName, content);
        var log = new ListLogger();

        Assert.Null(CustomTheme.Load(dir.Path, log));
        Assert.Contains(log.Messages, m => m.Contains("sets no colours Filee can use"));
    }

    [Fact]
    public void A_file_over_the_size_cap_is_skipped_without_reading_it()
    {
        using var dir = new TempDir();
        var padding = new string(' ', (int)CustomTheme.MaxBytes);
        dir.File(CustomTheme.FileName, $$"""{ "dark": { "page": "#101820" } }{{padding}}""");
        var log = new ListLogger();

        Assert.Null(CustomTheme.Load(dir.Path, log));
        Assert.Contains(log.Messages, m => m.Contains("is larger than"));
    }

    [Fact]
    public void Every_name_is_unique()
    {
        Assert.Equal(CustomTheme.Keys.Count, CustomTheme.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

/// <summary>Collects formatted log messages.</summary>
internal sealed class ListLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
