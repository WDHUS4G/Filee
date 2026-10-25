using Filee.Core.Presets;
using Filee.Engines.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class PdfPageToolsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static string MakePdf(string folder, string name, params (double Width, double Height, int Rotation)[] pages)
    {
        var path = Path.Combine(folder, name);
        using var document = new PdfDocument();
        foreach (var (width, height, rotation) in pages)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);
            page.Rotate = rotation;
        }
        document.Save(path);
        return path;
    }

    private static string MakeStructuredPdf(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        using var document = new PdfDocument();
        var first = document.AddPage();
        document.AddPage();
        document.Info.Title = "Preserve this title";
        document.Info.Author = "Filee tests";
        document.Outlines.Add("First page", first);
        var field = new PdfDictionary(document);
        field.Elements.SetName("/FT", "/Tx");
        field.Elements.SetString("/T", "Customer");
        field.Elements.SetString("/V", "Alice");
        document.Internals.AddObject(field);
        var fields = new PdfArray(document);
        fields.Elements.Add(field.Reference!);
        var form = new PdfDictionary(document);
        form.Elements.SetString("/FileeFormMarker", "form stays");
        form.Elements["/Fields"] = fields;
        document.Internals.AddObject(form);
        document.Internals.Catalog.Elements["/AcroForm"] = form.Reference;
        var labelStyle = new PdfDictionary(document);
        labelStyle.Elements.SetName("/S", "/D");
        labelStyle.Elements.SetString("/P", "Page ");
        var numbers = new PdfArray(document);
        numbers.Elements.Add(new PdfInteger(0));
        numbers.Elements.Add(labelStyle);
        var labels = new PdfDictionary(document);
        labels.Elements.SetString("/FileeLabelMarker", "labels stay");
        labels.Elements["/Nums"] = numbers;
        document.Internals.AddObject(labels);
        document.Internals.Catalog.Elements["/PageLabels"] = labels.Reference;
        document.Save(path);
        return path;
    }

    private static void AssertStructurePreserved(string path)
    {
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal("Preserve this title", result.Info.Title);
        Assert.Equal("Filee tests", result.Info.Author);
        Assert.Equal("First page", Assert.Single(result.Outlines).Title);
        Assert.Equal("form stays", result.AcroForm.Elements.GetString("/FileeFormMarker"));
        Assert.Equal("Customer", Assert.Single(result.AcroForm.Fields.Names));
        Assert.Equal("labels stay", result.Internals.Catalog.Elements
            .GetDictionary("/PageLabels")!.Elements.GetString("/FileeLabelMarker"));
    }

    [Fact]
    public void Save_reorders_combines_and_rotates_pages_without_rasterising()
    {
        var folder = fx.NewFolder();
        var first = MakePdf(folder, "first.pdf", (200, 500, 0), (300, 500, 0));
        var second = MakePdf(folder, "second.pdf", (400, 500, 90));
        var output = Path.Combine(folder, "edited.pdf");

        PdfPageTools.Save([
            new(second, 0, -90),
            new(first, 1, 90),
            new(first, 0),
        ], output, TestContext.Current.CancellationToken);

        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(3, result.PageCount);
        Assert.Equal([400d, 300d, 200d], result.Pages.Cast<PdfPage>().Select(p => p.Width.Point));
        Assert.Equal([0, 90, 0], result.Pages.Cast<PdfPage>().Select(p => p.Rotate));
    }

    [Fact]
    public void Crop_applies_displayed_proportions_to_every_page_and_respects_rotation()
    {
        var folder = fx.NewFolder();
        var source = MakePdf(folder, "source.pdf", (600, 800, 0), (600, 800, 90));
        var output = Path.Combine(folder, "cropped.pdf");

        PdfPageTools.CropAll(source, output, x: .1, y: .2, width: .5, height: .6,
            TestContext.Current.CancellationToken);

        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, result.PageCount);
        AssertBox(result.Pages[0].CropBox, 60, 160, 360, 640);
        AssertBox(result.Pages[1].CropBox, 120, 80, 480, 480);
    }

    [Fact]
    public void Whole_document_crop_preserves_outlines_forms_page_labels_and_information()
    {
        var folder = fx.NewFolder();
        var source = MakeStructuredPdf(folder, "structured.pdf");
        var output = Path.Combine(folder, "cropped.pdf");

        PdfPageTools.CropAll(source, output, .1, .1, .8, .8, TestContext.Current.CancellationToken);

        AssertStructurePreserved(output);
    }

    [Fact]
    public void Whole_document_page_editor_rotation_preserves_document_structure()
    {
        var folder = fx.NewFolder();
        var source = MakeStructuredPdf(folder, "structured.pdf");
        var output = Path.Combine(folder, "rotated.pdf");

        PdfPageTools.Save([new(source, 0, 90), new(source, 1, -90)], output,
            TestContext.Current.CancellationToken);

        AssertStructurePreserved(output);
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal([90, 270], result.Pages.Cast<PdfPage>().Select(page => page.Rotate));
    }

    [Fact]
    public void Preview_source_keeps_one_cached_snapshot_for_all_pages()
    {
        var folder = fx.NewFolder();
        var source = MakePdf(folder, "preview.pdf", (200, 500, 0), (300, 500, 0));
        var preview = PdfPreviewSource.Open(source);
        File.Delete(source);

        Assert.Equal(2, preview.PageCount);
        Assert.NotEmpty(preview.Preview(0, 100).Png);
        Assert.NotEmpty(preview.Preview(1, 100).Png);
    }

    [Theory]
    [InlineData(PdfCompression.None)]
    [InlineData(PdfCompression.Ebook)]
    public async Task Pdf_rotation_preset_turns_all_pages_losslessly(PdfCompression compression)
    {
        var folder = fx.NewFolder();
        var source = MakePdf(folder, "source.pdf", (200, 500, 0), (300, 500, 90));
        var preset = new Preset
        {
            TargetFormat = "pdf",
            Output = { FileNamePattern = "{name}_right" },
            Pdf = { Rotation = PdfRotation.Right90, Compression = compression },
        };

        var job = await fx.ConvertAsync([source], preset);

        Assert.Equal(Filee.Core.Conversion.JobState.Completed, job.State);
        using var result = PdfReader.Open(Assert.Single(job.Outputs), PdfDocumentOpenMode.Import);
        Assert.Equal([90, 180], result.Pages.Cast<PdfPage>().Select(p => p.Rotate));
    }

    [Theory]
    [InlineData(PdfCompression.None)]
    [InlineData(PdfCompression.Ebook)]
    public async Task Pdf_rotation_preset_preserves_complete_document_structure(PdfCompression compression)
    {
        var folder = fx.NewFolder();
        var source = MakeStructuredPdf(folder, "structured.pdf");
        var preset = new Preset
        {
            TargetFormat = "pdf",
            Output = { FileNamePattern = "{name}_right" },
            Pdf = { Rotation = PdfRotation.Right90, Compression = compression },
        };

        var job = await fx.ConvertAsync([source], preset);

        Assert.Equal(Filee.Core.Conversion.JobState.Completed, job.State);
        var output = Assert.Single(job.Outputs);
        AssertStructurePreserved(output);
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.All(result.Pages.Cast<PdfPage>(), page => Assert.Equal(90, page.Rotate));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Crop_and_editor_rotation_accept_owner_restrictions_and_read_only_sources(bool ownerOnly, bool readOnly)
    {
        var folder = fx.NewFolder();
        var source = MakeSpecialSource(folder, ownerOnly, readOnly);
        var original = File.ReadAllBytes(source);
        try
        {
            var cropped = Path.Combine(folder, "cropped.pdf");
            PdfPageTools.CropAll(source, cropped, .1, .2, .5, .6, TestContext.Current.CancellationToken);
            using (var result = PdfReader.Open(cropped, PdfDocumentOpenMode.Import))
            {
                Assert.Equal(2, result.PageCount);
                AssertBox(result.Pages[0].CropBox, 60, 160, 360, 640);
            }
            Assert.False(File.GetAttributes(cropped).HasFlag(FileAttributes.ReadOnly));

            var rotated = Path.Combine(folder, "rotated.pdf");
            PdfPageTools.Save([new(source, 0, 90), new(source, 1, -90)], rotated,
                TestContext.Current.CancellationToken);
            using (var result = PdfReader.Open(rotated, PdfDocumentOpenMode.Import))
                Assert.Equal([90, 0], result.Pages.Cast<PdfPage>().Select(page => page.Rotate));
            Assert.False(File.GetAttributes(rotated).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Equal(readOnly, File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly));
        }
        finally { File.SetAttributes(source, File.GetAttributes(source) & ~FileAttributes.ReadOnly); }
    }

    [Theory]
    [InlineData(true, false, PdfCompression.None, PdfRotation.Right90)]
    [InlineData(true, false, PdfCompression.Ebook, PdfRotation.Right90)]
    [InlineData(false, true, PdfCompression.None, PdfRotation.Right90)]
    [InlineData(false, true, PdfCompression.Ebook, PdfRotation.Right90)]
    [InlineData(false, true, PdfCompression.None, PdfRotation.None)]
    [InlineData(false, true, PdfCompression.Ebook, PdfRotation.None)]
    public async Task Presets_accept_owner_restrictions_and_write_writable_outputs(bool ownerOnly, bool readOnly,
        PdfCompression compression, PdfRotation rotation)
    {
        var folder = fx.NewFolder();
        var source = MakeSpecialSource(folder, ownerOnly, readOnly);
        var original = File.ReadAllBytes(source);
        try
        {
            var job = await fx.ConvertAsync([source], new Preset
            {
                TargetFormat = "pdf",
                Output = { FileNamePattern = "{name}_edited" },
                Pdf = { Rotation = rotation, Compression = compression },
            });

            Assert.True(job.State == Filee.Core.Conversion.JobState.Completed,
                string.Join("; ", job.Files.Select(file => file.ErrorDetail)));
            var output = Assert.Single(job.Outputs);
            using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            var expected = rotation == PdfRotation.Right90 ? new[] { 90, 180 } : [0, 90];
            Assert.Equal(expected, result.Pages.Cast<PdfPage>().Select(page => page.Rotate));
            Assert.False(File.GetAttributes(output).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Equal(readOnly, File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly));
        }
        finally { File.SetAttributes(source, File.GetAttributes(source) & ~FileAttributes.ReadOnly); }
    }

    private static string MakeSpecialSource(string folder, bool ownerOnly, bool readOnly)
    {
        var source = MakePdf(folder, "source.pdf", (600, 800, 0), (600, 800, 90));
        if (ownerOnly)
        {
            using var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
            document.SecuritySettings.OwnerPassword = "owner-only";
            document.SecuritySettings.PermitModifyDocument = false;
            document.Save(source);
        }
        if (readOnly)
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
        return source;
    }

    private static void AssertBox(PdfRectangle actual, double x1, double y1, double x2, double y2)
    {
        Assert.Equal(x1, actual.X1, 6);
        Assert.Equal(y1, actual.Y1, 6);
        Assert.Equal(x2, actual.X2, 6);
        Assert.Equal(y2, actual.Y2, 6);
    }
}
