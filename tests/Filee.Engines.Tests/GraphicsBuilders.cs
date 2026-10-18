// Generates graphics test inputs that ImageMagick can't write: GIMP XCF files, linear DNG camera RAW files, ICNS
// entries in Apple's legacy RLE format, Windows metafiles (through GDI) and SVG / EPS documents.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Filee.Engines.Tests;

internal static class GraphicsBuilders
{
    // ───────────────────────── SVG and EPS ─────────────────────────

    /// <summary>A 400×200 drawing with a gradient, a Bézier path, a circle and text; transparent background.</summary>
    public const string Svg = """
        <?xml version="1.0" encoding="UTF-8"?>
        <svg xmlns="http://www.w3.org/2000/svg" width="400" height="200" viewBox="0 0 400 200">
          <defs>
            <linearGradient id="g" x1="0" y1="0" x2="1" y2="0">
              <stop offset="0" stop-color="#7F77DD"/>
              <stop offset="1" stop-color="#1D9E75"/>
            </linearGradient>
          </defs>
          <rect x="20" y="20" width="160" height="80" rx="12" fill="url(#g)"/>
          <path d="M 220 150 C 260 40, 320 40, 360 150 Z" fill="#E4572E" stroke="#222" stroke-width="3"/>
          <circle cx="200" cy="100" r="30" fill="#FF0000"/>
          <text x="20" y="170" font-family="Arial, sans-serif" font-size="28" fill="#222">Filee SVG 한글</text>
        </svg>
        """;

    /// <summary>A 200×100 EPS: a filled triangle and a line of text.</summary>
    public const string Eps = """
        %!PS-Adobe-3.0 EPSF-3.0
        %%BoundingBox: 0 0 200 100
        %%EndComments
        newpath 10 10 moveto 190 90 lineto 190 10 lineto closepath 0.2 0.5 0.8 setrgbcolor fill
        /Helvetica findfont 18 scalefont setfont 0 0 0 setrgbcolor 20 70 moveto (Filee EPS) show
        showpage
        %%EOF
        """;

    // ───────────────────────── GIMP XCF ─────────────────────────

    /// <summary>A solid-colour XCF layer.</summary>
    public sealed record XcfLayer(string Name, int Width, int Height, int X, int Y, byte R, byte G, byte B, byte A = 255, bool Visible = true);

    /// <summary>
    /// Writes an 8-bit RGB XCF (format version 0, RLE tiles) with the layers given top-most first, as GIMP stores them.
    /// </summary>
    public static byte[] Xcf(int width, int height, params XcfLayer[] layers)
    {
        var w = new BigEndianWriter();
        w.Bytes("gimp xcf file\0"u8);
        w.U32(width);
        w.U32(height);
        w.U32(0); // RGB
        w.U32(17); w.U32(1); w.Byte(1); // PROP_COMPRESSION = RLE
        w.U32(0); w.U32(0);             // PROP_END

        var layerTable = w.Position;
        foreach (var _ in layers)
            w.U32(0);
        w.U32(0); // end of layers
        w.U32(0); // no channels

        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            w.Patch(layerTable + 4 * i, w.Position);
            w.U32(layer.Width);
            w.U32(layer.Height);
            w.U32(1); // RGBA
            var name = Encoding.UTF8.GetBytes(layer.Name + "\0");
            w.U32(name.Length);
            w.Bytes(name);
            w.U32(6); w.U32(4); w.U32(255);                   // PROP_OPACITY
            w.U32(8); w.U32(4); w.U32(layer.Visible ? 1 : 0); // PROP_VISIBLE
            w.U32(15); w.U32(8); w.U32(layer.X); w.U32(layer.Y); // PROP_OFFSETS
            w.U32(7); w.U32(4); w.U32(0);                     // PROP_MODE normal
            w.U32(0); w.U32(0);                               // PROP_END
            var hierarchyPointer = w.Position;
            w.U32(0);
            w.U32(0); // no layer mask

            w.Patch(hierarchyPointer, w.Position);
            w.U32(layer.Width);
            w.U32(layer.Height);
            w.U32(4); // bytes per pixel
            var levelPointer = w.Position;
            w.U32(0);
            w.U32(0); // no smaller levels

            w.Patch(levelPointer, w.Position);
            w.U32(layer.Width);
            w.U32(layer.Height);
            var columns = (layer.Width + 63) / 64;
            var rows = (layer.Height + 63) / 64;
            var tileTable = w.Position;
            for (var t = 0; t < columns * rows; t++)
                w.U32(0);
            w.U32(0);
            for (var t = 0; t < columns * rows; t++)
            {
                w.Patch(tileTable + 4 * t, w.Position);
                var tileWidth = Math.Min(64, layer.Width - t % columns * 64);
                var tileHeight = Math.Min(64, layer.Height - t / columns * 64);
                foreach (var value in new[] { layer.R, layer.G, layer.B, layer.A })
                {
                    // One long run per channel: 127, 16-bit count, value.
                    w.Byte(127);
                    w.Byte((byte)(tileWidth * tileHeight >> 8));
                    w.Byte((byte)(tileWidth * tileHeight));
                    w.Byte(value);
                }
            }
        }
        return w.ToArray();
    }

    // ───────────────────────── DNG ─────────────────────────

    /// <summary>
    /// A minimal "linear" DNG (demosaiced RGB, 16 bit, uncompressed) as written by raw converters: enough for LibRaw.
    /// The left half is red, the right half blue.
    /// </summary>
    public static byte[] LinearDng(int width, int height, ushort orientation)
    {
        var pixels = new byte[width * height * 6];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = x < width / 2 ? (50000, 3000, 3000) : (3000, 3000, 50000);
                var i = (y * width + x) * 6;
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i), (ushort)r);
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i + 2), (ushort)g);
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i + 4), (ushort)b);
            }
        }

        // XYZ → camera: the camera is linear sRGB, so this is the XYZ → linear sRGB matrix.
        int[] colorMatrix = [32406, -15372, -4986, -9689, 18758, 415, 557, -2040, 10570];
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
        {
            (254, 4, 1, U32(0)),
            (256, 4, 1, U32((uint)width)),
            (257, 4, 1, U32((uint)height)),
            (258, 3, 3, [.. U16(16), .. U16(16), .. U16(16)]),
            (259, 3, 1, U16(1)),
            (262, 3, 1, U16(34892)), // LinearRaw
            (271, 2, 6, "Filee\0"u8.ToArray()),
            (272, 2, 5, "Test\0"u8.ToArray()),
            (273, 4, 1, U32(0)), // strip offset, patched below
            (274, 3, 1, U16(orientation)),
            (277, 3, 1, U16(3)),
            (278, 4, 1, U32((uint)height)),
            (279, 4, 1, U32((uint)pixels.Length)),
            (284, 3, 1, U16(1)),
            (50706, 1, 4, [1, 4, 0, 0]),
            (50707, 1, 4, [1, 1, 0, 0]),
            (50708, 2, 11, "Filee Test\0"u8.ToArray()),
            (50717, 4, 1, U32(65535)),
            (50721, 10, 9, colorMatrix.SelectMany(v => (byte[])[.. U32((uint)v), .. U32(10000)]).ToArray()),
            (50728, 5, 3, [.. U32(1), .. U32(1), .. U32(1), .. U32(1), .. U32(1), .. U32(1)]),
            (50778, 3, 1, U16(21)), // D65
        };

        using var stream = new MemoryStream();
        var ifdSize = 2 + entries.Count * 12 + 4;
        var extra = 8 + ifdSize;
        var extraData = new MemoryStream();
        var ifd = new byte[ifdSize];
        BinaryPrimitives.WriteUInt16LittleEndian(ifd, (ushort)entries.Count);
        var stripOffsetField = 0;
        for (var e = 0; e < entries.Count; e++)
        {
            var (tag, type, count, value) = entries[e];
            var at = 2 + e * 12;
            BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(ifd.AsSpan(at + 4), count);
            if (value.Length <= 4)
            {
                value.CopyTo(ifd, at + 8);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(ifd.AsSpan(at + 8), (uint)(extra + extraData.Length));
                extraData.Write(value);
                if (extraData.Length % 2 == 1)
                    extraData.WriteByte(0);
            }
            if (tag == 273)
                stripOffsetField = at + 8;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(ifd.AsSpan(stripOffsetField), (uint)(extra + extraData.Length));

        stream.Write("II*\0"u8);
        stream.Write(U32(8));
        stream.Write(ifd);
        extraData.Position = 0;
        extraData.CopyTo(stream);
        stream.Write(pixels);
        return stream.ToArray();

        static byte[] U16(ushort v) => BitConverter.GetBytes(v);
        static byte[] U32(uint v) => BitConverter.GetBytes(v);
    }

    // ───────────────────────── ICNS ─────────────────────────

    /// <summary>An .icns file from raw entries (no table of contents, as old files have).</summary>
    public static byte[] Icns(params (string Type, byte[] Data)[] entries)
    {
        var w = new BigEndianWriter();
        w.Bytes("icns"u8);
        w.U32(8 + entries.Sum(e => 8 + e.Data.Length));
        foreach (var (type, data) in entries)
        {
            w.Bytes(Encoding.Latin1.GetBytes(type));
            w.U32(8 + data.Length);
            w.Bytes(data);
        }
        return w.ToArray();
    }

    /// <summary>Legacy RGB entry data (is32, il32, ih32, it32): R, G and B planes of one colour, RLE encoded.</summary>
    public static byte[] LegacyRgb(int size, byte r, byte g, byte b, bool it32 = false)
    {
        var planes = new byte[size * size * 3];
        planes.AsSpan(0, size * size).Fill(r);
        planes.AsSpan(size * size, size * size).Fill(g);
        planes.AsSpan(2 * size * size, size * size).Fill(b);
        // Vary the first row so the encoder emits literal runs too.
        for (var x = 0; x < size; x += 2)
            planes[x] = (byte)(r ^ 1);
        var packed = PackBits(planes);
        return it32 ? [0, 0, 0, 0, .. packed] : packed;
    }

    /// <summary>Apple's icon RLE (see IcnsFile.UnpackBits): literal runs of 1..128 bytes, repeats of 3..130.</summary>
    public static byte[] PackBits(ReadOnlySpan<byte> source)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < source.Length)
        {
            var run = 1;
            while (i + run < source.Length && run < 130 && source[i + run] == source[i])
                run++;
            if (run >= 3)
            {
                output.Add((byte)(run + 125));
                output.Add(source[i]);
                i += run;
                continue;
            }
            var start = i;
            while (i < source.Length && i - start < 128
                   && !(i + 2 < source.Length && source[i] == source[i + 1] && source[i] == source[i + 2]))
                i++;
            output.Add((byte)(i - start - 1));
            for (var j = start; j < i; j++)
                output.Add(source[j]);
        }
        return [.. output];
    }

    // ───────────────────────── Windows metafiles ─────────────────────────

    /// <summary>
    /// Records an EMF of <paramref name="widthMm"/> × <paramref name="heightMm"/> with GDI: a white frame filled
    /// with a teal rectangle whose left third is orange.
    /// </summary>
    public static void Emf(string path, int widthMm, int heightMm)
    {
        var frame = new Rect { Right = widthMm * 100, Bottom = heightMm * 100 }; // .01 mm units
        var dc = CreateEnhMetaFileW(IntPtr.Zero, path, ref frame, "Filee test\0\0");
        Assert.NotEqual(IntPtr.Zero, dc);
        DrawSample(dc, widthMm, heightMm);
        DeleteEnhMetaFile(CloseEnhMetaFile(dc));
    }

    /// <summary>
    /// Writes a placeable WMF (Aldus header + Windows metafile), converted from an EMF by GDI, 1440 units per inch.
    /// </summary>
    public static void Wmf(string path, int widthMm, int heightMm)
    {
        var emf = Path.ChangeExtension(path, ".tmp.emf");
        Emf(emf, widthMm, heightMm);
        var handle = GetEnhMetaFileW(emf);
        var screen = GetDC(IntPtr.Zero);
        try
        {
            var size = GetWinMetaFileBits(handle, 0, null, 8 /* MM_ANISOTROPIC */, screen);
            var bits = new byte[size];
            GetWinMetaFileBits(handle, size, bits, 8, screen);

            const int unitsPerInch = 1440;
            var right = (short)(widthMm * unitsPerInch / 25.4);
            var bottom = (short)(heightMm * unitsPerInch / 25.4);
            var header = new byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x9AC6CDD7);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(10), right);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12), bottom);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), unitsPerInch);
            ushort checksum = 0;
            for (var i = 0; i < 20; i += 2)
                checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(i));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), checksum);
            File.WriteAllBytes(path, [.. header, .. bits]);
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screen);
            DeleteEnhMetaFile(handle);
            File.Delete(emf);
        }
    }

    private static void DrawSample(IntPtr dc, int widthMm, int heightMm)
    {
        // Draw in 0.01 mm (MM_HIMETRIC, y grows upwards) so the picture fills the frame whatever screen the metafile is
        // recorded against: device units would follow the display's resolution and scaling at the time of the test.
        _ = SetMapMode(dc, 3 /* MM_HIMETRIC */);
        var w = widthMm * 100;
        var h = heightMm * 100;
        Fill(dc, 0, 0, w, -h, 0x7F9E1D); // teal (COLORREF is 0x00BBGGRR)
        Fill(dc, 0, 0, w / 3, -h, 0x1E7FFF); // orange

        static void Fill(IntPtr dc, int left, int top, int right, int bottom, uint color)
        {
            var brush = CreateSolidBrush(color);
            var rect = new Rect { Left = left, Top = top, Right = right, Bottom = bottom };
            _ = FillRect(dc, ref rect, brush);
            DeleteObject(brush);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEnhMetaFileW(IntPtr reference, string path, ref Rect frame, string description);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CloseEnhMetaFile(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int SetMapMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteEnhMetaFile(IntPtr metafile);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetEnhMetaFileW(string path);

    [DllImport("gdi32.dll")]
    private static extern int GetWinMetaFileBits(IntPtr metafile, int size, byte[]? buffer, int mapMode, IntPtr reference);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr dc, int index);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    /// <summary>Big-endian writer with back-patching of 32-bit offsets.</summary>
    private sealed class BigEndianWriter
    {
        private readonly MemoryStream _stream = new();

        public int Position => (int)_stream.Position;

        public void Byte(byte value) => _stream.WriteByte(value);

        public void Bytes(ReadOnlySpan<byte> value) => _stream.Write(value);

        public void U32(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void Patch(int position, int value)
        {
            var end = _stream.Position;
            _stream.Position = position;
            U32(value);
            _stream.Position = end;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
