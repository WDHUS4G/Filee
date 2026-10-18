// Splits a polygon (OBJ and PLY faces with more than three corners) into triangles by ear clipping in the plane of
// the polygon, so concave faces come out right; a fan is the fallback for faces that are not simple polygons.

using System.Numerics;

namespace Filee.Engines.Models3D;

internal static class Triangulator
{
    /// <summary>Adds the triangles of the polygon <paramref name="corners"/> (vertex indices of <paramref name="part"/>).</summary>
    public static void AddPolygon(MeshPart part, ReadOnlySpan<int> corners)
    {
        switch (corners.Length)
        {
            case < 3:
                return;
            case 3:
                part.AddTriangle(corners[0], corners[1], corners[2]);
                return;
        }
        if (corners.Length > 64 || !EarClip(part, corners))
        {
            for (var i = 1; i + 1 < corners.Length; i++)
                part.AddTriangle(corners[0], corners[i], corners[i + 1]);
        }
    }

    private static bool EarClip(MeshPart part, ReadOnlySpan<int> corners)
    {
        var n = corners.Length;
        // Newell's normal gives the polygon's plane and winding even for concave polygons.
        var normal = Vector3.Zero;
        for (var i = 0; i < n; i++)
        {
            var a = part.Position(corners[i]);
            var b = part.Position(corners[(i + 1) % n]);
            normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        if (normal.LengthSquared() == 0 || !float.IsFinite(normal.X))
            return false;

        // Project onto the plane: drop the dominant axis, keeping the polygon counter-clockwise.
        var abs = Vector3.Abs(normal);
        Span<Vector2> points = stackalloc Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var p = part.Position(corners[i]);
            points[i] = abs.X >= abs.Y && abs.X >= abs.Z ? (normal.X >= 0 ? new Vector2(p.Y, p.Z) : new Vector2(p.Z, p.Y))
                : abs.Y >= abs.Z ? (normal.Y >= 0 ? new Vector2(p.Z, p.X) : new Vector2(p.X, p.Z))
                : (normal.Z >= 0 ? new Vector2(p.X, p.Y) : new Vector2(p.Y, p.X));
        }

        Span<int> remaining = stackalloc int[n];
        for (var i = 0; i < n; i++)
            remaining[i] = i;
        var count = n;
        var guard = n * n;
        var start = part.Indices.Count;
        while (count > 3 && guard-- > 0)
        {
            var clipped = false;
            for (var i = 0; i < count; i++)
            {
                var prev = remaining[(i + count - 1) % count];
                var cur = remaining[i];
                var next = remaining[(i + 1) % count];
                if (Cross(points[prev], points[cur], points[next]) <= 0)
                    continue; // reflex or degenerate corner
                var inside = false;
                for (var j = 0; j < count && !inside; j++)
                {
                    var k = remaining[j];
                    if (k != prev && k != cur && k != next && InTriangle(points[k], points[prev], points[cur], points[next]))
                        inside = true;
                }
                if (inside)
                    continue;
                part.AddTriangle(corners[prev], corners[cur], corners[next]);
                remaining[(i + 1)..count].CopyTo(remaining[i..]);
                count--;
                clipped = true;
                break;
            }
            if (!clipped)
            {
                part.Indices.RemoveRange(start, part.Indices.Count - start);
                return false;
            }
        }
        part.AddTriangle(corners[remaining[0]], corners[remaining[1]], corners[remaining[2]]);
        return true;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c) =>
        Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0;
}
