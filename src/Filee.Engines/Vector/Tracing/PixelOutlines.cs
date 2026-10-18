// Step 1 of tracing: the outlines of a pixel mask, as closed polygons along the pixel edges.
//
// Every edge between an inside and an outside pixel is a directed edge of the pixel grid, oriented so the inside is
// on its right (on screen, with y pointing down): around a filled area the outline runs clockwise, around a hole
// counter-clockwise, so the outlines fill correctly with the non-zero rule. Each grid point has as many edges
// leaving as arriving; where two inside pixels touch only at a corner (a "saddle") two edges leave, and the walk turns
// left, which keeps diagonally touching pixels in one shape (8-connected shapes, 4-connected holes).

using System.Numerics;

namespace Filee.Engines.Vector.Tracing;

/// <summary>A closed outline along pixel edges: its corner points (where the direction changes) in order.</summary>
/// <param name="Xs">X of each corner.</param>
/// <param name="Ys">Y of each corner.</param>
/// <param name="Area">Signed area in square pixels: positive around filled areas, negative around holes.</param>
internal sealed record PixelOutline(int[] Xs, int[] Ys, double Area)
{
    public int Count => Xs.Length;
}

/// <summary>Finds the outlines of the inside pixels of a mask.</summary>
internal static class PixelOutlines
{
    private static readonly int[] StepX = [1, 0, -1, 0];
    private static readonly int[] StepY = [0, 1, 0, -1];

    /// <summary>Directions as used below: 0 right, 1 down, 2 left, 3 up (clockwise on screen).</summary>
    private const int Right = 0;

    /// <summary>
    /// Every outline of the pixels where <paramref name="mask"/> is not 0. Outlines enclosing less than
    /// <paramref name="minArea"/> square pixels are left out: specks disappear and small holes are filled.
    /// </summary>
    public static List<PixelOutline> Trace(byte[] mask, int width, int height, double minArea, CancellationToken ct)
    {
        var outlines = new List<PixelOutline>();
        var stride = width + 1;
        // One bit per direction and grid point: edges already walked.
        var visited = new byte[stride * (height + 1)];
        var xs = new List<int>();
        var ys = new List<int>();

        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                // Every outline has an edge going right: the top edge of an inside pixel below an outside one.
                if (mask[row + x] == 0 || (y > 0 && mask[row - width + x] != 0) || (visited[y * stride + x] & 1) != 0)
                    continue;
                var area2 = Walk(mask, width, height, visited, x, y, xs, ys);
                if (Math.Abs(area2) / 2.0 >= Math.Max(minArea, 1e-9) && xs.Count >= 4)
                    outlines.Add(new PixelOutline([.. xs], [.. ys], area2 / 2.0));
            }
        }
        return outlines;
    }

    /// <summary>Walks one outline from the right-going edge at (x, y); returns twice its signed area.</summary>
    private static long Walk(byte[] mask, int width, int height, byte[] visited, int startX, int startY, List<int> xs, List<int> ys)
    {
        xs.Clear();
        ys.Clear();
        var stride = width + 1;
        int x = startX, y = startY, direction = Right;
        long area2 = 0;
        while (true)
        {
            visited[y * stride + x] |= (byte)(1 << direction);
            int nx = x + StepX[direction], ny = y + StepY[direction];
            area2 += (long)x * ny - (long)nx * y;
            x = nx;
            y = ny;

            var leaving = Leaving(mask, width, height, x, y);
            int next;
            if ((leaving & (leaving - 1)) == 0)
            {
                next = BitOperations.TrailingZeroCount(leaving);
            }
            else
            {
                // Saddle: turn left, so pixels touching at the corner stay together.
                next = (direction + 3) & 3;
                if ((leaving & (1 << next)) == 0)
                    next = (direction + 1) & 3;
            }

            if (x == startX && y == startY && next == Right)
            {
                if (direction != Right)
                {
                    xs.Add(x);
                    ys.Add(y);
                }
                return area2;
            }
            if (next != direction)
            {
                xs.Add(x);
                ys.Add(y);
            }
            direction = next;
        }
    }

    /// <summary>Bits of the edges leaving grid point (x, y), from the four pixels around it.</summary>
    private static int Leaving(byte[] mask, int width, int height, int x, int y)
    {
        var topLeft = Inside(mask, width, height, x - 1, y - 1);
        var topRight = Inside(mask, width, height, x, y - 1);
        var bottomLeft = Inside(mask, width, height, x - 1, y);
        var bottomRight = Inside(mask, width, height, x, y);
        // Each edge has the inside pixel on its right-hand side.
        var bits = 0;
        if (bottomRight && !topRight)
            bits |= 1; // right
        if (bottomLeft && !bottomRight)
            bits |= 2; // down
        if (topLeft && !bottomLeft)
            bits |= 4; // left
        if (topRight && !topLeft)
            bits |= 8; // up
        return bits;
    }

    private static bool Inside(byte[] mask, int width, int height, int x, int y) =>
        (uint)x < (uint)width && (uint)y < (uint)height && mask[y * width + x] != 0;
}
