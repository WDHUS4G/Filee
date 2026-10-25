// Merge order (issue #268): the real chooser opened from the donut, then the pages of the merged PDF.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Settings;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class MergeOrderTests
{
    private static void Click(Window window, string name)
    {
        var button = window.FindControl<Button>(name)!;
        var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        RenderTests.Pump();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Donut_merge_waits_for_review_and_uses_the_chosen_order_or_cancels(bool cancel)
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-merge-order-").FullName;
        var store = AppHost.Get<UserDataStore>();
        var profile = store.Profiles.Single(p => p.Id == "pdf");
        var previous = profile.PresetIds;
        var preset = store.FindPreset("merge-pdf")!;
        var previousOutput = preset.Output;
        var conversions = AppHost.Get<ConversionService>();
        var before = conversions.Jobs.Select(j => j.Job.Id).ToHashSet();
        var radial = AppHost.Get<RadialController>();
        var windows = AppHost.Get<WindowService>();
        try
        {
            var inputs = new List<string>();
            foreach (var width in new[] { 200, 300, 400 })
            {
                // Identical names in different folders stay distinguishable in the chooser.
                var dir = Directory.CreateDirectory(Path.Combine(folder, width.ToString())).FullName;
                var path = Path.Combine(dir, "source.pdf");
                using var document = new PdfDocument();
                var page = document.AddPage();
                page.Width = PdfSharp.Drawing.XUnit.FromPoint(width);
                page.Height = PdfSharp.Drawing.XUnit.FromPoint(500);
                document.Save(path);
                inputs.Add(path);
            }
            profile.PresetIds = ["merge-pdf"];
            preset.Output = new() { Location = Filee.Core.Presets.OutputLocation.CustomFolder, CustomFolder = folder };
            radial.ShowForFiles(450, 400, inputs);
            var donut = radial.Window!.DonutControl;
            Assert.True(donut.HandleKey(Key.Right));
            Assert.True(donut.HandleKey(Key.Enter));
            RenderTests.Pump();

            var dialog = Assert.IsType<MergeOrderWindow>(windows.MergeOrder);
            // Sorted by name, then folder: 200, 300, 400.
            var vm = Assert.IsType<MergeOrderViewModel>(dialog.DataContext);
            Assert.False(radial.IsVisible);
            Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
            Assert.Equal(inputs, vm.OrderedFiles);
            Assert.False(dialog.FindControl<Button>("MoveUp")!.IsEffectivelyEnabled);
            Click(dialog, "MoveDown"); // 300, 200, 400
            Assert.Equal([inputs[1], inputs[0], inputs[2]], vm.OrderedFiles);
            dialog.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null); // 300, 400, 200
            RenderTests.Pump();
            Assert.Equal([inputs[1], inputs[2], inputs[0]], vm.OrderedFiles);
            Assert.False(dialog.FindControl<Button>("MoveDown")!.IsEffectivelyEnabled);
            Click(dialog, cancel ? "CancelMerge" : "StartMerge");

            if (cancel)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
                Assert.Empty(Directory.EnumerateFiles(folder, "*.pdf"));
            }
            else
            {
                JobViewModel? started = null;
                for (var i = 0; i < 200; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    started = conversions.Jobs.FirstOrDefault(j => !before.Contains(j.Job.Id));
                    if (started?.Job.State is JobState.Completed or JobState.Failed) break;
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }
                Assert.NotNull(started);
                Assert.Equal(JobState.Completed, started.Job.State);
                Assert.Equal([inputs[1], inputs[2], inputs[0]], started.Job.Files.Select(f => f.SourcePath));
                using var merged = PdfReader.Open(Assert.Single(started.Job.Outputs), PdfDocumentOpenMode.Import);
                Assert.Equal([300d, 400d, 200d], merged.Pages.Cast<PdfPage>().Select(p => p.Width.Point));
            }
            Assert.All(inputs, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            windows.MergeOrder?.Close();
            radial.Close(animate: false);
            profile.PresetIds = previous;
            preset.Output = previousOutput;
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ar")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    [InlineData("pl")]
    [InlineData("hi")]
    [InlineData("ru")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    [InlineData("fr")]
    public void Chooser_fits_long_paths_at_its_minimum_width(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new MergeOrderViewModel([
            @"C:\Documents\Quarterly reports\2026\This is a very long document name with spaces.pdf",
            @"C:\Documents\Another folder\This is a very long document name with spaces.pdf",
            @"C:\Documents\旅行の記録とملاحظات\中文-한국어-שלום.pdf"]);
        var window = new MergeOrderWindow { DataContext = vm, Width = 440, Height = 360 };
        try
        {
            window.Show();
            RenderTests.Pump();
            Rect Box(string name)
            {
                var control = window.FindControl<Control>(name)!;
                return new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window)!.Value);
            }
            var (merge, cancel, down) = (Box("StartMerge"), Box("CancelMerge"), Box("MoveDown"));
            // Arabic mirrors the row: the arrows go to the right, Merge to the left edge.
            if (language == "ar")
            {
                Assert.True(merge.Right < down.Left);
                Assert.True(cancel.Left >= merge.Right);
            }
            else
            {
                Assert.True(merge.Left > down.Right);
                Assert.True(cancel.Right <= merge.Left);
            }
            Assert.True(merge.Right <= window.Bounds.Width && merge.Left >= 0);
            Assert.True(window.FindControl<ListBox>("FileOrder")!.Bounds.Height > 80);
            RenderTests.Save(window, $"merge-order-{language}.png");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Files_start_in_name_order_counting_numbers_and_browser_files_show_no_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "filee-merge-names");
        string In(string folder, string name) => Path.Combine(root, folder, name);
        var browser = Path.Combine(Filee.Core.Web.WebDrop.DefaultWorkRoot, "abc", "drop", "1", "page 1.pdf");
        var vm = new MergeOrderViewModel([In("b", "page 10.pdf"), In("a", "page 2.pdf"), browser, In("a", "page 10.pdf")]);
        Assert.Equal([browser, In("a", "page 2.pdf"), In("a", "page 10.pdf"), In("b", "page 10.pdf")], vm.OrderedFiles);
        Assert.Equal("", vm.Files[0].Folder);
        Assert.Equal(Path.Combine(root, "a"), vm.Files[1].Folder);
        Assert.False(vm.MoveUpCommand.CanExecute(null));
        Assert.True(vm.MoveDownCommand.CanExecute(null));
    }
}
