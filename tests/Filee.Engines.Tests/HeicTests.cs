// Images → HEIC with FFmpeg's x265 and Filee's HEIF writer. Every file written is read back with ImageMagick's
// libheif, an independent decoder. The real conversions run only when Filee's FFmpeg is installed
// (`pwsh build/fetch-engines.ps1 -Only ffmpeg`).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Heic;
using Filee.Engines.Media;
using ImageMagick;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class HeicTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly bool FfmpegInstalled = FfmpegConverter.Locate() is not null;

    private static void SkipUnlessInstalled() =>
        Assert.SkipUnless(FfmpegInstalled, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");

    // ───────────────────────── Without FFmpeg ─────────────────────────

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("heic")] // re-encode, e.g. smaller
    [InlineData("raw")]
    public void Images_become_heic_in_one_ffmpeg_step(string from)
    {
        var route = fx.Catalog.CreatePlanner(["ffmpeg"]).Plan(from, "heic");
        Assert.NotNull(route);
        Assert.Equal(("ffmpeg", from, "heic"), Assert.Single(route.Steps) is var s ? (s.Converter.Id, s.From, s.To) : default);
    }

    [Fact]
    public void Pdf_pages_become_heic_through_png()
    {
        var route = fx.Catalog.CreatePlanner(["ffmpeg"]).Plan("pdf", "heic");
        Assert.NotNull(route);
        Assert.Equal([("pdfium", "pdf", "png"), ("ffmpeg", "png", "heic")], route.Steps.Select(s => (s.Converter.Id, s.From, s.To)));
    }

    [Theory]
    [InlineData(512, 512, 1, 1, 512, 512)]
    [InlineData(301, 201, 1, 1, 302, 202)] // odd sides: an even coded picture, cropped by a 1 × 1 grid
    [InlineData(4032, 3024, 8, 6, 512, 512)] // a 12 MP phone photo
    [InlineData(513, 100, 2, 1, 512, 512)]
    public void Pictures_are_laid_out_like_camera_photos(int width, int height, int columns, int rows, int tileWidth, int tileHeight) =>
        Assert.Equal((columns, rows, tileWidth, tileHeight), HeicWriter.Layout(width, height));

    [Fact]
    public void FFmpeg_pads_with_edge_pixels_and_cuts_tiles()
    {
        var args = HeicWriter.Arguments("in.png", "out.hevc", 1000, 700, 80);
        var filters = args[args.IndexOf("-vf") + 1];
        Assert.Equal("pad=1024:1024:0:0,fillborders=right=24:bottom=324:mode=smear,scale=out_color_matrix=bt601:out_range=pc,format=yuv420p,untile=2x2", filters);
        Assert.Equal("libx265", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("18", args[args.IndexOf("-crf") + 1]);
        // Every tile an IDR picture of the Main profile (keyint=1 would make x265 signal "Main Intra").
        Assert.Equal(["-force_key_frames", "expr:1", "-forced-idr", "1"], args.SkipWhile(a => a != "-force_key_frames").Take(4));
        Assert.Contains("open-gop=0", args[args.IndexOf("-x265-params") + 1]);
        Assert.DoesNotContain("keyint", args[args.IndexOf("-x265-params") + 1]);

        var small = HeicWriter.Arguments("in.png", "out.hevc", 400, 300, 80);
        Assert.Equal("scale=out_color_matrix=bt601:out_range=pc,format=yuv420p", small[small.IndexOf("-vf") + 1]);
        Assert.Equal((14, 30, 10), (HeicWriter.Crf(90), HeicWriter.Crf(50), HeicWriter.Crf(100)));
    }

    [Fact]
    public void Annex_b_streams_are_split_into_nal_units()
    {
        byte[] stream = [0, 0, 0, 1, 0x40, 0x01, 0x0C, 0, 0, 1, 0x42, 0x01, 0x01, 0, 0, 0, 1, 0x26, 0x01, 0xAF, 0x80];
        var units = HevcStream.Units(stream);
        Assert.Equal([[0x40, 0x01, 0x0C], [0x42, 0x01, 0x01], [0x26, 0x01, 0xAF, 0x80]], units);
        Assert.Throws<InvalidDataException>(() => HevcStream.Parse(stream[..10]));
        // 0x26 is a VCL unit of type 19 (IDR); a type 1 (trailing) picture cannot stand on its own as a tile.
        Assert.Contains("not an IDR", Assert.Throws<InvalidDataException>(() => HevcStream.Parse([0, 0, 1, 0x02, 0x01, 0x80])).Message);
    }

    [Fact]
    public async Task Tiles_are_main_profile_idr_pictures()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var png = EngineFixture.MakeImage(folder, "tiles.png", MagickFormat.Png, 1100, 600);
        var stream = Path.Combine(folder, "tiles.hevc");
        var result = await Infrastructure.ProcessRunner.RunAsync(FfmpegConverter.Locate()!.Value.Ffmpeg,
            HeicWriter.Arguments(png, stream, 1100, 600, 80), TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);

        var hevc = HevcStream.Parse(await File.ReadAllBytesAsync(stream, TestContext.Current.CancellationToken)); // all IDR, same parameter sets
        Assert.Equal(6, hevc.Pictures.Count); // 3 × 2 tiles
        Assert.Equal(8, hevc.BitDepth);
        Assert.Equal(1, hevc.Configuration[1] & 0x1F); // general_profile_idc 1: Main
    }

    [Fact]
    public void Exif_items_point_at_the_tiff_header()
    {
        byte[] withPrefix = [.. "Exif\0\0"u8, .. "MM\0*"u8, 0, 0, 0, 8];
        var payload = HeifFile.ExifPayload(withPrefix)!;
        Assert.Equal([0, 0, 0, 6], payload[..4]);
        Assert.Equal(withPrefix, payload[4..]);
        Assert.Equal([0, 0, 0, 0], HeifFile.ExifPayload([.. "II*\0"u8, 8, 0, 0, 0])![..4]);
        Assert.Null(HeifFile.ExifPayload([1, 2, 3, 4, 5]));
    }

    // ───────────────────────── Real conversions ─────────────────────────

    private async Task<string> ConvertAsync(string input, ImageOptions? image = null)
    {
        var preset = new Preset { TargetFormat = "heic", Image = image ?? new ImageOptions { Quality = 80 } };
        var job = await fx.ConvertAsync([input], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = Assert.Single(job.Outputs);
        Assert.Equal(".heic", Path.GetExtension(output));
        return output;
    }

    [Fact]
    public async Task A_small_odd_sized_png_keeps_its_size_and_colours()
    {
        SkipUnlessInstalled();
        // 301 × 201 gradient #7F77DD → #1D9E75 with a transparent 40 × 40 corner.
        var png = EngineFixture.MakeImage(fx.NewFolder(), "small.png", MagickFormat.Png, 301, 201);
        using var heic = new MagickImage(await ConvertAsync(png));

        Assert.Equal((301u, 201u), (heic.Width, heic.Height));
        Assert.False(heic.HasAlpha);
        using var pixels = heic.GetPixels();
        var middle = pixels.GetPixel(150, 100).ToColor()!;
        Assert.InRange(middle.R, 0x4E - 6, 0x4E + 6);
        Assert.InRange(middle.G, 0x8A - 6, 0x8A + 6);
        Assert.InRange(middle.B, 0xA9 - 6, 0xA9 + 6);
        var corner = pixels.GetPixel(10, 10).ToColor()!;
        Assert.True(corner.R > 245 && corner.G > 245 && corner.B > 245, "transparency is flattened onto white");
    }

    [Fact]
    public async Task A_rotated_photo_is_stored_upright_in_tiles_with_its_profile_and_exif()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var jpg = Path.Combine(folder, "phone.jpg");
        using (var photo = new MagickImage("gradient:#FF8800-#0044AA", 1300, 900))
        {
            // Stored sideways, shown upright through Exif orientation 6 (turn 90° clockwise), like a phone photo.
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            exif.SetValue(ExifTag.Make, "Filee test");
            photo.SetProfile(exif);
            photo.Orientation = OrientationType.RightTop; // ImageMagick writes this into the Exif orientation
            photo.SetProfile(ColorProfiles.AdobeRGB1998);
            photo.Quality = 95;
            photo.Write(jpg, MagickFormat.Jpeg);
        }

        var output = await ConvertAsync(jpg);
        using var heic = new MagickImage(output);
        Assert.Equal((900u, 1300u), (heic.Width, heic.Height)); // upright: 2 × 3 tiles of 512, cropped
        Assert.Equal(ColorProfiles.AdobeRGB1998.ToByteArray(), heic.GetColorProfile()?.ToByteArray());
        var exifBack = heic.GetExifProfile();
        Assert.NotNull(exifBack);
        Assert.Equal("Filee test", exifBack.GetValue(ExifTag.Make)?.Value);
        Assert.Equal((ushort)1, exifBack.GetValue(ExifTag.Orientation)?.Value);

        // The gradient runs from top to bottom in the stored pixels, so from right to left once upright. Tiles in
        // the wrong place would break it up.
        using var reference = new MagickImage(jpg);
        reference.AutoOrient();
        reference.Alpha(AlphaOption.Off);
        heic.Alpha(AlphaOption.Off);
        var rmse = heic.Compare(reference, ErrorMetric.RootMeanSquared);
        Assert.True(rmse < 0.01, $"RMSE {rmse}");
    }

    [Fact]
    public async Task Quality_and_metadata_options_apply()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var source = Path.Combine(folder, "detail.png");
        using (var noise = new MagickImage("plasma:", 800, 600))
        {
            noise.SetProfile(ColorProfiles.AdobeRGB1998);
            noise.Write(source, MagickFormat.Png);
        }

        var high = new FileInfo(await ConvertAsync(source, new ImageOptions { Quality = 90 })).Length;
        File.Move(Path.Combine(folder, "detail.heic"), Path.Combine(folder, "high.heic"));
        var stripped = await ConvertAsync(source, new ImageOptions { Quality = 40, KeepMetadata = false, KeepColorProfile = false });
        Assert.True(new FileInfo(stripped).Length < high / 2, $"{new FileInfo(stripped).Length} vs {high}");
        using var image = new MagickImage(stripped);
        Assert.Null(image.GetColorProfile()); // declared sRGB (nclx) instead
        Assert.Null(image.GetExifProfile());
    }
}
