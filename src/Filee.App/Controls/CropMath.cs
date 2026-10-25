// The crop overlay's arithmetic without a window: the ratios of the aspect dial, keyboard nudges, the frame's size in
// pixels, where the size chip sits next to the frame and where the aspect dial opens.

using Avalonia;

namespace Filee.App.Controls;

public static class CropMath
{
    /// <summary>The aspect dial's choices, clockwise from the top.</summary>
    public static IReadOnlyList<string> AspectModes => Modes;

    private static readonly string[] Modes = ["free", "original", "1:1", "5:4", "4:3", "3:2", "16:9"];

    /// <summary>Where the mode sits on the dial, -1 for none.</summary>
    public static int AspectIndex(string mode) => Array.IndexOf(Modes, mode);

    /// <summary>A fixed ratio with a portrait and a landscape form (not free, the picture's own or square).</summary>
    public static bool CanSwap(string mode) => mode is "5:4" or "4:3" or "3:2" or "16:9";

    /// <summary>Width / height of the mode, 0 for free. Portrait turns a swappable ratio around (4:3 → 3:4).</summary>
    public static double Ratio(string mode, bool portrait, int originalWidth, int originalHeight)
    {
        var ratio = mode switch
        {
            "original" => originalWidth > 0 && originalHeight > 0 ? (double)originalWidth / originalHeight : 0,
            "1:1" => 1.0,
            "5:4" => 5.0 / 4.0, // upright: 4:5, Instagram's portrait size (#542)
            "4:3" => 4.0 / 3.0,
            "3:2" => 3.0 / 2.0,
            "16:9" => 16.0 / 9.0,
            _ => 0,
        };
        return portrait && CanSwap(mode) ? 1 / ratio : ratio;
    }

    /// <summary>The numbers shown for a mode: "4:3", or "3:4" in portrait. Free and original have none.</summary>
    public static string Numbers(string mode, bool portrait)
    {
        if (!CanSwap(mode) || !portrait)
            return mode;
        var parts = mode.Split(':');
        return $"{parts[1]}:{parts[0]}";
    }

    /// <summary>The frame in pixels of the picture (at least 1 × 1).</summary>
    public static PixelSize PixelSize(Rect normalized, int width, int height) =>
        new(Math.Max(1, (int)Math.Round(normalized.Width * width)), Math.Max(1, (int)Math.Round(normalized.Height * height)));

    /// <summary>
    /// Moves the frame by whole pixels of the picture (an arrow key: 1, with Shift 10). The frame lands on the pixel
    /// grid and stops at the picture's edges; its size stays.
    /// </summary>
    public static Rect Nudge(Rect normalized, int dx, int dy, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return normalized;
        var size = PixelSize(normalized, width, height);
        var x = Math.Clamp((int)Math.Round(normalized.X * width) + dx, 0, Math.Max(0, width - size.Width));
        var y = Math.Clamp((int)Math.Round(normalized.Y * height) + dy, 0, Math.Max(0, height - size.Height));
        return new Rect((double)x / width, (double)y / height, normalized.Width, normalized.Height);
    }

    /// <summary>How close (screen pixels) the frame's centre has to come to the picture's centre line to snap to it.</summary>
    public const double SnapDistance = 8;

    /// <summary>
    /// One side of a frame being dragged (issue #369): its start moved so its centre sits on the centre of
    /// <paramref name="min"/>…<paramref name="max"/> when it comes within <see cref="SnapDistance"/> of it. A frame as
    /// wide as the picture has nowhere to go and never counts as snapped.
    /// </summary>
    public static (double Start, bool Snapped) SnapToCentre(double start, double length, double min, double max)
    {
        var centre = (min + max) / 2;
        if (length >= max - min - 0.5 || Math.Abs(start + length / 2 - centre) > SnapDistance)
            return (start, false);
        return (centre - length / 2, true);
    }

    /// <summary>Space between the frame and the size chip.</summary>
    public const double ChipGap = 10;

    /// <summary>
    /// Top-left corner of the size chip: centred under the frame; inside its bottom edge when there's no room below
    /// <paramref name="area"/>; above the frame when the frame is too low for it as well. Never outside the area.
    /// </summary>
    public static Point SizeChipOrigin(Rect frame, Size chip, Rect area)
    {
        var x = Math.Clamp(frame.Center.X - chip.Width / 2, area.Left, Math.Max(area.Left, area.Right - chip.Width));
        double y;
        if (frame.Bottom + ChipGap + chip.Height <= area.Bottom)
            y = frame.Bottom + ChipGap;
        else if (frame.Height >= chip.Height + 3 * ChipGap)
            y = frame.Bottom - ChipGap * 1.6 - chip.Height; // clear of the handle on the bottom edge
        else
            y = frame.Top - ChipGap - chip.Height;
        return new Point(x, Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - chip.Height)));
    }

    /// <summary>Space kept between the aspect dial and the edges of the stage.</summary>
    public const double DialMargin = 8;

    /// <summary>Space between the aspect dial and the toolbar button it grows out of.</summary>
    public const double DialGap = 4;

    /// <summary>
    /// Top-left corner of the aspect dial (a square of <paramref name="side"/>) on a stage of <paramref name="stage"/>:
    /// centred on <paramref name="pointer"/> when A was pressed with the pointer over the stage, as the donut opens
    /// around the pointer; otherwise centred over its toolbar button, a little above <paramref name="buttonTop"/> (the
    /// middle of the button's top edge). Kept inside the stage.
    /// </summary>
    public static Point DialOrigin(Point? pointer, Point buttonTop, Size stage, double side)
    {
        var (x, y) = pointer is { } p
            ? (p.X - side / 2, p.Y - side / 2)
            : (buttonTop.X - side / 2, buttonTop.Y - side - DialGap);
        return new Point(
            Math.Clamp(x, DialMargin, Math.Max(DialMargin, stage.Width - side - DialMargin)),
            Math.Clamp(y, DialMargin, Math.Max(DialMargin, stage.Height - side - DialMargin)));
    }
}
