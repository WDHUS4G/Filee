// HP-GL plot files (.plt, issue #410): pen moves read as lines, drawn as SVG and rendered through the vector engine.
// The packed HP-GL/2 sample (PE) was encoded independently of Filee's decoder, so the test is not circular.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Vector;
using ImageMagick;

namespace Filee.Engines.Tests;

public class HpglPlotTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>
    /// Pen 1: a square of 400 units (10 mm) from (400, 400), drawn relative; pen 2: a move of (800, 0), then a line
    /// of (-120, -120). Wrapped in PJL / PCL like a printer driver's plot, with pen 2 at 0.5 mm.
    /// </summary>
    private static byte[] DriverPlot() => Encoding.Latin1.GetBytes(
        "\u001B%-12345X@PJL ENTER LANGUAGE=HPGL2\r\n\u001BE\u001B%0BIN;PW0.5,2;PE" +
        ":Á<=_Ë_Ë_Ë¿¿_Ë`Ë¿¿`Ë:Ã<?Ø¿pÂpÂ" +
        ";\u001B%0A\u001BE\u001B%-12345X");

    [Fact]
    public void Plain_hpgl_pen_moves_become_lines()
    {
        var strokes = HpglPlot.Read(Encoding.ASCII.GetBytes("IN;SP1;PU0,0;PD400,0,400,400;PU;SP2;PA800,0;PD;PR0,400;SP0;"));

        Assert.Equal(2, strokes.Count);
        Assert.Equal([(0, 0), (400, 0), (400, 400)], strokes[0].Points);
        Assert.Equal("#000000", strokes[0].Color);
        Assert.Equal([(800, 0), (800, 400)], strokes[1].Points);
        Assert.Equal("#FF0000", strokes[1].Color);
    }

    [Theory]
    [InlineData("\u001B.(;\u001B.I81;;17:\u001B.N;19:IN;SP1;PU0,0;PD400,0,400,400;")] // device control first
    [InlineData("IP0,0,4000,4000;SC;SP1;PU0,0;PD400,0,400,400;")] // scaling points first
    [InlineData("\u0003IN;SP1;PU0,0;PD400,0,400,400;")] // a leading ETX
    [InlineData("CO\"made by x; PD 9,9\";IN;SP1;PU0,0;PD400,0,400,400;")] // a comment first, with a command in it
    [InlineData("in;sp1;pu0,0;pd400,0,400,400;")] // lower case
    public void Hpgl1_plots_starting_with_any_command_are_read(string plot)
    {
        var stroke = Assert.Single(HpglPlot.Read(Encoding.Latin1.GetBytes(plot)));

        Assert.Equal([(0, 0), (400, 0), (400, 400)], stroke.Points);
    }

    [Fact]
    public void Encoded_polylines_of_a_printer_driver_are_read()
    {
        var strokes = HpglPlot.Read(DriverPlot());

        Assert.Equal(2, strokes.Count);
        Assert.Equal([(400, 400), (800, 400), (800, 800), (400, 800), (400, 400)], strokes[0].Points);
        Assert.Equal([(1200, 400), (1080, 280)], strokes[1].Points);
        Assert.Equal(("#FF0000", 20.0), (strokes[1].Color, strokes[1].Width)); // 0.5 mm
    }

    [Fact]
    public void Arcs_circles_and_filled_polygons_are_drawn()
    {
        var strokes = HpglPlot.Read(Encoding.ASCII.GetBytes(
            "IN;SP1;PA1000,1000;CI200;PA0,0;PD;AA0,400,90;PU;PC3,0,128,255;SP3;PA2000,0;PM0;PD2400,0,2400,400;PU;PM2;FP;"));

        Assert.Equal(3, strokes.Count);
        Assert.All(strokes[0].Points, p => Assert.Equal(200, Math.Sqrt(Math.Pow(p.X - 1000, 2) + Math.Pow(p.Y - 1000, 2)), 6));
        Assert.Equal(400, strokes[1].Points[^1].X, 6); // a quarter turn around (0, 400) ends at (400, 400)
        Assert.Equal(400, strokes[1].Points[^1].Y, 6);
        Assert.True(strokes[2].Filled);
        Assert.Equal("#0080FF", strokes[2].Color);
    }

    [Fact]
    public async Task A_plot_becomes_an_svg_a_png_and_a_pdf()
    {
        var path = Path.Combine(fx.NewFolder(), "floor.plt");
        await File.WriteAllBytesAsync(path, DriverPlot(), TestContext.Current.CancellationToken);
        Assert.Equal("plt", FormatRegistry.Detect(path)?.Id);

        var svg = await fx.ConvertAsync([path], new Preset { TargetFormat = "svg" });
        Assert.True(svg.State == JobState.Completed, string.Join("; ", svg.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var text = await File.ReadAllTextAsync(svg.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("width=\"24mm\"", text); // 20 mm of drawing (400 → 1200) and 2 mm on each side

        var png = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });
        Assert.True(png.State == JobState.Completed, string.Join("; ", png.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using (var image = new MagickImage(png.Outputs.Single()))
            Assert.True(image.Histogram().Count > 2, "the lines are drawn");

        var pdf = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });
        Assert.Equal(JobState.Completed, pdf.State);
    }

    [Fact]
    public async Task A_plot_file_without_hpgl_fails_with_a_reason()
    {
        var path = Path.Combine(fx.NewFolder(), "postscript.plt");
        await File.WriteAllTextAsync(path, "%!PS-Adobe-3.0\nnewpath 0 0 moveto showpage\n", TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("HP-GL", job.Files[0].ErrorDetail);
    }
}
