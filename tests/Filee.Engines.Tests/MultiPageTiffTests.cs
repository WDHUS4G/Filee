// "Put every file into one multi-page TIFF": the real ImageMagick merge, end to end through the job queue.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public sealed class MultiPageTiffTests(EngineFixture engines) : IClassFixture<EngineFixture>
{
    [Fact]
    public async Task Images_and_a_multi_page_tiff_become_one_tiff_with_every_page()
    {
        var folder = Directory.CreateTempSubdirectory("filee-tiff-").FullName;
        try
        {
            var red = Path.Combine(folder, "a.png");
            using (var image = new MagickImage(new MagickColor("#FF000080"), 40, 30)) // with transparency
                image.Write(red, MagickFormat.Png);
            var two = Path.Combine(folder, "b.tif");
            using (var pages = new MagickImageCollection())
            {
                pages.Add(new MagickImage(MagickColors.Green, 20, 20));
                pages.Add(new MagickImage(MagickColors.Blue, 20, 20));
                pages.Write(two, MagickFormat.Tiff);
            }

            var job = await engines.ConvertAsync([red, two],
                new Preset { Name = "TIFF", TargetFormat = "tiff", Image = { MultiPageTiff = true, TiffCompression = TiffCompression.Lzw } });

            Assert.Equal(JobState.Completed, job.State);
            var output = Assert.Single(job.Outputs);
            using var result = new MagickImageCollection(output);
            Assert.Equal(3, result.Count); // the PNG, then both pages of the TIFF
            Assert.All(result, page =>
            {
                Assert.Equal(CompressionMethod.LZW, page.Compression);
                Assert.False(page.HasAlpha); // flattened for old viewers
            });
            Assert.Equal(40u, result[0].Width);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
