// Pixel-level check that slice labels sit on their slice's centre line.
// Regression: labels were centred on the measured text width while being laid out centred in a wider box,
// so short labels drifted right (reported with a screenshot of the donut editor).

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Controls;

namespace Filee.App.Tests;

public class DonutLabelPlacementTests
{
    private const double OuterRadius = 150;
    private const double HoleRatio = 0.42;

    [AvaloniaFact]
    public void Labels_are_centred_on_their_slices()
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu
        {
            OuterRadius = OuterRadius,
            HoleRatio = HoleRatio,
            Items =
            [
                new DonutItem { Id = "a", Label = "PNG" },
                new DonutItem { Id = "b", Label = "JPG" },
                new DonutItem { Id = "c", Label = "PDF" },
                new DonutItem { Id = "d", Label = "ICO" },
            ],
            SliceBrush = Brushes.White,
            CenterBrush = Brushes.White,
            Foreground = Brushes.Black,
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight, Background = Brushes.White };
        window.Show();
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var (pixels, width, height) = Read(window.CaptureRenderedFrame()!);
        var origin = donut.TranslatePoint(new Point(0, 0), window)!.Value;
        var center = new Point(origin.X + donut.Bounds.Width / 2, origin.Y + donut.Bounds.Height / 2);
        var labelRadius = (OuterRadius + OuterRadius * HoleRatio) / 2;

        for (var i = 0; i < 4; i++)
        {
            var anchor = DonutGeometry.PointAt(center, labelRadius, DonutGeometry.MidAngle(i, 4));
            var (cx, cy) = DarkCentroid(pixels, width, height, anchor, 34);
            Assert.True(Math.Abs(cx - anchor.X) < 3, $"slice {i}: text centre x {cx:0.0} vs slice centre {anchor.X:0.0}");
            Assert.True(Math.Abs(cy - anchor.Y) < 5, $"slice {i}: text centre y {cy:0.0} vs slice centre {anchor.Y:0.0}");
        }
        window.Close();
    }

    /// <summary>Average position of dark (text) pixels in a square around <paramref name="at"/>.</summary>
    private static (double X, double Y) DarkCentroid(byte[] bgra, int width, int height, Point at, int half)
    {
        double sx = 0, sy = 0;
        var n = 0;
        for (var y = Math.Max(0, (int)at.Y - half); y < Math.Min(height, (int)at.Y + half); y++)
        {
            for (var x = Math.Max(0, (int)at.X - half); x < Math.Min(width, (int)at.X + half); x++)
            {
                var o = (y * width + x) * 4;
                var luminance = 0.0722 * bgra[o] + 0.7152 * bgra[o + 1] + 0.2126 * bgra[o + 2];
                if (luminance < 110)
                {
                    sx += x;
                    sy += y;
                    n++;
                }
            }
        }
        Assert.True(n > 20, $"no label found near {at}");
        return (sx / n, sy / n);
    }

    private static (byte[] Pixels, int Width, int Height) Read(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var width = buffer.Size.Width;
        var height = buffer.Size.Height;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Marshal.Copy(buffer.Address + row * buffer.RowBytes, pixels, row * width * 4, width * 4);
        return (pixels, width, height);
    }
}
