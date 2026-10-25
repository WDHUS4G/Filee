using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

public class PdfPageEditorTests
{
    private static string MakePdf(string folder, string name, params (double Width, int Rotation)[] pages)
    {
        var path = Path.Combine(folder, name);
        using var document = new PdfDocument();
        for (var index = 0; index < pages.Length; index++)
        {
            var (width, rotation) = pages[index];
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(500);
            page.Rotate = rotation;
            using var graphics = XGraphics.FromPdfPage(page);
            var accent = new XSolidBrush(XColor.FromArgb(
                (byte)(70 + index * 35), (byte)(100 + index * 20), (byte)(180 - index * 25)));
            graphics.DrawRectangle(accent, 24, 24, width - 48, 88);
            graphics.DrawRectangle(accent, 24, 148, width * .62, 24);
            graphics.DrawRectangle(accent, 24, 192, width * .78, 16);
            graphics.DrawRectangle(accent, 24, 228, width * .48, 16);
        }
        document.Save(path);
        return path;
    }

    [AvaloniaFact]
    public async Task Editor_adds_reorders_rotates_deletes_and_saves_pages()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-").FullName;
        try
        {
            var first = MakePdf(folder, "source.pdf", (200, 0), (300, 0));
            var added = MakePdf(folder, "added.pdf", (400, 90));
            var store = new UserDataStore(Path.Combine(folder, "data"));
            store.Load();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-pdf-pages");
            preset.Output.Location = OutputLocation.CustomFolder;
            preset.Output.CustomFolder = folder;
            using var vm = new PdfPageEditorViewModel([first], preset, AppHost.Get<ILocalizer>(), store);

            Assert.Empty(vm.Pages); // The constructor does not parse the PDF on the UI thread.
            await vm.LoadThumbnailsAsync();
            await vm.AddFilesAsync([added]);
            vm.SelectedPage = vm.Pages[2];
            vm.MoveUpCommand.Execute(null);
            vm.MoveUpCommand.Execute(null);
            vm.RotateLeftCommand.Execute(null);
            vm.SelectedPage = vm.Pages[2];
            vm.DeleteCommand.Execute(null);

            Assert.Equal([400d, 200d], vm.Pages.Select(p => p.Path == added ? 400d : 200d));
            Assert.Equal("Rotated −90°", vm.Pages[0].RotationText.Replace('-', '−'));
            Assert.True(await vm.SaveAsync());

            var output = Path.Combine(folder, "source_edited.pdf");
            using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert.Equal([400d, 200d], result.Pages.Cast<PdfPage>().Select(p => p.Width.Point));
            Assert.Equal([0, 0], result.Pages.Cast<PdfPage>().Select(p => p.Rotate));
            Assert.Contains(store.History, h => h.Outputs.Contains(output));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public async Task Editor_window_renders_previews_and_all_actions_without_clipping(string language)
    {
        TestServices.EnsureInitialized(language);
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-ui-").FullName;
        try
        {
            var source = MakePdf(folder, "Annual report.pdf", (595, 0), (595, 0), (842, 0), (595, 0));
            var store = new UserDataStore(Path.Combine(folder, "data"));
            store.Load();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-pdf-pages");
            var vm = new PdfPageEditorViewModel([source], preset, AppHost.Get<ILocalizer>(), store)
            {
                SelectedPage = null,
            };
            var window = new PdfPageEditorWindow { DataContext = vm };
            window.Show();

            await vm.LoadThumbnailsAsync();
            vm.SelectedPage = vm.Pages[1];
            vm.RotateRightCommand.Execute(null);
            for (var i = 0; i < 100 && !vm.HasSelectedPreview; i++)
            {
                RenderTests.Pump();
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            RenderTests.Pump();
            RenderTests.Save(window, $"pdf-page-editor-{language}.png");

            Assert.True(window.PageOrder.Bounds.Width > 220);
            Assert.Equal(4, window.PageOrder.ItemsView.Count);
            Assert.All(vm.Pages, page => Assert.True(page.HasThumbnail));
            Assert.True(vm.HasSelectedPreview);
            window.Close();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
