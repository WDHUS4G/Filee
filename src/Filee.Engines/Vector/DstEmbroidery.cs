// Tajima DST embroidery designs (issue #397) as SVG: the stitches as lines, a colour per thread, for the vector engine
// to render to PNG, JPG or PDF. A DST file is a 512-byte text header ("LA:" name, stitch count, extents) and 3-byte
// stitch records. Each record moves the needle by up to ±121 in X and Y (0.1 mm units): byte 0 holds the ±1 and ±9
// bits of both axes, byte 1 the ±3 and ±27 bits, byte 2 the ±81 bits and, in its top two bits, what the record is (a
// stitch, a jump, a colour change or a sequin-mode switch; 0xF3 there ends the design). Threads have no colours in DST,
// so the colour changes cycle through a fixed palette.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Vector;

internal static class DstEmbroidery
{
    private const int HeaderSize = 512;

    private static readonly string[] Palette =
        ["#1D3557", "#E63946", "#2A9D8F", "#F4A261", "#6A4C93", "#264653", "#E9C46A", "#8D99AE"];

    /// <summary>The stitched runs of each thread, in file order, as (x, y) in 0.1 mm with Y up.</summary>
    /// <exception cref="InvalidDataException">The file isn't a DST embroidery design (AutoCAD sheet sets use .dst too).</exception>
    public static List<List<List<(int X, int Y)>>> Read(byte[] data)
    {
        if (data.Length < HeaderSize || data[0] != 'L' || data[1] != 'A' || data[2] != ':')
            throw new InvalidDataException("The file isn't a Tajima DST embroidery design (it has no \"LA:\" header).");

        List<List<List<(int X, int Y)>>> threads = [[]];
        List<(int X, int Y)>? run = null;
        int x = 0, y = 0;
        for (var i = HeaderSize; i + 3 <= data.Length; i += 3)
        {
            int b0 = data[i], b1 = data[i + 1], b2 = data[i + 2];
            if ((b2 & 0xF3) == 0xF3)
                break; // end
            var dx = Step(b0, b1, b2, 0x01, 0x02, 0x04, 0x08, 0x01, 0x02, 0x04, 0x08, 0x04, 0x08);
            var dy = Step(b0, b1, b2, 0x80, 0x40, 0x20, 0x10, 0x80, 0x40, 0x20, 0x10, 0x20, 0x10);
            x += dx;
            y += dy;
            if ((b2 & 0xC3) == 0xC3)
            {
                threads.Add([]); // colour change: the next thread
                run = null;
            }
            else if ((b2 & 0x43) == 0x43 || (b2 & 0x83) == 0x83)
            {
                run = null; // a jump, or a sequin-mode switch or drop: no thread
            }
            else
            {
                if (run is null)
                {
                    // A stitch starts at the needle's last position.
                    run = [(x - dx, y - dy)];
                    threads[^1].Add(run);
                }
                run.Add((x, y));
            }
        }
        threads.RemoveAll(t => t.Count == 0);
        return threads;
    }

    /// <summary>One axis of a record: the ±1 and ±9 bits of byte 0, ±3 and ±27 of byte 1, ±81 of byte 2.</summary>
    private static int Step(int b0, int b1, int b2, int p1, int m1, int p9, int m9, int p3, int m3, int p27, int m27, int p81, int m81) =>
        ((b0 & p1) != 0 ? 1 : 0) - ((b0 & m1) != 0 ? 1 : 0) + ((b0 & p9) != 0 ? 9 : 0) - ((b0 & m9) != 0 ? 9 : 0)
        + ((b1 & p3) != 0 ? 3 : 0) - ((b1 & m3) != 0 ? 3 : 0) + ((b1 & p27) != 0 ? 27 : 0) - ((b1 & m27) != 0 ? 27 : 0)
        + ((b2 & p81) != 0 ? 81 : 0) - ((b2 & m81) != 0 ? 81 : 0);

    /// <summary>The design as SVG at its real size (millimetres), each thread a colour of <see cref="Palette"/>.</summary>
    public static string ToSvg(byte[] data)
    {
        var threads = Read(data);
        var points = threads.SelectMany(t => t).SelectMany(r => r).ToList();
        if (points.Count == 0)
            throw new InvalidDataException("The DST file has no stitches.");
        const int margin = 20; // 2 mm around the design
        int minX = points.Min(p => p.X) - margin, maxX = points.Max(p => p.X) + margin;
        int minY = points.Min(p => p.Y) - margin, maxY = points.Max(p => p.Y) + margin;
        int width = maxX - minX, height = maxY - minY;
        string Mm(int tenths) => (tenths / 10.0).ToString("0.#", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Mm(width)}mm\" height=\"{Mm(height)}mm\" viewBox=\"0 0 {width} {height}\">");
        svg.Append($"<rect width=\"{width}\" height=\"{height}\" fill=\"#FFFFFF\"/>");
        for (var t = 0; t < threads.Count; t++)
        {
            // A thread about 0.4 mm thick; Y flipped, DST counts it upwards.
            svg.Append($"<g fill=\"none\" stroke=\"{Palette[t % Palette.Length]}\" stroke-width=\"4\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
            foreach (var run in threads[t])
                svg.Append("<polyline points=\"").AppendJoin(' ', run.Select(p => $"{p.X - minX},{maxY - p.Y}")).Append("\"/>");
            svg.Append("</g>");
        }
        return svg.Append("</svg>").ToString();
    }
}
