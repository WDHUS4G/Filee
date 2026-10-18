// Images and vector graphics: the extra raster formats (JPEG XL, JPEG 2000, PSD, TGA, PPM, XCF, camera RAW,
// EMF/WMF), SVG → PDF/PNG, SVGZ, ICNS, Illustrator files and Ghostscript (EPS/PS, skipped when not installed).

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Icns;
using Filee.Engines.Magick;
using Filee.Engines.Vector;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class GraphicsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static bool GhostscriptInstalled => GhostscriptConverter.Locate() is not null;

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
        Assert.All(job.Outputs, o => Assert.True(new FileInfo(o).Length > 0, o));
    }

    private static IMagickColor<byte> Pixel(IMagickImage<byte> image, int x, int y) =>
        image.GetPixels().GetPixel(x, y).ToColor()!;

    private static string Write(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    // ───────────────────────── Raster formats ─────────────────────────

    [Theory]
    [InlineData("jxl", MagickFormat.Jxl, true)]
    [InlineData("jp2", MagickFormat.Jp2, true)]
    [InlineData("psd", MagickFormat.Psd, true)]
    [InlineData("psb", MagickFormat.Psb, true)]
    [InlineData("tga", MagickFormat.Tga, true)]
    [InlineData("ppm", MagickFormat.Ppm, false)]
    public async Task Png_converts_to_the_new_raster_formats_and_back(string target, MagickFormat expected, bool keepsAlpha)
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png);

        var job = await fx.ConvertAsync([source], new Preset { Name = target, TargetFormat = target, Image = { Quality = 100 } });

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("." + target, Path.GetExtension(output));
        using (var result = new MagickImage(output))
        {
            Assert.Equal(expected, result.Format);
            Assert.Equal((320u, 200u), (result.Width, result.Height));
            // The transparent corner survives where the format has alpha, else it becomes the white background.
            var corner = Pixel(result, 5, 5);
            if (keepsAlpha)
                Assert.Equal(0, corner.A);
            else
                Assert.True(corner.R > 250 && corner.G > 250 && corner.B > 250, corner.ToString());
        }

        // … and back to PNG through the same engine.
        var back = await fx.ConvertAsync([output], new Preset { TargetFormat = "png", Output = { FileNamePattern = "{name}_back" } });
        AssertDone(back);
        using var png = new MagickImage(back.Outputs.Single());
        Assert.Equal((320u, 200u), (png.Width, png.Height));
    }

    [Fact]
    public async Task Jpeg_xl_quality_comes_from_the_preset_and_100_is_lossless()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png, 640, 400);

        var low = await fx.ConvertAsync([source], new Preset { TargetFormat = "jxl", Image = { Quality = 40 }, Output = { FileNamePattern = "{name}_40" } });
        var high = await fx.ConvertAsync([source], new Preset { TargetFormat = "jxl", Image = { Quality = 95 }, Output = { FileNamePattern = "{name}_95" } });
        var lossless = await fx.ConvertAsync([source], new Preset { TargetFormat = "jxl", Image = { Quality = 100 }, Output = { FileNamePattern = "{name}_100" } });

        AssertDone(low);
        AssertDone(high);
        AssertDone(lossless);
        Assert.True(new FileInfo(low.Outputs.Single()).Length < new FileInfo(high.Outputs.Single()).Length);
        using var original = new MagickImage(source);
        using var exact = new MagickImage(lossless.Outputs.Single());
        Assert.Equal(0, original.Compare(exact, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task Layered_psd_becomes_one_image_of_the_composite()
    {
        var dir = fx.NewFolder();
        var path = Path.Combine(dir, "layers.psd");
        using (var layers = new MagickImageCollection())
        {
            var background = new MagickImage(MagickColors.Red, 120, 80);
            var box = new MagickImage(MagickColors.Blue, 40, 30) { Page = new MagickGeometry(60, 20, 40, 30) };
            // Photoshop stores the composite first, then the layers.
            using var composite = background.Clone();
            composite.Composite(box, 60, 20, CompositeOperator.Over);
            layers.Add(composite.Clone());
            layers.Add(background);
            layers.Add(box);
            layers.Write(path, MagickFormat.Psd);
        }
        using (var check = new MagickImageCollection(path))
            Assert.True(check.Count >= 3, "the test file should contain the composite and two layers");

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.Equal("layers.png", Path.GetFileName(job.Outputs.Single()));
        Assert.Equal((120u, 80u), (result.Width, result.Height));
        Assert.True(Pixel(result, 10, 10).R > 200, "background layer");
        Assert.True(Pixel(result, 80, 35).B > 200 && Pixel(result, 80, 35).R < 50, "the upper layer is composited at its offset");
    }

    [Fact]
    public async Task Gimp_layers_are_flattened_at_their_offsets_and_hidden_layers_are_left_out()
    {
        var dir = fx.NewFolder();
        var path = Path.Combine(dir, "drawing.xcf");
        File.WriteAllBytes(path, GraphicsBuilders.Xcf(96, 64,
            new GraphicsBuilders.XcfLayer("Hidden", 96, 64, 0, 0, 0, 0, 255, Visible: false),
            new GraphicsBuilders.XcfLayer("Box", 16, 16, 60, 8, 0, 200, 0),
            new GraphicsBuilders.XcfLayer("Background", 48, 64, 0, 0, 220, 30, 30)));

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.Equal((96u, 64u), (result.Width, result.Height));
        var background = Pixel(result, 10, 10);
        Assert.True(background.R > 200 && background.B < 50, "background: " + background);
        var box = Pixel(result, 66, 12);
        Assert.True(box.G > 180 && box.R < 50 && box.B < 50, "box: " + box);
        Assert.Equal(0, Pixel(result, 80, 50).A); // outside every visible layer: transparent, not the hidden blue
    }

    [Fact]
    public async Task Netpbm_keeps_its_kind_and_grayscale_becomes_pgm()
    {
        var dir = fx.NewFolder();
        var png = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png);
        var pgm = EngineFixture.MakeImage(dir, "scan.pgm", MagickFormat.Pgm);

        var gray = await fx.ConvertAsync([png], new Preset { TargetFormat = "ppm", Image = { Grayscale = true } });
        var half = await fx.ConvertAsync([pgm], new Preset
        {
            TargetFormat = BuiltInData.SameAsSource,
            Image = { Resize = ResizeMode.Percent, ResizePercent = 50 },
            Output = { FileNamePattern = "{name}_50" },
        });

        AssertDone(gray);
        AssertDone(half);
        Assert.Equal("a.pgm", Path.GetFileName(gray.Outputs.Single()));
        Assert.Equal("scan_50.pgm", Path.GetFileName(half.Outputs.Single()));
        using var result = new MagickImage(half.Outputs.Single());
        Assert.Equal(MagickFormat.Pgm, result.Format);
        Assert.Equal(160u, result.Width);
    }

    [Theory]
    [InlineData("photo.dng")]
    [InlineData("photo.raw")] // ImageMagick alone would read ".raw" as headerless pixels
    public async Task Camera_raw_is_decoded_by_LibRaw_and_turned_upright(string name)
    {
        var dir = fx.NewFolder();
        var path = Path.Combine(dir, name);
        // Orientation 6: the camera was turned; the picture must be rotated 90° clockwise for display.
        File.WriteAllBytes(path, GraphicsBuilders.LinearDng(60, 40, orientation: 6));

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "jpg" });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.Equal((40u, 60u), (result.Width, result.Height));
        // Left half red → after turning clockwise it is the top half.
        var top = Pixel(result, 20, 10);
        var bottom = Pixel(result, 20, 50);
        Assert.True(top.R > top.B + 60, "top should be red: " + top);
        Assert.True(bottom.B > bottom.R + 60, "bottom should be blue: " + bottom);
    }

    [Fact]
    public void Camera_raw_uses_the_camera_white_balance_and_srgb()
    {
        var settings = ImageReader.Settings("raw", new Preset());

        Assert.Equal(MagickFormat.Dng, settings.Format);
        Assert.Equal("true", settings.GetDefine(MagickFormat.Dng, "use-camera-wb"));
        Assert.Equal("false", settings.GetDefine(MagickFormat.Dng, "use-auto-wb"));
        Assert.Equal("1", settings.GetDefine(MagickFormat.Dng, "output-color"));
    }

    [Fact]
    public async Task Windows_metafiles_are_rendered_at_the_preset_dpi()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "EMF/WMF are read through GDI+ on Windows only.");
        var dir = fx.NewFolder();
        var emf = Path.Combine(dir, "chart.emf");
        var wmf = Path.Combine(dir, "clip.wmf");
        GraphicsBuilders.Emf(emf, 60, 30);
        GraphicsBuilders.Wmf(wmf, 60, 30);

        var job = await fx.ConvertAsync([emf, wmf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 200 } });
        var pdf = await fx.ConvertAsync([emf], new Preset { TargetFormat = "pdf" });

        AssertDone(job);
        AssertDone(pdf);
        foreach (var output in job.Outputs)
        {
            using var image = new MagickImage(output);
            // 60 mm at 200 dpi = 472 px (a pixel or two either way from GDI's rounding).
            Assert.InRange((int)image.Width, 460, 485);
            Assert.InRange((int)image.Height, 225, 245);
            var left = Pixel(image, (int)image.Width / 6, (int)image.Height / 2);
            var right = Pixel(image, (int)image.Width * 5 / 6, (int)image.Height / 2);
            Assert.True(left.R > 200 && left.B < 100, Path.GetFileName(output) + " left: " + left);
            Assert.True(right.G > 120 && right.R < 60, Path.GetFileName(output) + " right: " + right);
        }
        using var document = PdfReader.Open(pdf.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.InRange(document.Pages[0].Width.Millimeter, 58, 62);
    }

    // ───────────────────────── SVG ─────────────────────────

    [Fact]
    public async Task Svg_to_pdf_is_vector()
    {
        var dir = fx.NewFolder();
        var svg = Write(dir, "drawing.svg", GraphicsBuilders.Svg);

        var job = await fx.ConvertAsync([svg], new Preset { TargetFormat = "pdf" });

        AssertDone(job);
        using var document = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(1, document.PageCount);
        var page = document.Pages[0];
        // 400 × 200 CSS px = 300 × 150 pt.
        Assert.Equal(300, page.Width.Point, 1);
        Assert.Equal(150, page.Height.Point, 1);

        var content = new StringBuilder(Encoding.Latin1.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue));
        var images = new List<(int Width, int Height)>();
        var fonts = 0;
        CollectResources(page.Resources, content, images, ref fonts, 0);
        var text = content.ToString();
        Assert.Matches(@"\s(c|l|re)\s", text); // path operators: curves, lines, rectangles
        Assert.Matches(@"\sf\*?\s", text);    // fills
        // Text is drawn as glyph outlines: Skia would embed whole font files (13 MB for one line of Korean).
        Assert.DoesNotContain("BT", text);
        Assert.Equal(0, fonts);
        Assert.True(new FileInfo(job.Outputs.Single()).Length < 100_000, "the PDF should be small");
        Assert.DoesNotContain(images, i => i.Width > 64 || i.Height > 64); // no picture of the drawing
    }

    /// <summary>Collects content of form XObjects, image sizes and font count, recursively.</summary>
    private static void CollectResources(PdfDictionary? resources, StringBuilder content, List<(int, int)> images, ref int fonts, int depth)
    {
        if (resources is null || depth > 5)
            return;
        fonts += resources.Elements.GetDictionary("/Font")?.Elements.Count ?? 0;
        var xobjects = resources.Elements.GetDictionary("/XObject");
        if (xobjects is null)
            return;
        foreach (var key in xobjects.Elements.Keys)
        {
            if (xobjects.Elements.GetDictionary(key) is { } dictionary)
            {
                var subtype = dictionary.Elements.GetName("/Subtype");
                if (subtype == "/Image")
                {
                    images.Add((dictionary.Elements.GetInteger("/Width"), dictionary.Elements.GetInteger("/Height")));
                }
                else if (subtype == "/Form")
                {
                    content.Append(Encoding.Latin1.GetString(dictionary.Stream.UnfilteredValue));
                    CollectResources(dictionary.Elements.GetDictionary("/Resources"), content, images, ref fonts, depth + 1);
                }
            }
        }
    }

    [Fact]
    public async Task Svg_to_png_is_rendered_large_enough_and_keeps_transparency()
    {
        var dir = fx.NewFolder();
        var svg = Write(dir, "drawing.svg", GraphicsBuilders.Svg);

        var job = await fx.ConvertAsync([svg], new Preset { TargetFormat = "png" });
        var sized = await fx.ConvertAsync([svg], new Preset
        {
            TargetFormat = "webp",
            Image = { Resize = ResizeMode.LongEdge, LongEdge = 300, WebpLossless = true },
        });
        var jpg = await fx.ConvertAsync([svg], new Preset { TargetFormat = "jpg" });

        AssertDone(job);
        AssertDone(sized);
        AssertDone(jpg);
        using (var png = new MagickImage(job.Outputs.Single()))
        {
            // At least 1024 px on the long edge (the default 150 dpi would give only 625 px).
            Assert.Equal((1024u, 512u), (png.Width, png.Height));
            Assert.Equal(0, Pixel(png, 3, 3).A);
            var circle = Pixel(png, 512, 256);
            Assert.True(circle.R > 240 && circle.G < 20 && circle.A == 255, circle.ToString());
        }
        using (var webp = new MagickImage(sized.Outputs.Single()))
            Assert.Equal((300u, 150u), (webp.Width, webp.Height)); // rendered at exactly that size
        using (var flat = new MagickImage(jpg.Outputs.Single()))
            Assert.True(Pixel(flat, 3, 3).R > 245, "JPG gets the preset background");
    }

    [Fact]
    public async Task Svgz_round_trip()
    {
        var dir = fx.NewFolder();
        var svg = Write(dir, "logo.svg", GraphicsBuilders.Svg);

        var packed = await fx.ConvertAsync([svg], new Preset { TargetFormat = "svgz" });
        AssertDone(packed);
        var svgz = packed.Outputs.Single();
        Assert.Equal([0x1F, 0x8B], File.ReadAllBytes(svgz)[..2]);

        var unpacked = await fx.ConvertAsync([svgz], new Preset { TargetFormat = "svg", Output = { FileNamePattern = "{name}_plain" } });
        var rendered = await fx.ConvertAsync([svgz], new Preset { TargetFormat = "png", Image = { Resize = ResizeMode.LongEdge, LongEdge = 200 } });

        AssertDone(unpacked);
        AssertDone(rendered);
        Assert.Equal(File.ReadAllBytes(svg), File.ReadAllBytes(unpacked.Outputs.Single()));
        using var png = new MagickImage(rendered.Outputs.Single());
        Assert.Equal(200u, png.Width);
    }

    [Fact]
    public async Task Files_that_are_not_svg_fail_with_a_message()
    {
        var dir = fx.NewFolder();
        var fake = Write(dir, "fake.svg", "just text, no drawing");

        var job = await fx.ConvertAsync([fake], new Preset { TargetFormat = "svgz" });

        Assert.Equal(FileState.Failed, job.Files.Single().State);
        Assert.Contains("not an SVG", job.Files.Single().ErrorDetail);
    }

    // ───────────────────────── ICNS ─────────────────────────

    [Fact]
    public async Task Icns_holds_every_size_as_a_padded_square()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "app.png", MagickFormat.Png, 300, 200);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "icns" });

        AssertDone(job);
        var entries = IcnsFile.Parse(File.ReadAllBytes(job.Outputs.Single()));
        Assert.Equal(["TOC ", "icp4", "ic11", "icp5", "ic12", "ic07", "ic13", "ic08", "ic14", "ic09", "ic10"], entries.Select(e => e.Type));
        foreach (var (type, size) in IcnsFile.WrittenTypes)
        {
            using var icon = new MagickImage(entries.Single(e => e.Type == type).Data);
            Assert.Equal(MagickFormat.Png, icon.Format);
            Assert.Equal(((uint)size, (uint)size), (icon.Width, icon.Height));
        }
        using var large = new MagickImage(entries.Single(e => e.Type == "ic10").Data);
        Assert.Equal(0, Pixel(large, 512, 20).A);    // padding above the landscape picture
        Assert.Equal(255, Pixel(large, 512, 512).A); // the picture itself
    }

    [Fact]
    public async Task Icns_converts_back_from_its_largest_image()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "app.png", MagickFormat.Png, 256, 256);
        var icns = (await fx.ConvertAsync([source], new Preset { TargetFormat = "icns" })).Outputs.Single();

        var png = await fx.ConvertAsync([icns], new Preset { TargetFormat = "png", Output = { FileNamePattern = "{name}_icon" } });
        var jpg = await fx.ConvertAsync([icns], new Preset { TargetFormat = "jpg", Image = { Resize = ResizeMode.LongEdge, LongEdge = 100 } });

        AssertDone(png);
        AssertDone(jpg);
        using (var image = new MagickImage(png.Outputs.Single()))
            Assert.Equal((1024u, 1024u), (image.Width, image.Height));
        using (var image = new MagickImage(jpg.Outputs.Single()))
            Assert.Equal((100u, 100u), (image.Width, image.Height));
    }

    [Fact]
    public async Task Legacy_and_jpeg_2000_icns_entries_are_read()
    {
        var dir = fx.NewFolder();
        var mask = new byte[128 * 128];
        mask.AsSpan(0, 64 * 128).Fill(255); // top half opaque, bottom half transparent
        var legacy = Path.Combine(dir, "legacy.icns");
        File.WriteAllBytes(legacy, GraphicsBuilders.Icns(
            ("is32", GraphicsBuilders.LegacyRgb(16, 200, 0, 0)),
            ("s8mk", Enumerable.Repeat((byte)255, 256).ToArray()),
            ("it32", GraphicsBuilders.LegacyRgb(128, 0, 180, 40, it32: true)),
            ("t8mk", mask)));

        using var jp2Source = new MagickImage(MagickColors.Navy, 256, 256);
        var jpeg2000 = Path.Combine(dir, "jp2.icns");
        File.WriteAllBytes(jpeg2000, GraphicsBuilders.Icns(
            ("il32", GraphicsBuilders.LegacyRgb(32, 0, 0, 200)),
            ("ic08", jp2Source.ToByteArray(MagickFormat.Jp2))));

        var job = await fx.ConvertAsync([legacy, jpeg2000], new Preset { TargetFormat = "png" });

        AssertDone(job);
        using (var image = new MagickImage(job.Outputs.Single(o => Path.GetFileName(o) == "legacy.png")))
        {
            Assert.Equal((128u, 128u), (image.Width, image.Height)); // it32 is larger than is32
            var top = Pixel(image, 64, 32);
            Assert.True(top.G > 170 && top.R < 10 && top.A == 255, top.ToString());
            Assert.Equal(0, Pixel(image, 64, 100).A);
        }
        using (var image = new MagickImage(job.Outputs.Single(o => Path.GetFileName(o) == "jp2.png")))
        {
            Assert.Equal((256u, 256u), (image.Width, image.Height));
            Assert.True(Pixel(image, 10, 10).B > 100);
        }
    }

    [Fact]
    public void Icns_rle_decodes_literal_and_repeat_runs()
    {
        byte[] data = [.. Enumerable.Range(0, 300).Select(i => (byte)(i < 100 ? i : 7))];

        var decoded = IcnsFile.UnpackBits(GraphicsBuilders.PackBits(data), data.Length);

        Assert.Equal(data, decoded);
    }

    // ───────────────────────── Illustrator ─────────────────────────

    [Fact]
    public async Task Pdf_compatible_illustrator_files_convert_through_pdf()
    {
        var dir = fx.NewFolder();
        var svg = Write(dir, "art.svg", GraphicsBuilders.Svg);
        var pdf = (await fx.ConvertAsync([svg], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        var ai = Path.Combine(dir, "artwork.ai");
        File.Copy(pdf, ai);

        var toPdf = await fx.ConvertAsync([ai], new Preset { TargetFormat = "pdf" });
        var toPng = await fx.ConvertAsync([ai], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 96 } });

        AssertDone(toPdf);
        AssertDone(toPng);
        Assert.Equal(File.ReadAllBytes(ai), File.ReadAllBytes(toPdf.Outputs.Single()));
        using var png = new MagickImage(toPng.Outputs.Single());
        Assert.Equal((400u, 200u), (png.Width, png.Height));
    }

    [Fact]
    public async Task PostScript_illustrator_files_need_ghostscript()
    {
        var dir = fx.NewFolder();
        var ai = Write(dir, "old.ai", GraphicsBuilders.Eps);
        var garbage = Write(dir, "garbage.ai", "not an illustrator file");

        var job = await fx.ConvertAsync([ai, garbage], new Preset { TargetFormat = "pdf" });

        var old = job.Files.Single(f => f.SourcePath == ai);
        if (GhostscriptInstalled)
        {
            Assert.Equal(FileState.Done, old.State);
        }
        else
        {
            Assert.Equal(FileState.Failed, old.State);
            Assert.Contains("Ghostscript", old.ErrorDetail);
        }
        var bad = job.Files.Single(f => f.SourcePath == garbage);
        Assert.Equal(FileState.Failed, bad.State);
        Assert.Contains("not an Adobe Illustrator", bad.ErrorDetail);
    }

    // ───────────────────────── Ghostscript ─────────────────────────

    [Fact]
    public async Task Eps_converts_to_pdf_cropped_and_to_png()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = Path.Combine(fx.NewFolder(), "한글 폴더");
        Directory.CreateDirectory(dir);
        var eps = Write(dir, "그림 1.eps", GraphicsBuilders.Eps);

        var pdf = await fx.ConvertAsync([eps], new Preset { TargetFormat = "pdf" });
        var png = await fx.ConvertAsync([eps], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 144 } });

        AssertDone(pdf);
        AssertDone(png);
        using (var document = PdfReader.Open(pdf.Outputs.Single(), PdfDocumentOpenMode.Import))
        {
            Assert.Equal(200, document.Pages[0].Width.Point, 1);
            Assert.Equal(100, document.Pages[0].Height.Point, 1);
        }
        using var image = new MagickImage(png.Outputs.Single());
        Assert.Equal((400u, 200u), (image.Width, image.Height));
        var triangle = Pixel(image, 360, 170);
        Assert.True(triangle.B > 150 && triangle.R < 100, triangle.ToString());
        // Where the drawing leaves the page empty, the PNG stays transparent (#39), not white.
        Assert.True(image.HasAlpha);
        Assert.Equal(0, Pixel(image, 2, 2).A);
    }

    [Fact]
    public async Task Pdf_converts_to_eps_per_page_and_to_postscript()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 3)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var eps = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "eps", Pdf = { PageRange = "2-3" } });
        var ps = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "ps" });

        AssertDone(eps);
        AssertDone(ps);
        Assert.Equal(["pages_p2.eps", "pages_p3.eps"], eps.Outputs.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(eps.Outputs, o => Assert.StartsWith("%!PS-Adobe-3.0 EPSF-3.0", File.ReadAllText(o)));
        var postScript = File.ReadAllText(ps.Outputs.Single());
        Assert.StartsWith("%!PS-Adobe-3.0", postScript);
        Assert.Equal(3, postScript.Split("%%Page:").Length - 1);
    }

    [Fact]
    public async Task Svg_converts_to_eps_through_pdf()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = fx.NewFolder();
        var svg = Write(dir, "logo.svg", GraphicsBuilders.Svg);

        var job = await fx.ConvertAsync([svg], new Preset { TargetFormat = "eps" });

        AssertDone(job);
        var eps = File.ReadAllText(job.Outputs.Single());
        Assert.StartsWith("%!PS-Adobe-3.0 EPSF-3.0", eps);
        // eps2write crops to the drawn content, which lies inside the 300 × 150 pt page.
        var box = System.Text.RegularExpressions.Regex.Match(eps, @"%%BoundingBox: (\d+) (\d+) (\d+) (\d+)");
        Assert.True(box.Success, "no bounding box");
        var (left, bottom, right, top) = (int.Parse(box.Groups[1].Value), int.Parse(box.Groups[2].Value), int.Parse(box.Groups[3].Value), int.Parse(box.Groups[4].Value));
        Assert.InRange(left, 0, 30);
        Assert.InRange(bottom, 0, 40);
        Assert.InRange(right, 250, 300);
        Assert.InRange(top, 120, 150);
    }

    // ───────────────────────── Routes ─────────────────────────

    [Theory]
    [InlineData("svg", "png", "vector")]
    [InlineData("svg", "pdf", "vector")]
    [InlineData("ai", "png", "vector,pdfium")]
    [InlineData("eps", "png", "ghostscript")] // straight to PNG: keeps transparency (#39)
    [InlineData("svg", "eps", "vector,ghostscript")]
    [InlineData("raw", "jpg", "magick")]
    [InlineData("xcf", "psd", "magick")]
    [InlineData("png", "icns", "icns")]
    [InlineData("icns", "webp", "icns")]
    [InlineData("pdf", "icns", "pdfium,icns")]
    [InlineData("psd", "pdf", "pdfsharp")]
    public void Graphics_routes_use_the_expected_engines(string from, string to, string engines)
    {
        var route = fx.Catalog.CreatePlanner(["ghostscript"]).Plan(from, to);

        Assert.NotNull(route);
        Assert.Equal(engines, string.Join(',', route.Steps.Select(s => s.Converter.Id)));
    }

    [Fact]
    public void Illustrator_headers_are_recognised()
    {
        var dir = fx.NewFolder();
        var pdf = Path.Combine(dir, "a.ai");
        File.WriteAllBytes(pdf, [.. "%PDF-1.6\n"u8]);
        var ps = Write(dir, "b.ai", "%!PS-Adobe-3.0\n");
        var dos = Path.Combine(dir, "c.ai");
        File.WriteAllBytes(dos, [0xC5, 0xD0, 0xD3, 0xC6, 0, 0, 0, 0]);

        Assert.Equal(VectorConverter.AiKind.Pdf, VectorConverter.IllustratorKind(pdf));
        Assert.Equal(VectorConverter.AiKind.PostScript, VectorConverter.IllustratorKind(ps));
        Assert.Equal(VectorConverter.AiKind.PostScript, VectorConverter.IllustratorKind(dos));
    }

    [Fact]
    public void Svgz_detection_uses_the_gzip_signature()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write("<svg/>"u8);

        Assert.True(SvgRenderer.IsGzip(buffer.ToArray()));
        Assert.False(SvgRenderer.IsGzip("<svg/>"u8));
    }
}
