// Text as glyph outlines: shaped with HarfBuzz the way Svg.Skia does it (adapted from SkiaModel.TextShaping.cs,
// Svg.Skia, MIT), so Arabic joins and Indic syllables come out right, and drawn as paths, so no font is embedded.
// Used for SVG text in PDFs (OutlinedTextPlayback) and for PDF watermarks and page numbers (PdfFinishing).

using System.Runtime.InteropServices;
using HarfBuzzSharp;
using Buffer = HarfBuzzSharp.Buffer;
using SKFont = SkiaSharp.SKFont;
using SKPath = SkiaSharp.SKPath;
using SKTextAlign = SkiaSharp.SKTextAlign;
using SKTypeface = SkiaSharp.SKTypeface;

namespace Filee.Engines.Vector;

/// <summary>Shapes text and appends its glyph outlines to a path; HarfBuzz fonts are cached per typeface.</summary>
internal sealed class GlyphOutlines : IDisposable
{
    /// <summary>HarfBuzz works in integer units: positions are computed at this scale and divided back.</summary>
    private const int HarfBuzzScale = 512;

    private readonly Dictionary<string, Font?> _shapers = new(StringComparer.Ordinal);

    /// <summary>Shapes <paramref name="text"/> and appends its glyph outlines, aligned at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public bool AppendShaped(SKPath path, string text, SKFont font, float x, float y, SKTextAlign align)
    {
        if (string.IsNullOrEmpty(text))
            return true;
        var shaper = ShaperFor(font.Typeface);
        if (shaper is null)
            return false;

        using var buffer = new Buffer();
        buffer.ClusterLevel = ClusterLevel.Characters;
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        shaper.Shape(buffer);

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        var scaleY = font.Size / HarfBuzzScale;
        var scaleX = scaleY * font.ScaleX;
        var width = 0f;
        foreach (var position in positions)
            width += position.XAdvance * scaleX;
        var penX = x - align switch
        {
            SKTextAlign.Center => width / 2,
            SKTextAlign.Right => width,
            _ => 0f,
        };
        var penY = y;
        for (var i = 0; i < infos.Length; i++)
        {
            AppendGlyph(path, font, (ushort)infos[i].Codepoint, penX + positions[i].XOffset * scaleX, penY - positions[i].YOffset * scaleY);
            penX += positions[i].XAdvance * scaleX;
            penY -= positions[i].YAdvance * scaleY;
        }
        return true;
    }

    public static void AppendGlyph(SKPath path, SKFont font, ushort glyph, float x, float y)
    {
        using var outline = font.GetGlyphPath(glyph);
        if (outline is { IsEmpty: false })
            path.AddPath(outline, x, y);
    }

    /// <summary>A HarfBuzz font for the typeface (cached), or null when its font data can't be read.</summary>
    private Font? ShaperFor(SKTypeface typeface)
    {
        var key = $"{typeface.FamilyName}|{typeface.FontWeight}|{typeface.FontWidth}|{typeface.FontSlant}";
        if (_shapers.TryGetValue(key, out var cached))
            return cached;

        Font? font = null;
        var asset = typeface.OpenStream(out var index);
        if (asset is not null)
        {
            using var blob = ToBlob(asset);
            using var face = new Face(blob, index) { Index = index, UnitsPerEm = typeface.UnitsPerEm };
            font = new Font(face);
            font.SetScale(HarfBuzzScale, HarfBuzzScale);
            font.SetFunctionsOpenType();
        }
        _shapers[key] = font;
        return font;
    }

    /// <summary>Wraps Skia's font data for HarfBuzz without copying when Skia has it in memory.</summary>
    private static Blob ToBlob(SkiaSharp.SKStreamAsset asset)
    {
        var size = asset.Length;
        var memory = asset.GetMemoryBase();
        Blob blob;
        if (memory != IntPtr.Zero)
        {
            blob = new Blob(memory, size, MemoryMode.ReadOnly, asset.Dispose);
        }
        else
        {
            var copy = Marshal.AllocCoTaskMem(size);
            asset.Read(copy, size);
            blob = new Blob(copy, size, MemoryMode.ReadOnly, () =>
            {
                Marshal.FreeCoTaskMem(copy);
                asset.Dispose();
            });
        }
        blob.MakeImmutable();
        return blob;
    }

    public void Dispose()
    {
        foreach (var font in _shapers.Values)
            font?.Dispose();
        _shapers.Clear();
    }
}
