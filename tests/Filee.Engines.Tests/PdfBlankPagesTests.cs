using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using Filee.Engines.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class PdfBlankPagesTests
{
    [Fact]
    public void Removes_only_pages_without_any_painting_operation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"filee-blank-pages-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = new PdfDocument())
            {
                document.AddPage(); // removed
                var line = document.AddPage();
                using (var graphics = XGraphics.FromPdfPage(line))
                    graphics.DrawLine(XPens.Black, 10, 10, 100, 100);
                document.AddPage(); // removed
                var white = document.AddPage();
                using (var graphics = XGraphics.FromPdfPage(white))
                    graphics.DrawRectangle(XBrushes.White, 10, 10, 100, 100);
                document.Save(path);
            }

            Assert.Equal(2, PdfBlankPages.Remove(path));
            using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.Equal(2, result.PageCount);
            for (var i = 0; i < result.PageCount; i++)
                Assert.False(PdfBlankPages.IsBlank(result.Pages[i]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Keeps_a_wholly_blank_document()
    {
        var path = Path.Combine(Path.GetTempPath(), $"filee-blank-document-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = new PdfDocument())
            {
                document.AddPage();
                document.AddPage();
                document.Save(path);
            }

            Assert.Equal(0, PdfBlankPages.Remove(path));
            using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.Equal(2, result.PageCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Keeps_PdfA_spreadsheet_output_byte_for_byte()
    {
        var folder = Directory.CreateTempSubdirectory("filee-pdfa-pages-").FullName;
        var path = Path.Combine(folder, "archive.pdf");
        try
        {
            using (var document = new PdfDocument())
            {
                document.AddPage(); // would be removed from an ordinary Calc PDF
                var painted = document.AddPage();
                using var graphics = XGraphics.FromPdfPage(painted);
                graphics.DrawLine(XPens.Black, 10, 10, 100, 100);
                document.Save(path);
            }
            var before = File.ReadAllBytes(path);
            var preset = new Preset { TargetFormat = "pdf", Document = { PdfA = true } };
            var step = new ConversionStep(Path.Combine(folder, "sheet.xlsx"), "xlsx", "pdf", preset,
                new FixedOutput(path), folder);

            Assert.Equal(0, LibreOfficeConverter.RemoveEmptySpreadsheetPages(step, path));
            Assert.Equal(before, File.ReadAllBytes(path));
            using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.Equal(2, result.PageCount);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class FixedOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
