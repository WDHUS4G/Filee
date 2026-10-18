using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines;
using Filee.Engines.Infrastructure;
using ImageMagick;

namespace Filee.Engines.Tests;

/// <summary>Real engines + a temp folder with generated sample files.</summary>
public sealed class EngineFixture : IDisposable
{
    public EngineFixture()
    {
        Directory.CreateDirectory(Root);
        var env = new EngineEnvironment(Path.Combine(Root, "data"));
        Converters = EngineRegistry.CreateAll(env);
        Catalog = new ConverterCatalog(Converters) { Priority = new AppSettings().EnginePriority };
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "filee-engine-tests", Guid.NewGuid().ToString("N")[..8]);
    public IReadOnlyList<IConverter> Converters { get; }
    public ConverterCatalog Catalog { get; }

    /// <summary>Writes a small 한글 document with the built-in HWPX writer (no external engine needed).</summary>
    public async Task<string> MakeHwpxAsync(string folder)
    {
        var txt = Path.Combine(folder, "sample.txt");
        await File.WriteAllTextAsync(txt, "Filee 한글 문서 테스트\n\n두 번째 문단입니다. 文件转换 Hello");
        var job = await ConvertAsync([txt], new Preset { TargetFormat = "hwpx" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>Creates a fresh sub folder for one test.</summary>
    public string NewFolder()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes a 320×200 gradient image with a transparent corner in the given format.</summary>
    public static string MakeImage(string folder, string name, MagickFormat format, uint width = 320, uint height = 200)
    {
        using var image = new MagickImage("gradient:#7F77DD-#1D9E75", width, height);
        image.Alpha(AlphaOption.Set);
        // Punch a fully transparent 40×40 hole in the top-left corner (RGBA = 0).
        using (var pixels = image.GetPixels())
            pixels.SetArea(0, 0, 40, 40, new byte[40 * 40 * image.ChannelCount]);
        var path = Path.Combine(folder, name);
        image.Write(path, format);
        return path;
    }

    /// <summary>Writes a multi-page TIFF.</summary>
    public static string MakeMultiPageTiff(string folder, int pages)
    {
        using var collection = new MagickImageCollection();
        for (var i = 0; i < pages; i++)
            collection.Add(new MagickImage(i % 2 == 0 ? MagickColors.Coral : MagickColors.Teal, 120, 80));
        var path = Path.Combine(folder, "pages.tiff");
        collection.Write(path, MagickFormat.Tiff);
        return path;
    }

    public async Task<ConversionJob> ConvertAsync(IReadOnlyList<string> sources, Preset preset)
    {
        await using var queue = new JobQueue(Catalog, EngineRegistry.FindPdfMerger(Converters),
            combiner: EngineRegistry.FindFileCombiner(Converters), tiffMerger: EngineRegistry.FindTiffMerger(Converters));
        var job = new ConversionJob(sources, preset, preset.Name);
        await queue.RunAsync(job);
        return job;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
