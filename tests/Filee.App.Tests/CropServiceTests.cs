// Tests for CropService and the crop overlay: output resolving, conflict policies, quality, failures and skips.

using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

public class CropServiceTests
{
    private static string CreateTestImage(string folder, string name, int width = 100, int height = 80)
    {
        var path = Path.Combine(folder, name);
        using var image = new MagickImage(MagickColors.Green, (uint)width, (uint)height);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    [AvaloniaFact]
    public async Task CropFileAsync_resolves_output_path_and_crops_image()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-test-").FullName;
        try
        {
            var source = CreateTestImage(folder, "photo.png", 200, 100);
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");

            var result = await CropService.CropFileAsync(source, 10, 10, 50, 40, preset, store, loc);

            Assert.Equal(CropStatus.Success, result.Status);
            Assert.NotNull(result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.EndsWith("photo_cropped.png", result.OutputPath);

            using var cropped = new MagickImage(result.OutputPath);
            Assert.Equal((uint)50, cropped.Width);
            Assert.Equal((uint)40, cropped.Height);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Pdf_preview_and_crop_keep_every_page_as_a_PDF()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-crop-test-").FullName;
        try
        {
            var source = Path.Combine(folder, "pages.pdf");
            using (var document = new PdfDocument())
            {
                foreach (var rotation in new[] { 0, 90 })
                {
                    var page = document.AddPage();
                    page.Width = XUnit.FromPoint(600);
                    page.Height = XUnit.FromPoint(800);
                    page.Rotate = rotation;
                }
                document.Save(source);
            }
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-pdf-crop");
            var preview = await CropService.LoadPreviewAsync(source);

            var result = await CropService.CropFileAsync(source,
                preview.Width / 10, preview.Height / 5, preview.Width / 2, preview.Height * 3 / 5,
                preset, store, loc, originalWidth: preview.Width, originalHeight: preview.Height);

            Assert.Equal(CropStatus.Success, result.Status);
            Assert.EndsWith("pages_cropped.pdf", result.OutputPath);
            using var cropped = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
            Assert.Equal(2, cropped.PageCount);
            Assert.Equal([0, 90], cropped.Pages.Cast<PdfPage>().Select(p => p.Rotate));
            Assert.InRange(cropped.Pages[0].CropBox.Width, 299, 301);
            Assert.InRange(cropped.Pages[0].CropBox.Height, 479, 481);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task CropFileAsync_honors_conflict_policies()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-conflict-").FullName;
        try
        {
            var source = CreateTestImage(folder, "sample.png", 100, 100);
            var expectedOutput = Path.Combine(folder, "sample_cropped.png");
            File.WriteAllBytes(expectedOutput, [0x89, 0x50, 0x4E, 0x47]); // Existing file

            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();

            // 1. ConflictPolicy.Rename -> generates (2)
            var renamePreset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop").Clone();
            renamePreset.Output.Conflict = ConflictPolicy.Rename;
            var renameResult = await CropService.CropFileAsync(source, 0, 0, 50, 50, renamePreset, store, loc);

            Assert.Equal(CropStatus.Success, renameResult.Status);
            Assert.NotNull(renameResult.OutputPath);
            Assert.EndsWith("sample_cropped (2).png", renameResult.OutputPath);
            Assert.True(File.Exists(renameResult.OutputPath));

            // 2. ConflictPolicy.Skip -> returns SkippedExists and null path
            var skipPreset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop").Clone();
            skipPreset.Output.Conflict = ConflictPolicy.Skip;
            var skipResult = await CropService.CropFileAsync(source, 0, 0, 50, 50, skipPreset, store, loc);

            Assert.Equal(CropStatus.SkippedExists, skipResult.Status);
            Assert.Null(skipResult.OutputPath);
            Assert.Equal("toast.skipped", skipResult.ErrorKey);

            // 3. ConflictPolicy.Overwrite -> overwrites existing file
            var overwritePreset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop").Clone();
            overwritePreset.Output.Conflict = ConflictPolicy.Overwrite;
            var overwriteResult = await CropService.CropFileAsync(source, 0, 0, 30, 30, overwritePreset, store, loc);

            Assert.Equal(CropStatus.Success, overwriteResult.Status);
            Assert.Equal(expectedOutput, overwriteResult.OutputPath);
            using var overwritten = new MagickImage(expectedOutput);
            Assert.Equal((uint)30, overwritten.Width);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task CropFileAsync_keeps_the_sources_quality()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-quality-").FullName;
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");

            // A JPEG saved at 75 isn't saved again at a higher quality.
            var jpeg = Path.Combine(folder, "photo.jpg");
            using (var image = new MagickImage("plasma:", 120, 90))
            {
                image.Quality = 75;
                image.Write(jpeg, MagickFormat.Jpeg);
            }
            var jpegResult = await CropService.CropFileAsync(jpeg, 10, 10, 60, 40, preset, store, loc);
            Assert.Equal(CropStatus.Success, jpegResult.Status);
            using (var cropped = new MagickImage(jpegResult.OutputPath!))
                Assert.InRange(cropped.Quality, 73u, 77u);

            // A lossless WebP (VP8L) stays lossless instead of becoming lossy (VP8).
            var webp = Path.Combine(folder, "logo.webp");
            using (var image = new MagickImage("plasma:", 120, 90))
            {
                image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                image.Write(webp, MagickFormat.WebP);
            }
            Assert.True(IsLosslessWebp(webp));
            var webpResult = await CropService.CropFileAsync(webp, 10, 10, 60, 40, preset, store, loc);
            Assert.Equal(CropStatus.Success, webpResult.Status);
            Assert.True(IsLosslessWebp(webpResult.OutputPath!));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// <summary>True when the WebP's image chunk is VP8L (lossless), not VP8 (lossy).</summary>
    private static bool IsLosslessWebp(string path)
    {
        var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));
        return text.Contains("VP8L", StringComparison.Ordinal) && !text.Contains("VP8 ", StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_only_file_that_cant_be_cropped_keeps_the_overlay_open_with_the_reason_and_no_retry()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-animated-").FullName;
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var gif = Path.Combine(folder, "spinner.gif");
            using (var frames = new MagickImageCollection())
            {
                frames.Add(new MagickImage(MagickColors.Red, 40, 40));
                frames.Add(new MagickImage(MagickColors.Blue, 40, 40));
                frames.Write(gif, MagickFormat.Gif);
            }
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");

            using var vm = new CropOverlayViewModel([gif], preset, loc, store);
            var closed = false;
            vm.CloseRequested += () => closed = true;
            await WaitFor(() => vm.IsFinished);

            Assert.False(closed);
            Assert.Equal(["spinner.gif: " + loc["error.unsupported_source"]], vm.Problems);
            var entry = store.History.First(h => h.Sources.Contains(gif));
            Assert.Equal(JobState.Failed, entry.State);
            var failure = Assert.Single(entry.Failures);
            Assert.Equal("error.unsupported_source", failure.ErrorKey);
            Assert.Null(failure.ErrorDetail); // the English exception text stays in the log
            Assert.Null(entry.Preset); // Home offers no Retry: ConversionService doesn't run tools

            vm.CancelCommand.Execute(null);
            Assert.True(closed);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task A_file_skipped_because_the_output_exists_is_shown_but_isnt_a_failure()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-skip-").FullName;
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var source = CreateTestImage(folder, "scan.png");
            File.WriteAllBytes(Path.Combine(folder, "scan_cropped.png"), [0x89, 0x50, 0x4E, 0x47]);
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            preset.Output.Conflict = ConflictPolicy.Skip;

            using var vm = new CropOverlayViewModel([source], preset, loc, store);
            await WaitFor(() => !vm.IsBusy && vm.OriginalWidth > 0);
            await vm.CropAndSaveAsync();

            Assert.True(vm.IsFinished);
            Assert.Equal(["scan.png: " + loc["toast.skipped"]], vm.Problems);
            Assert.Equal(loc["toast.done"], vm.Summary);
            var entry = store.History.First(h => h.Sources.Contains(source));
            Assert.Equal(JobState.Completed, entry.State);
            Assert.Empty(entry.Failures);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(condition());
    }
}
