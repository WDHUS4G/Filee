// A small software rasteriser for 3D → picture: an isometric view from the front right, above, with flat shading
// (one light, faces lit by their geometric normal turned towards the viewer) and a z-buffer, rendered at twice the
// size and scaled down for smooth edges. Colours come from the vertex colours, else the material (its texture
// times its base colour), else grey; both are interpolated across each triangle, which is exact for a parallel
// projection.

using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Filee.Engines.Models3D;

internal static class MeshRenderer
{
    private static readonly Vector4 DefaultColor = new(0.72f, 0.74f, 0.78f, 1);

    private sealed record Picture(SKColor[] Pixels, int Width, int Height);

    /// <summary>What one triangle is painted with: three corner colours, or a texture at three UVs times a tint.</summary>
    private readonly record struct Paint(Vector3 A, Vector3 B, Vector3 C, Picture? Texture, Vector2 UvA, Vector2 UvB, Vector2 UvC);

    /// <summary>Renders <paramref name="model"/> into a square PNG of <paramref name="size"/> pixels with a transparent background.</summary>
    public static byte[] RenderPng(Model3D model, int size, CancellationToken cancellationToken)
    {
        const int scale = 2;
        var full = size * scale;
        var pixels = new uint[full * full];
        var depth = new float[full * full];
        Array.Fill(depth, float.NegativeInfinity);

        // Camera at (1, 1, 1) looking at the origin, Y up: the classic isometric view. The light comes from the
        // upper left, a little from the front, so the top, left and right faces of a box differ.
        var forward = Vector3.Normalize(new Vector3(-1, -1, -1));
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        var light = Vector3.Normalize(-0.5f * right + 0.8f * up - 0.6f * forward);

        // Fit the projected bounds into the picture with a margin.
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var part in model.Parts)
        {
            for (var v = 0; v < part.VertexCount; v++)
            {
                var p = part.Position(v);
                var x = Vector3.Dot(p, right);
                var y = Vector3.Dot(p, up);
                minX = MathF.Min(minX, x);
                maxX = MathF.Max(maxX, x);
                minY = MathF.Min(minY, y);
                maxY = MathF.Max(maxY, y);
            }
        }
        var extent = MathF.Max(maxX - minX, maxY - minY);
        var pixelsPerUnit = extent > 0 && float.IsFinite(extent) ? full * 0.9f / extent : 1;
        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;

        var textures = new Dictionary<Texture3D, Picture?>();
        foreach (var part in model.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var material = model.MaterialOf(part);
            var texture = material?.BaseColorTexture is { } t && part.TexCoords is not null ? Decode(textures, t) : null;
            var tint = Rgb(material is null ? DefaultColor : MeshMath.LinearToSrgb(material.BaseColor));
            var indices = part.IndexSpan;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
                Vector3 a = part.Position(ia), b = part.Position(ib), c = part.Position(ic);
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() == 0 || !float.IsFinite(normal.X))
                    continue;
                normal = Vector3.Normalize(normal);
                // Seen from behind (an open surface, or a mesh wound the other way): light the side we see.
                if (Vector3.Dot(normal, forward) > 0)
                    normal = -normal;
                var shade = 0.45f + 0.55f * MathF.Max(0, Vector3.Dot(normal, light));

                var paint = part.Colors is not null
                    ? new Paint(Rgb(part.Color(ia)) * shade, Rgb(part.Color(ib)) * shade, Rgb(part.Color(ic)) * shade, null, default, default, default)
                    : new Paint(tint * shade, tint * shade, tint * shade, texture, part.TexCoord(ia), part.TexCoord(ib), part.TexCoord(ic));
                Fill(pixels, depth, full, Project(a), Project(b), Project(c), paint);
            }
        }

        var info = new SKImageInfo(full, full, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var image = SKImage.FromPixelCopy(info, MemoryMarshal.AsBytes(pixels.AsSpan()), full * 4);
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, size, size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();

        Vector3 Project(Vector3 p) => new(
            full / 2f + (Vector3.Dot(p, right) - centerX) * pixelsPerUnit,
            full / 2f - (Vector3.Dot(p, up) - centerY) * pixelsPerUnit,
            -Vector3.Dot(p, forward));
    }

    private static Vector3 Rgb(Vector4 color) => new(color.X, color.Y, color.Z);

    /// <summary>Fills one triangle into the z-buffered picture (larger depth = nearer).</summary>
    private static void Fill(uint[] pixels, float[] depth, int size, Vector3 a, Vector3 b, Vector3 c, in Paint paint)
    {
        var area = Edge(a, b, c.X, c.Y);
        if (MathF.Abs(area) < 1e-6f || !float.IsFinite(area))
            return;
        var x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        var inverse = 1 / area;
        var flat = paint.Texture is null && paint.A == paint.B && paint.B == paint.C;
        var flatColor = Pack(paint.A);
        for (var y = y0; y <= y1; y++)
        {
            var py = y + 0.5f;
            var row = y * size;
            for (var x = x0; x <= x1; x++)
            {
                var px = x + 0.5f;
                var w0 = Edge(b, c, px, py) * inverse;
                var w1 = Edge(c, a, px, py) * inverse;
                var w2 = Edge(a, b, px, py) * inverse;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                    continue;
                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                if (z <= depth[row + x])
                    continue;
                depth[row + x] = z;
                if (flat)
                {
                    pixels[row + x] = flatColor;
                    continue;
                }
                var color = w0 * paint.A + w1 * paint.B + w2 * paint.C;
                if (paint.Texture is { } texture)
                    color *= Sample(texture, w0 * paint.UvA + w1 * paint.UvB + w2 * paint.UvC);
                pixels[row + x] = Pack(color);
            }
        }
    }

    private static uint Pack(Vector3 rgb)
    {
        rgb = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        return (uint)(rgb.X * 255 + 0.5f) | (uint)(rgb.Y * 255 + 0.5f) << 8 | (uint)(rgb.Z * 255 + 0.5f) << 16 | 0xFF000000u;
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    private static Picture? Decode(Dictionary<Texture3D, Picture?> cache, Texture3D texture)
    {
        if (cache.TryGetValue(texture, out var decoded))
            return decoded;
        using var bitmap = SKBitmap.Decode(texture.Data);
        decoded = bitmap is null || bitmap.Width == 0 || bitmap.Height == 0 ? null : new Picture(bitmap.Pixels, bitmap.Width, bitmap.Height);
        cache[texture] = decoded;
        return decoded;
    }

    /// <summary>The texture's colour at <paramref name="uv"/> (origin bottom left, repeating), nearest pixel.</summary>
    private static Vector3 Sample(Picture texture, Vector2 uv)
    {
        if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
            return Vector3.One;
        var u = uv.X - MathF.Floor(uv.X);
        var v = uv.Y - MathF.Floor(uv.Y);
        var x = Math.Clamp((int)(u * texture.Width), 0, texture.Width - 1);
        var y = Math.Clamp((int)((1 - v) * texture.Height), 0, texture.Height - 1);
        var pixel = texture.Pixels[y * texture.Width + x];
        return new Vector3(pixel.Red / 255f, pixel.Green / 255f, pixel.Blue / 255f);
    }
}
