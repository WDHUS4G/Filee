// The crop overlay's arithmetic: ratios and their portrait forms, keyboard nudges, snapping to the centre, the frame
// in pixels and where the size chip goes.

using Avalonia;
using Filee.App.Controls;

namespace Filee.App.Tests;

public class CropMathTests
{
    [Theory]
    [InlineData("free", false, 0.0)]
    [InlineData("1:1", true, 1.0)]
    [InlineData("5:4", false, 5.0 / 4.0)]
    [InlineData("5:4", true, 4.0 / 5.0)] // Instagram's portrait size
    [InlineData("4:3", false, 4.0 / 3.0)]
    [InlineData("4:3", true, 3.0 / 4.0)]
    [InlineData("3:2", true, 2.0 / 3.0)]
    [InlineData("16:9", false, 16.0 / 9.0)]
    [InlineData("16:9", true, 9.0 / 16.0)]
    [InlineData("original", true, 1.6)] // the picture's own ratio isn't turned around
    public void Ratios_and_their_portrait_forms(string mode, bool portrait, double expected)
    {
        Assert.Equal(expected, CropMath.Ratio(mode, portrait, 1600, 1000), 6);
    }

    [Theory]
    // A 100 px wide frame over a 0…1000 px picture: centred at 500 when its start is 450.
    [InlineData(450, 450, true)]   // already centred
    [InlineData(443, 450, true)]   // 7 px off: pulled in
    [InlineData(458, 450, true)]   // 8 px off: still pulled in
    [InlineData(459, 459, false)]  // 9 px off: left alone
    [InlineData(100, 100, false)]  // far away
    public void A_moved_frame_snaps_to_the_centre_line(double start, double expected, bool snapped)
    {
        Assert.Equal((expected, snapped), CropMath.SnapToCentre(start, 100, 0, 1000));
    }

    [Fact]
    public void A_frame_as_wide_as_the_picture_never_counts_as_snapped()
    {
        Assert.Equal((0.0, false), CropMath.SnapToCentre(0, 1000, 0, 1000));
    }

    [Fact]
    public void Only_fixed_ratios_that_arent_square_swap()
    {
        Assert.Equal(["5:4", "4:3", "3:2", "16:9"], CropMath.AspectModes.Where(CropMath.CanSwap));
        Assert.Equal("4:5", CropMath.Numbers("5:4", portrait: true));
        Assert.Equal("9:16", CropMath.Numbers("16:9", portrait: true));
        Assert.Equal("16:9", CropMath.Numbers("16:9", portrait: false));
        Assert.Equal("1:1", CropMath.Numbers("1:1", portrait: true));
        Assert.Equal(0, CropMath.Ratio("original", false, 0, 0));
        Assert.Equal(4, CropMath.AspectIndex("4:3"));
        Assert.Equal(-1, CropMath.AspectIndex("21:9"));
    }

    [Fact]
    public void Nudging_moves_by_whole_pixels_and_stops_at_the_edges()
    {
        var frame = new Rect(0.25, 0.25, 0.5, 0.5); // 400, 250, 800 × 500 of 1600 × 1000
        Assert.Equal(new Rect(401 / 1600.0, 250 / 1000.0, 0.5, 0.5), CropMath.Nudge(frame, 1, 0, 1600, 1000));
        Assert.Equal(new Rect(400 / 1600.0, 240 / 1000.0, 0.5, 0.5), CropMath.Nudge(frame, 0, -10, 1600, 1000));
        Assert.Equal(new Rect(0, 250 / 1000.0, 0.5, 0.5), CropMath.Nudge(frame, -1000, 0, 1600, 1000));
        Assert.Equal(new Rect(800 / 1600.0, 500 / 1000.0, 0.5, 0.5), CropMath.Nudge(frame, 5000, 5000, 1600, 1000));

        // Off the pixel grid (a frame dragged on screen), the first nudge lands on it.
        var loose = new Rect(100.4 / 1600, 0, 0.5, 0.5);
        Assert.Equal(101 / 1600.0, CropMath.Nudge(loose, 1, 0, 1600, 1000).X, 9);

        // A frame as large as the picture doesn't move; an unknown picture leaves it alone.
        Assert.Equal(new Rect(0, 0, 1, 1), CropMath.Nudge(new Rect(0, 0, 1, 1), 3, 3, 1600, 1000));
        Assert.Equal(frame, CropMath.Nudge(frame, 3, 3, 0, 0));
    }

    [Fact]
    public void The_frame_in_pixels_is_rounded_and_never_empty()
    {
        Assert.Equal(new PixelSize(800, 333), CropMath.PixelSize(new Rect(0, 0, 0.5, 0.333), 1600, 1000));
        Assert.Equal(new PixelSize(1, 1), CropMath.PixelSize(new Rect(0, 0, 0.0001, 0), 1600, 1000));
    }

    [Fact]
    public void The_size_chip_sits_under_the_frame_inside_it_or_above_it()
    {
        var area = new Rect(0, 0, 800, 600);
        var chip = new Size(100, 24);

        // Room below: centred under the frame.
        Assert.Equal(new Point(150, 310), CropMath.SizeChipOrigin(new Rect(100, 100, 200, 200), chip, area));

        // The frame reaches the bottom: inside along its bottom edge, clear of the edge's handle.
        var low = CropMath.SizeChipOrigin(new Rect(100, 300, 200, 300), chip, area);
        Assert.Equal(150, low.X);
        Assert.True(low.Y + chip.Height < 600 - CropMath.ChipGap && low.Y > 300);

        // A thin frame at the bottom: above it.
        Assert.Equal(new Point(150, 576 - CropMath.ChipGap - 24), CropMath.SizeChipOrigin(new Rect(100, 576, 200, 24), chip, area));

        // Near a side: kept inside the area.
        Assert.Equal(0, CropMath.SizeChipOrigin(new Rect(0, 100, 40, 40), chip, area).X);
        Assert.Equal(700, CropMath.SizeChipOrigin(new Rect(760, 100, 40, 40), chip, area).X);
    }

    [Fact]
    public void The_dial_opens_around_the_pointer_or_over_its_button_inside_the_stage()
    {
        var stage = new Size(1000, 700);
        const double side = 246;
        var button = new Point(400, 620);

        // With the pointer over the stage: centred on it.
        Assert.Equal(new Point(377, 177), CropMath.DialOrigin(new Point(500, 300), button, stage, side));

        // Near an edge or a corner: moved in until it fits, with a margin.
        Assert.Equal(new Point(CropMath.DialMargin, CropMath.DialMargin), CropMath.DialOrigin(new Point(20, 10), button, stage, side));
        Assert.Equal(new Point(1000 - side - CropMath.DialMargin, 700 - side - CropMath.DialMargin),
            CropMath.DialOrigin(new Point(990, 690), button, stage, side));

        // Without a pointer (the keyboard): centred over the button, just above it.
        Assert.Equal(new Point(277, 620 - side - CropMath.DialGap), CropMath.DialOrigin(null, button, stage, side));
        // A button near the side still gets the whole dial.
        Assert.Equal(CropMath.DialMargin, CropMath.DialOrigin(null, new Point(30, 620), stage, side).X);

        // A stage smaller than the dial: from its top left corner.
        Assert.Equal(new Point(CropMath.DialMargin, CropMath.DialMargin), CropMath.DialOrigin(new Point(100, 100), button, new Size(200, 200), side));
    }
}
