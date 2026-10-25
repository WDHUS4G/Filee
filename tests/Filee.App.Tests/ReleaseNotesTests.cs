// The update notice says how much an update matters and what it changes, from a hidden block in the release notes.

using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class ReleaseNotesTests
{
    private const string Body = """
        ## What's Changed

        <!-- update
        level: required
        ko: 엑셀 페이지 나누기가 PDF에 반영돼요
        ko: 한글 수식이 제대로 나와요
        en: Excel page breaks carry over to PDF
        zh: Excel 分页符会保留到 PDF 中
        -->

        ### Fixed
        """;

    [Fact]
    public void The_hidden_block_gives_the_level_and_a_line_per_change_by_language()
    {
        var note = ReleaseNotes.Parse("1.10.3", Body);

        Assert.Equal(UpdateLevel.Required, note.Level);
        Assert.Equal(["엑셀 페이지 나누기가 PDF에 반영돼요", "한글 수식이 제대로 나와요"], note.In("ko"));
        Assert.Equal(["Excel 分页符会保留到 PDF 中"], note.In("zh-CN"));
        Assert.Equal(["Excel page breaks carry over to PDF"], note.In("ja")); // no Japanese: English
    }

    [Fact]
    public void Every_language_filee_ships_has_its_lines_and_unknown_ones_are_skipped()
    {
        var note = ReleaseNotes.Parse("1.12.0", """
            <!-- update
            en: Polish translation
            pl: Język polski
            pt-BR: Português do Brasil
            xx: from a newer Filee
            -->
            """);

        Assert.Equal(["Język polski"], note.In("pl"));
        Assert.Equal(["Português do Brasil"], note.In("pt-BR"));
        Assert.Equal(["Polish translation"], note.In("xx")); // not a Filee language: English
    }

    [Fact]
    public void Releases_without_the_block_are_recommended_and_say_nothing()
    {
        var note = ReleaseNotes.Parse("1.9.0", "### New\n\n- Something");

        Assert.Equal(UpdateLevel.Recommended, note.Level);
        Assert.Empty(note.In("ko"));
    }

    [Fact]
    public void Someone_a_few_versions_behind_gets_every_version_and_the_highest_level()
    {
        var notes = ReleaseNotes.Between(
        [
            ("v9.9.12", "<!-- update\nlevel: optional\n-->"),   // newer than the latest the check found: not yet
            ("v9.9.11", "<!-- update\nlevel: optional\nko: 새 기능\n-->"),
            ("v9.9.10", "<!-- update\nlevel: required\nko: 중요한 수정\n-->"),
            ("v9.9.9", "no block"),
            ("v1.0.0", "<!-- update\nlevel: required\n-->"),    // older than this version
        ], current: "9.9.9", latest: "9.9.11");

        Assert.Equal(["9.9.11", "9.9.10"], notes.Select(n => n.Version));
        Assert.Equal(UpdateLevel.Required, ReleaseNotes.Highest(notes));
        Assert.Equal(UpdateLevel.Optional, ReleaseNotes.Highest([notes[0]]));
        Assert.Equal(UpdateLevel.Recommended, ReleaseNotes.Highest([]));
    }

    [AvaloniaFact] // the check publishes its answer on the UI thread
    public async Task The_check_reads_the_notes_of_every_newer_release()
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new Releases());

        Assert.True(await updates.RefreshAsync(announce: false));

        Assert.Equal("9.9.11", updates.LatestVersion);
        Assert.Equal(["9.9.11", "9.9.10"], updates.Notes.Select(n => n.Version));
        Assert.True(updates.IsRequired);
        Assert.Contains("• 중요한 수정", UpdateTexts.Changes(updates, new KoreanOnly()));
    }

    private sealed class KoreanOnly : Filee.Core.Localization.ILocalizer
    {
        public string Language => "ko";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => key;
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    /// <summary>GitHub with 9.9.11 as the latest release, 9.9.10 required, a draft and an older release.</summary>
    private sealed class Releases : HttpMessageHandler
    {
        private static object Release(string tag, string body, bool draft = false) =>
            new { tag_name = tag, body, draft, prerelease = false, assets = Array.Empty<object>() };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object? answer = path.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? Release("v9.9.11", "<!-- update\nlevel: optional\nko: 새 기능\n-->")
                : path.EndsWith("/releases", StringComparison.Ordinal)
                    ? new[]
                    {
                        Release("v9.9.12", "<!-- update\nlevel: optional\n-->", draft: true),
                        Release("v9.9.11", "<!-- update\nlevel: optional\nko: 새 기능\n-->"),
                        Release("v9.9.10", "<!-- update\nlevel: required\nko: 중요한 수정\n-->"),
                        Release("v1.0.0", "old"),
                    }
                    : null;
            return Task.FromResult(answer is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json") });
        }
    }
}
