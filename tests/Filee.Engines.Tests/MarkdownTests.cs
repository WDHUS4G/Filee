// Markdown without optional engines (Markdig, in-process), the progress estimate of silent engines and the LibreOffice
// profile warm-up.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Text;
using ImageMagick;

namespace Filee.Engines.Tests;

public class MarkdownTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Sample = """
        ---
        title: 회의록
        ---

        # 주간 회의

        오늘 결정한 것: **배포**, *문서 정리*, `v1.0.1`.

        | 항목 | 담당 |
        |------|------|
        | 설치 | 민지 |

        ![그림](chart.png)

        - [x] 끝낸 일
        - [ ] 남은 일
        """;

    /// <summary>Only engines that ship with the app (or run in-process): no Pandoc or LibreOffice.</summary>
    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice"];

    private RoutePlanner BuiltInPlanner(params string[] assumeInstalled) =>
        new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }
            .CreatePlanner(assumeInstalled);

    private async Task<string> WriteSampleAsync(string dir)
    {
        using (var chart = new MagickImage(MagickColors.Teal, 160, 90))
            chart.Write(Path.Combine(dir, "chart.png"));
        var md = Path.Combine(dir, "회의록.md");
        await File.WriteAllTextAsync(md, Sample, TestContext.Current.CancellationToken);
        return md;
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public void Markdown_routes_need_no_optional_engine()
    {
        var planner = BuiltInPlanner();

        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "html")!.Steps).Converter.Id);
        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "txt")!.Steps).Converter.Id);
        Assert.Equal("hwpx-writer", Assert.Single(planner.Plan("md", "hwpx")!.Steps).Converter.Id);
        // PDF: the built-in HWPX writer, then rhwp (bundled with the installer).
        Assert.Equal(["hwpx-writer", "rhwp"], BuiltInPlanner("rhwp").Plan("md", "pdf")!.Steps.Select(s => s.Converter.Id));
    }

    [Fact]
    public void Built_in_markdown_wins_over_pandoc_for_html_and_text()
    {
        var planner = fx.Catalog.CreatePlanner(["pandoc"]);

        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "html")!.Steps).Converter.Id);
        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "txt")!.Steps).Converter.Id);
    }

    [Fact]
    public async Task Markdown_to_html_and_text()
    {
        var dir = fx.NewFolder();
        var md = await WriteSampleAsync(dir);

        var html = await File.ReadAllTextAsync(await ConvertAsync(md, "html"), TestContext.Current.CancellationToken);
        Assert.Contains("<h1", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<strong>배포</strong>", html);
        Assert.Contains("src=\"chart.png\"", html); // next to the Markdown file the relative path still works
        Assert.DoesNotContain("title: 회의록", html);

        var text = await File.ReadAllTextAsync(await ConvertAsync(md, "txt"), TestContext.Current.CancellationToken);
        Assert.Contains("주간 회의", text);
        Assert.Contains("배포", text);
        Assert.DoesNotContain("**", text);
    }

    [Fact]
    public async Task Html_written_elsewhere_points_images_at_the_original_files()
    {
        var dir = fx.NewFolder();
        var md = await WriteSampleAsync(dir);

        var html = MarkdownConverter.ToHtml(Sample, md, Path.Combine(fx.NewFolder(), "step.html"));

        Assert.Contains($"src=\"{new Uri(Path.Combine(dir, "chart.png")).AbsoluteUri}\"", html);
    }

    [Fact]
    public async Task Markdown_to_hwpx_keeps_structure()
    {
        var dir = fx.NewFolder();
        var hwpx = await ConvertAsync(await WriteSampleAsync(dir), "hwpx");

        HwpxAssert.ValidPackage(hwpx, expectImages: 1);
        var text = HwpxAssert.ReadBackText(hwpx);
        foreach (var expected in new[] { "주간 회의", "배포", "v1.0.1", "설치", "민지", "☑ 끝낸 일", "☐ 남은 일" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("title:", text);
        Assert.Single(HwpxAssert.Xml(hwpx).Descendants(HwpxAssert.Hp + "tbl"));
    }

    [Fact]
    public async Task Markdown_to_pdf_with_built_in_engines()
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");
        var dir = fx.NewFolder();
        var md = await WriteSampleAsync(dir);

        var pdf = await ConvertAsync(md, "pdf");

        Assert.True(new FileInfo(pdf).Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken))[..4]));
    }

    [Fact]
    public void Progress_estimate_rises_towards_the_bound_and_never_reaches_it()
    {
        var values = new[] { 0, 0.5, 1, 2, 4, 8, 30 }.Select(t => ProgressEstimate.Value(t, typical: 2, from: 0.1, to: 0.9)).ToList();

        Assert.Equal(0.1, values[0], 6);
        Assert.True(values.Zip(values.Skip(1)).All(p => p.Second > p.First));
        Assert.True(values[^1] < 0.9);
        Assert.InRange(ProgressEstimate.Value(2, typical: 2, from: 0.1, to: 0.9), 0.6, 0.62); // 1 - 1/e of the way
    }

    [Fact]
    public async Task Progress_estimate_reports_while_running_and_stops_when_disposed()
    {
        var reports = new List<double>();
        var sink = new SyncProgress(v => { lock (reports) reports.Add(v); });
        int Count()
        {
            lock (reports)
                return reports.Count;
        }

        using (ProgressEstimate.Start(sink, TimeSpan.FromSeconds(1)))
        {
            // Timer callbacks run late on a busy machine (CI runners): wait for two reports, up to 10 s.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Count() < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        // A callback that was already running when the estimate was disposed may still report once.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var count = Count();
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.True(count >= 2, $"only {count} reports");
        Assert.Equal(count, Count());
        lock (reports)
            Assert.All(reports, v => Assert.InRange(v, 0.1, 0.9));
    }

    [Fact]
    public async Task LibreOffice_warm_up_prepares_every_profile_once()
    {
        var env = new EngineEnvironment(Path.Combine(fx.NewFolder(), "data"));
        var libreOffice = new LibreOfficeConverter(env);
        Assert.SkipUnless(libreOffice.GetStatus().IsAvailable, "LibreOffice not found");

        Assert.Equal(2, await libreOffice.WarmUpAsync(TestContext.Current.CancellationToken));
        Assert.All(Directory.GetDirectories(Path.Combine(env.DataDirectory, "libreoffice-profiles")),
            profile => Assert.True(Directory.Exists(Path.Combine(profile, "user"))));
        Assert.Equal(0, await libreOffice.WarmUpAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
