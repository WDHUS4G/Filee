// Finds the cheapest chain of converters from a source format to a target format.
//
// Why a graph search: engines only know direct conversions (edges). Many useful conversions need
// two hops, e.g. HWPX → PDF (rhwp) → PNG (PDFium). Treating formats as nodes and converter edges as
// weighted arcs lets new engines automatically unlock new routes without touching any other code.

using Filee.Core.Formats;

namespace Filee.Core.Conversion;

/// <summary>One hop of a route.</summary>
/// <param name="FrameSequence">The hop saves every frame of a video (<see cref="ConversionEdge.FrameSequence"/>).</param>
/// <param name="Unshaped">The hop draws text without complex-script shaping (<see cref="ConversionEdge.Unshaped"/>).</param>
/// <param name="MotionPhoto">The hop takes the video of a Live / Motion Photo (<see cref="ConversionEdge.MotionPhoto"/>).</param>
/// <param name="LivePhoto">The hop makes a Live / Motion Photo of a video (<see cref="ConversionEdge.LivePhoto"/>).</param>
public sealed record RouteStep(IConverter Converter, string From, string To, bool FrameSequence = false, bool Unshaped = false,
    bool MotionPhoto = false, bool LivePhoto = false);

/// <summary>A planned conversion from one format to another.</summary>
public sealed record Route(IReadOnlyList<RouteStep> Steps, int Cost)
{
    /// <summary>Index of the first step that turns and mirrors images (<see cref="IImageTransformer"/>), or -1.</summary>
    public int FirstTransformStep()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Converter is IImageTransformer transformer && transformer.Transforms(Steps[i].From, Steps[i].To))
                return i;
        }
        return -1;
    }
}

/// <summary>Plans conversion routes over the available converters.</summary>
public sealed class RoutePlanner
{
    /// <summary>Maximum number of hops. Longer chains are almost always lossy and slow.</summary>
    public const int MaxSteps = 3;

    /// <summary>Every edge with its cost (priority penalty included), by source format.</summary>
    private readonly Dictionary<string, List<(IConverter Converter, ConversionEdge Edge, int Cost)>> _edgesFrom;

    /// <param name="converters">Converters that are currently available.</param>
    /// <param name="priorityPenalty">
    /// Extra cost per step for a converter, derived from the user's engine priority order.
    /// Keep it small (0..9) so priority breaks ties between engines without making routes longer.
    /// </param>
    public RoutePlanner(IEnumerable<IConverter> converters, Func<IConverter, int>? priorityPenalty = null)
    {
        var penalty = priorityPenalty ?? (_ => 0);
        _edgesFrom = converters
            .SelectMany(c => c.Edges.Select(e =>
                (c, e with { From = e.From.ToLowerInvariant(), To = e.To.ToLowerInvariant() }, e.Cost + penalty(c))))
            .GroupBy(e => e.Item2.From)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Returns the cheapest route, or <c>null</c> if the conversion is impossible.</summary>
    /// <param name="frames">
    /// Save every frame of a video (<see cref="Presets.Preset.SavesFramesOf"/>): the route is one
    /// <see cref="ConversionEdge.FrameSequence"/> edge from <paramref name="from"/> to <paramref name="to"/>, or none.
    /// Without it those edges are never taken.
    /// </param>
    /// <param name="shaping">
    /// The source has text that needs complex-script shaping — or letters the unshaped engine has no widths for
    /// (outside ASCII and Latin-1 other than full-width CJK): <see cref="ConversionEdge.Unshaped"/> edges are
    /// left out.
    /// </param>
    /// <param name="motionPhoto">
    /// Take the video of a Live / Motion Photo (<see cref="Presets.Preset.ExtractsMotionOf"/>): the route is one
    /// <see cref="ConversionEdge.MotionPhoto"/> edge, or none. Without it those edges are never taken.
    /// </param>
    /// <param name="livePhoto">
    /// Make a Live / Motion Photo of a video (<see cref="Presets.Preset.MakesLivePhotoOf"/>): the route is one
    /// <see cref="ConversionEdge.LivePhoto"/> edge, or none. Without it those edges are never taken.
    /// </param>
    /// <param name="ocr">
    /// Recognize the text first (<see cref="Presets.Preset.RecognizesTextOf"/>): an <see cref="ConversionEdge.Ocr"/>
    /// edge to PDF, from the file itself or from a PNG of a picture Tesseract can't read, then the cheapest route from
    /// that PDF on. Without it those edges are never taken.
    /// </param>
    public Route? Plan(string from, string to, bool frames = false, bool shaping = false, bool motionPhoto = false, bool livePhoto = false,
        bool ocr = false)
    {
        from = from.ToLowerInvariant();
        to = to.ToLowerInvariant();

        if (ocr)
            return PlanWithOcr(from, to, shaping);

        if (frames || motionPhoto || livePhoto)
        {
            // One of them: frames, then the video of a photo, then a photo of a video.
            motionPhoto &= !frames;
            livePhoto &= !frames && !motionPhoto;
            var special = EdgesFrom(from)
                .Where(e => e.Edge.To == to && (frames ? e.Edge.FrameSequence : motionPhoto ? e.Edge.MotionPhoto : e.Edge.LivePhoto))
                .OrderBy(e => e.Cost)
                .FirstOrDefault();
            return special.Converter is null
                ? null
                : new Route([new RouteStep(special.Converter, from, to, FrameSequence: frames, MotionPhoto: motionPhoto, LivePhoto: livePhoto)],
                    special.Cost);
        }

        // Same-format "conversions" (re-encode, split, compress) must use an explicit self edge.
        if (from == to)
        {
            var best = EdgesFrom(from)
                .Where(e => e.Edge.To == to && !e.Edge.FrameSequence && !e.Edge.MotionPhoto && !e.Edge.LivePhoto && !e.Edge.Ocr)
                .OrderBy(e => e.Cost)
                .FirstOrDefault();
            return best.Converter is null
                ? null
                : new Route([new RouteStep(best.Converter, from, to)], best.Cost);
        }

        // Edges for raster images only (tracing) are left out unless the file itself is one.
        var imageSource = FormatRegistry.FindById(from)?.Category == FormatCategory.Image;

        // Dijkstra over formats. The graph is small (a few hundred nodes).
        var dist = new Dictionary<string, int> { [from] = 0 };
        var prev = new Dictionary<string, (string Node, RouteStep Step)>();
        var hops = new Dictionary<string, int> { [from] = 0 };
        var queue = new PriorityQueue<string, int>();
        queue.Enqueue(from, 0);

        while (queue.TryDequeue(out var node, out var cost))
        {
            if (cost > dist[node])
                continue; // stale queue entry
            if (node == to)
                break;
            if (hops[node] >= MaxSteps)
                continue;

            foreach (var (converter, edge, edgeCost) in EdgesFrom(node))
            {
                if (edge.To == edge.From || edge.FrameSequence || edge.MotionPhoto || edge.LivePhoto || edge.Ocr
                    || (edge.ImageSourcesOnly && !imageSource) || (edge.NotForImageSources && imageSource)
                    || (edge.Unshaped && shaping)
                    || (edge.DirectOnly && (node != from || edge.To != to)))
                    continue;
                var next = cost + edgeCost;
                if (dist.TryGetValue(edge.To, out var known) && known <= next)
                    continue;

                dist[edge.To] = next;
                hops[edge.To] = hops[node] + 1;
                prev[edge.To] = (node, new RouteStep(converter, edge.From, edge.To, Unshaped: edge.Unshaped));
                queue.Enqueue(edge.To, next);
            }
        }

        if (!prev.ContainsKey(to))
            return null;

        var steps = new List<RouteStep>();
        for (var node = to; node != from; node = prev[node].Node)
            steps.Add(prev[node].Step);
        steps.Reverse();
        return new Route(steps, dist[to]);
    }

    /// <summary>True if <paramref name="from"/> can be converted to <paramref name="to"/>.</summary>
    /// <param name="frames">Every frame of a video as pictures (see <see cref="Plan"/>).</param>
    /// <param name="motionPhoto">The video of a Live / Motion Photo (see <see cref="Plan"/>).</param>
    /// <param name="livePhoto">A Live / Motion Photo of a video (see <see cref="Plan"/>).</param>
    /// <param name="ocr">Recognize the text first (see <see cref="Plan"/>).</param>
    public bool CanConvert(string from, string to, bool frames = false, bool motionPhoto = false, bool livePhoto = false, bool ocr = false) =>
        Plan(from, to, frames, motionPhoto: motionPhoto, livePhoto: livePhoto, ocr: ocr) is not null;

    private Route? PlanWithOcr(string from, string to, bool shaping)
    {
        var steps = new List<RouteStep>();
        var cost = 0;
        var source = from;
        if (OcrEdge(from).Converter is null)
        {
            // A picture Tesseract can't open (HEIC, PSD, …) becomes a PNG first.
            if (FormatRegistry.FindById(from)?.Category != FormatCategory.Image || Plan(from, "png", shaping: shaping) is not { } toPng)
                return null;
            steps.AddRange(toPng.Steps);
            cost += toPng.Cost;
            source = "png";
        }
        var ocr = OcrEdge(source);
        if (ocr.Converter is null)
            return null;
        steps.Add(new RouteStep(ocr.Converter, source, "pdf"));
        cost += ocr.Cost;
        if (to != "pdf")
        {
            if (Plan("pdf", to, shaping: shaping) is not { } rest)
                return null;
            steps.AddRange(rest.Steps);
            cost += rest.Cost;
        }
        return new Route(steps, cost);
    }

    private (IConverter Converter, ConversionEdge Edge, int Cost) OcrEdge(string from) =>
        EdgesFrom(from).Where(e => e.Edge.Ocr && e.Edge.To == "pdf").OrderBy(e => e.Cost).FirstOrDefault();

    private IReadOnlyList<(IConverter Converter, ConversionEdge Edge, int Cost)> EdgesFrom(string format) =>
        _edgesFrom.TryGetValue(format, out var edges) ? edges : [];
}
