// Rotate and flip presets: the image turns and mirrors the way the preset names say.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;
using ImageMagick.Drawing;

namespace Filee.Engines.Tests;

public class ImageToolTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A 4 × 2 white image with one red pixel in the top left corner, run through a built-in preset.</summary>
    [Theory]
    [InlineData("rotate-right", "a_right.png", 2, 4, 1, 0)]
    [InlineData("rotate-left", "a_left.png", 2, 4, 0, 3)]
    [InlineData("rotate-180", "a_180.png", 4, 2, 3, 1)]
    [InlineData("flip-horizontal", "a_mirrored.png", 4, 2, 3, 0)]
    [InlineData("flip-vertical", "a_flipped.png", 4, 2, 0, 1)]
    public async Task The_corner_pixel_moves_where_the_preset_says(string presetId, string name, int width, int height, int x, int y)
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "a.png");
        using (var image = new MagickImage(MagickColors.White, 4, 2))
        {
            new Drawables().FillColor(MagickColors.Red).Point(0, 0).Draw(image);
            image.Write(source, MagickFormat.Png);
        }

        var job = await fx.ConvertAsync([source], BuiltInData.CreatePresets().Single(p => p.Id == presetId));

        Assert.Equal(JobState.Completed, job.State);
        var output = job.Outputs.Single();
        Assert.Equal(name, Path.GetFileName(output));
        using var result = new MagickImage(output);
        Assert.Equal(((uint)width, (uint)height), (result.Width, result.Height));
        var red = result.GetPixels().GetPixel(x, y).ToColor()!;
        Assert.True(red.R > 200 && red.G < 60, $"{presetId}: ({x},{y}) is {red}");
    }

    private static Preset Tool(string id) => BuiltInData.CreatePresets().Single(p => p.Id == id);

    /// <summary>A 40 × 20 white image with a red 10 × 10 square in the top left corner.</summary>
    private static MagickImage Marked()
    {
        var image = new MagickImage(MagickColors.White, 40, 20);
        new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 9, 9).Draw(image);
        return image;
    }

    private static bool IsRed(IMagickImage<byte> image, int x, int y) =>
        image.GetPixels().GetPixel(x, y).ToColor() is { } c && c.R > 200 && c.G < 80 && c.B < 80;

    [Fact]
    public async Task A_phone_photo_turns_the_way_it_looks_and_says_so_in_its_orientation()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "phone.jpg");
        using (var image = Marked())
        {
            // Stored landscape, shown turned right (EXIF orientation 6): on screen 20 × 40, red at the top right.
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)OrientationType.RightTop);
            image.SetProfile(exif);
            image.Orientation = OrientationType.RightTop; // ImageMagick writes this into the profile
            image.Write(source, MagickFormat.Jpeg);
        }
        using (var written = new MagickImage(source))
            Assert.Equal(OrientationType.RightTop, written.Orientation); // the test photo is what it says

        var job = await fx.ConvertAsync([source], Tool("rotate-left"));

        Assert.Equal(JobState.Completed, job.State);
        using var result = new MagickImage(job.Outputs.Single());
        // Turned left from how it looked: back to 40 × 20 with red at the top left, and no tag turning it again.
        Assert.Equal((40u, 20u), (result.Width, result.Height));
        Assert.True(IsRed(result, 3, 3));
        Assert.True(result.Orientation is OrientationType.TopLeft or OrientationType.Undefined, $"{result.Orientation}");
    }

    /// <summary>
    /// EXIF data with orientation <paramref name="orientation"/> and a small JPEG preview, as cameras and phones write
    /// it: a TIFF header, IFD0 with the orientation, IFD1 pointing at the preview.
    /// </summary>
    private static ExifProfile ExifWithThumbnail(ushort orientation)
    {
        byte[] thumbnail;
        using (var small = new MagickImage(MagickColors.Blue, 8, 4))
            thumbnail = small.ToByteArray(MagickFormat.Jpeg);
        var data = new List<byte>();
        void U16(int v) => data.AddRange(BitConverter.GetBytes((ushort)v));
        void U32(int v) => data.AddRange(BitConverter.GetBytes((uint)v));
        data.AddRange("II"u8.ToArray());
        U16(42);
        U32(8);
        U16(1); // IFD0 at 8: one entry
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        U32(26); // IFD1 at 8 + 2 + 12 + 4
        U16(2); // IFD1: the preview's offset and length
        U16(0x0201); U16(4); U32(1); U32(26 + 2 + 24 + 4);
        U16(0x0202); U16(4); U32(1); U32(thumbnail.Length);
        U32(0);
        data.AddRange(thumbnail);
        return new ExifProfile([.. "Exif\0\0"u8.ToArray(), .. data]);
    }

    [Fact]
    public async Task A_turned_photo_drops_the_unturned_preview_in_its_EXIF_data()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "camera.jpg");
        using (var image = Marked())
        {
            image.SetProfile(ExifWithThumbnail(1));
            image.Write(source, MagickFormat.Jpeg);
        }
        using (var written = new MagickImage(source))
            Assert.True(written.GetExifProfile()!.ThumbnailLength > 0); // the test photo has a preview

        var job = await fx.ConvertAsync([source], Tool("rotate-right"));

        Assert.Equal(JobState.Completed, job.State);
        using var result = new MagickImage(job.Outputs.Single());
        var exif = result.GetExifProfile();
        Assert.NotNull(exif); // the metadata stays (KeepMetadata), only the stale preview goes
        Assert.Equal(0u, exif.ThumbnailLength);
    }

    [Fact]
    public async Task A_good_JPG_keeps_its_quality_when_it_turns()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "photo.jpg");
        using (var image = Marked())
        {
            image.Quality = 95;
            image.Write(source, MagickFormat.Jpeg);
        }

        var job = await fx.ConvertAsync([source], Tool("flip-horizontal"));

        using var result = new MagickImage(job.Outputs.Single());
        Assert.True(result.Quality >= 95, $"quality {result.Quality}"); // not 90, the default for converting to JPG
    }

    [Fact]
    public async Task Every_frame_of_an_animation_turns()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "anim.gif");
        using (var frames = new MagickImageCollection())
        {
            for (var i = 0; i < 2; i++)
            {
                var frame = Marked();
                frame.AnimationDelay = 20;
                frames.Add(frame);
            }
            frames.Write(source, MagickFormat.Gif);
        }

        var job = await fx.ConvertAsync([source], Tool("rotate-right"));

        Assert.Equal(JobState.Completed, job.State);
        using var result = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal(2, result.Count);
        Assert.All(result, frame =>
        {
            Assert.Equal((20u, 40u), (frame.Width, frame.Height));
            Assert.True(IsRed(frame, 16, 3)); // top left went to the top right
        });
    }

    [Fact]
    public async Task A_PDF_is_not_copied_as_if_rotated_it_fails_and_says_why()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "doc.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            pdf.AddPage();
            pdf.Save(source);
        }

        var job = await fx.ConvertAsync([source], Tool("rotate-left"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.cannot_turn", job.Files.Single().ErrorKey);
        Assert.False(File.Exists(Path.Combine(dir, "doc_left.pdf")));
    }

    [Fact]
    public async Task A_turned_jpeg_keeps_its_own_quality_instead_of_the_presets_higher_one()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "phone.jpg");
        using (var photo = new MagickImage(MagickColors.SkyBlue, 320, 240))
        {
            photo.Draw(new Drawables().FillColor(MagickColors.OrangeRed).Ellipse(160, 120, 90, 60, 0, 360));
            photo.Quality = 75;
            photo.Write(source, MagickFormat.Jpeg);
        }

        var job = await fx.ConvertAsync([source], Tool("rotate-left")); // the preset says quality 95

        Assert.Equal(JobState.Completed, job.State);
        using var turned = new MagickImage(job.Outputs.Single());
        Assert.Equal((240u, 320u), (turned.Width, turned.Height));
        Assert.InRange(turned.Quality, 70u, 75u);
    }

    [Fact]
    public async Task A_long_image_of_turned_pages_still_runs_top_to_bottom()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "slides.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            for (var i = 0; i < 2; i++)
            {
                var page = pdf.AddPage();
                page.Width = PdfSharp.Drawing.XUnit.FromPoint(200); // landscape slides
                page.Height = PdfSharp.Drawing.XUnit.FromPoint(100);
            }
            pdf.Save(source);
        }
        var preset = Tool("pdf-long-image");
        preset.Image.Rotation = ImageRotation.Right90;

        var job = await fx.ConvertAsync([source], preset);

        Assert.Equal(JobState.Completed, job.State);
        using var result = new MagickImage(job.Outputs.Single());
        // Each page turned (100 × 200), then stacked: four times as tall as wide, not the whole strip on its side.
        Assert.InRange(result.Height / (double)result.Width, 3.5, 4.5);
    }

    [Fact]
    public async Task An_image_into_a_PDF_is_turned_on_the_way()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "wide.png");
        using (var image = Marked())
            image.Write(source, MagickFormat.Png);
        var preset = Tool("rotate-left");
        preset.TargetFormat = "pdf";

        var job = await fx.ConvertAsync([source], preset);

        Assert.Equal(JobState.Completed, job.State);
        using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(job.Outputs.Single(), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.True(pdf.Pages[0].Height.Point > pdf.Pages[0].Width.Point); // landscape image, portrait page
    }
}
