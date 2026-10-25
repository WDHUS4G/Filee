// The UI's Skia rendering and the idle memory trim at the same time. The crop canvas swaps its preview and disposes
// the old one (as the crop overlay does), the donut redraws, and each frame is captured and encoded as PNG, while
// another thread runs MemoryTrimmer.Trim (engine caches released, every generation collected, finalizers run, working
// set trimmed) with compacting collections in between. Native code left with a pointer into moved or freed memory
// crashes the test host. FILEE_STRESS_SECONDS runs it longer.

using System.Runtime;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class NativeMemoryRenderTests
{
    private static TimeSpan Duration =>
        TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("FILEE_STRESS_SECONDS"), out var s) && s > 0 ? s : 5);

    [AvaloniaFact]
    public async Task Rendering_and_saving_frames_survive_idle_trims_and_compacting_collections()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-native-render-").FullName;
        try
        {
            var pictures = new[] { MagickColors.Coral, MagickColors.Teal }.Select((color, i) =>
            {
                var path = Path.Combine(folder, $"picture{i}.png");
                using var image = new MagickImage(color, 640, 400);
                image.Write(path, MagickFormat.Png);
                return path;
            }).ToArray();

            var canvas = new CropCanvas { Width = 640, Height = 400 };
            var donut = new DonutMenu
            {
                Items = new[] { "PNG", "JPG", "WEBP", "PDF", "TIFF", "BMP" }.Select(l => new DonutItem { Id = l, Label = l }).ToList(),
            };
            var window = new Window { Content = new StackPanel { Children = { canvas, donut } }, SizeToContent = SizeToContent.WidthAndHeight };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            using var stop = new CancellationTokenSource(Duration);
            var trims = 0;
            var trimmer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    MemoryTrimmer.Trim(NullLogger.Instance);
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                    trims++;
                    Thread.Sleep(10);
                }
            }, TestContext.Current.CancellationToken);

            var frames = 0;
            while (!stop.IsCancellationRequested)
            {
                var (preview, _, _) = await CropService.LoadPreviewAsync(pictures[frames % pictures.Length]);
                var old = canvas.SourceImage;
                canvas.SourceImage = preview;
                old?.Dispose();
                donut.InvalidateVisual();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                // Disposed at the end: Avalonia encodes a snapshot that doesn't own the frame's pixels, so the frame
                // must stay alive until Save returns.
                using var frame = window.CaptureRenderedFrame()!;
                using var png = new MemoryStream();
                frame.Save(png, new PngBitmapEncoderOptions());
                Assert.True(png.Length > 1000);
                frames++;
            }
            await trimmer;
            window.Close();
            canvas.SourceImage?.Dispose();

            Assert.True(frames > 0);
            Assert.True(trims > 0);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
