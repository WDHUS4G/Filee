// Engine paths that hand memory to native code (PDFium, Skia, HarfBuzz, ImageMagick, WIC, unhwp) run on several
// threads while another thread keeps compacting the managed heap and a third drops the engines' caches the way the
// idle memory trim does. Native code that keeps a pointer into an unpinned managed buffer, a callback whose delegate
// was collected, or a cache release that frees something still in use crashes the test host or corrupts the output.
// FILEE_STRESS_SECONDS runs it longer (the default keeps CI time low).

using System.Runtime;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using ImageMagick;

namespace Filee.Engines.Tests;

public class NativeMemoryStressTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int Workers = 3;

    private static TimeSpan Duration =>
        TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("FILEE_STRESS_SECONDS"), out var s) && s > 0 ? s : 6);

    [Fact]
    public async Task Native_conversions_survive_a_compacting_collector_and_cache_releases()
    {
        var dir = fx.NewFolder();
        var routes = new List<(string Source, string Target)>
        {
            (Pdf(dir), "png"),                                  // PDFium into SKBitmap, Skia PNG encoder, ImageMagick
            (Write(dir, "drawing.svg", GraphicsBuilders.Svg), "pdf"), // Svg.Skia, HarfBuzz outlines, Skia PDF
            (Write(dir, "drawing2.svg", GraphicsBuilders.Svg), "png"),
            (CadBuilder.SaveDxf(CadBuilder.Sample(), dir, "도면.dxf"), "png"), // CAD typefaces (released by the trim)
            (ModelBuilders.Save(ModelBuilders.Cube(), dir, "cube", "glb"), "png"), // software renderer → SKImage
            (EngineFixture.MakeImage(dir, "photo.png", MagickFormat.Png), "webp"),
        };
        if (OperatingSystem.IsWindows())
        {
            var jxr = Path.Combine(dir, "photo.jxr");
            JpegXrBuilder.WriteBgr(jxr, 96, 64, (x, y) => ((byte)x, (byte)y, 128)); // WIC CopyPixels into a pinned array
            routes.Add((jxr, "png"));
        }
        if (fx.Catalog.StatusOf(fx.Converters.Single(c => c.Id == "unhwp")).IsAvailable)
            routes.Add((await fx.MakeHwpxAsync(fx.NewFolder()), "md"));

        // Every worker goes through every route at least once, however slow the machine; then until the time is up.
        using var stop = new CancellationTokenSource(Duration);
        var done = false;
        var compactions = 0;
        var compactor = Task.Run(() =>
        {
            // At least once: on a busy CI runner this task can start only after the workers have finished.
            do
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                compactions++;
                Thread.Sleep(20);
            }
            while (!Volatile.Read(ref done));
        }, TestContext.Current.CancellationToken);
        var releaser = Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
            {
                EngineCaches.Release();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(15);
            }
        }, TestContext.Current.CancellationToken);

        var runs = 0;
        var workers = Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            // Own copies, so the workers' outputs don't meet in one folder.
            var own = fx.NewFolder();
            var mine = routes.Select(r => (Source: CopyInto(own, r.Source), r.Target)).ToList();
            for (var i = 0; i < mine.Count || !stop.IsCancellationRequested; i++)
            {
                var (source, target) = mine[(worker + i) % mine.Count];
                var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target });
                var failures = string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
                Assert.True(job.State == JobState.Completed, $"{Path.GetFileName(source)} → {target}: {failures}");
                Assert.True(new FileInfo(job.Outputs.Single()).Length > 0);
                Interlocked.Increment(ref runs);
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            Volatile.Write(ref done, true);
            await Task.WhenAll(compactor, releaser);
        }
        Assert.True(runs >= Workers * routes.Count);
        Assert.True(compactions > 0);
    }

    private static string Pdf(string dir)
    {
        var pdf = new PdfBuilder().NewPage();
        var font = pdf.Font();
        for (var line = 0; line < 20; line++)
            pdf.Line($"Line {line}: PDFium renders this page into a Skia bitmap.", 12, font, 60, 80 + line * 18);
        pdf.Bar(60, 500, 300, 40, (30, 120, 200));
        return pdf.Save(Path.Combine(dir, "pages.pdf"));
    }

    private static string CopyInto(string folder, string file)
    {
        var copy = Path.Combine(folder, Path.GetFileName(file));
        File.Copy(file, copy);
        return copy;
    }

    private static string Write(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }
}
