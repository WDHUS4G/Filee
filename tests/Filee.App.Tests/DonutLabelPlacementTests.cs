// Pixel-level check that slice labels sit on their slice's centre line.
// Regression: labels were centred on the measured text width while being laid out centred in a wider box,
// so short labels drifted right (reported with a screenshot of the donut editor).

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;

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

        using var frame = window.CaptureRenderedFrame()!;
        var (pixels, width, height) = Read(frame);
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

    /// <summary>
    /// A word wider than its slice ("Grayscale" on a thin one) is drawn smaller rather than broken in the middle, and a
    /// Korean word stays on one line. Chinese, which breaks between any two characters, keeps its size.
    /// </summary>
    [AvaloniaFact]
    public void A_word_wider_than_its_slice_gets_smaller_instead_of_breaking()
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu();
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        Assert.Equal(13, donut.LabelSize("Half size", typeface, 60));
        Assert.InRange(donut.LabelSize("Grayscale", typeface, 40), 10, 12.9);
        Assert.Equal(10, donut.LabelSize("Supercalifragilistic", typeface, 30)); // no smaller than 10
        Assert.Equal(13, donut.LabelSize("去除背景去除背景", typeface, 40));
        Assert.Equal("ZIP 하\u2060나\u2060로", DonutMenu.KeepKoreanWords("ZIP 하나로"));
        Assert.Equal("Merge PDF", DonutMenu.KeepKoreanWords("Merge PDF"));
    }

    [AvaloniaTheory]
    [InlineData("pt-PT", "preset.half_size")]
    [InlineData("pt-BR", "preset.half_size")]
    [InlineData("pt-PT", "preset.trace_line_art")]
    [InlineData("pt-BR", "preset.trace_line_art")]
    [InlineData("pt-PT", "preset.trace_svg")]
    [InlineData("pt-BR", "preset.trace_svg")]
    [InlineData("pt-PT", "preset.extract_motion")]
    [InlineData("pt-BR", "preset.extract_motion")]
    public void Portuguese_image_actions_fit_the_two_line_slice_label(string language, string key)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<LocalizationService>();
        var preset = BuiltInData.CreatePresets().Single(p => p.NameKey == key);
        var label = loc.DonutName(preset);
        Assert.Equal(loc[key], loc.DisplayName(preset));
        preset.Name = "My custom action";
        Assert.Equal(preset.Name, loc.DonutName(preset));
        var typeface = new Typeface(SoftPalette.FontFamilyFor(language), FontStyle.Normal, FontWeight.SemiBold);
        var donut = new DonutMenu();
        // Compact image donuts leave roughly 50 px for an action, over at most two lines.
        using var text = new TextLayout(label, typeface, donut.LabelSize(label, typeface, 50), Brushes.Black,
            textWrapping: TextWrapping.Wrap, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: 50, maxLines: 2);
        Assert.All(text.TextLines, line => Assert.False(line.HasCollapsed, $"{language}: {label} is truncated"));
    }

    /// <summary>A caption wider than its slice wraps where the slice is deep enough, rather than ending in "Same for…".</summary>
    [AvaloniaFact]
    public void A_caption_wider_than_its_slice_takes_a_second_line_where_there_is_room()
    {
        TestServices.EnsureInitialized("en");
        var typeface = new Typeface(FontFamily.Default);
        var oneLine = DonutMenu.MakeCaption("PNG", typeface, null, 45, labelHeight: 17, maxHeight: 80).Height;

        Assert.InRange(DonutMenu.MakeCaption("Same format", typeface, null, 45, labelHeight: 17, maxHeight: 80).Height, oneLine * 1.8, oneLine * 2.2);
        Assert.Equal(oneLine, DonutMenu.MakeCaption("Same format", typeface, null, 45, labelHeight: 17, maxHeight: 40).Height);
        Assert.Equal(oneLine, DonutMenu.MakeCaption("Same format", typeface, null, 120, labelHeight: 17, maxHeight: 80).Height);
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
