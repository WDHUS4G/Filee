// Geospatial vector data in managed code: GeoJSON, KML, KMZ, GPX, Shapefile and CSV (a WKT column, or longitude and
// latitude columns) into each other through one in-memory model (GeoModel). Whatever the source's coordinate system
// (a .prj or a GeoJSON crs), every result is longitude / latitude in WGS 84. See docs/ENGINES.md, "Geospatial data",
// for what each format keeps and what it loses.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines.Geo;

/// <summary>Converts GeoJSON, KML, KMZ, GPX, Shapefile and CSV points / WKT into each other.</summary>
public sealed class GeoConverter(ILogger? logger = null) : IConverter
{
    /// <summary>The geospatial formats, each readable and writable.</summary>
    public static readonly string[] Formats = ["geojson", "kml", "kmz", "gpx", "shp"];

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Id => "geo";
    public string DisplayName => "Geospatial vector data (built-in)";

    /// <summary>Whole files are read into memory, so only a few run at once.</summary>
    public int MaxParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in Formats from target in Formats where source != target select new ConversionEdge(source, target),
        // The same format again rewrites the file in WGS 84: a projected Shapefile or a GeoJSON with an old "crs" member
        // becomes longitude / latitude, and a Shapefile with mixed geometries is split by kind.
        new("shp", "shp"),
        new("geojson", "geojson"),
        .. Formats.Select(format => new ConversionEdge(format, "csv")),
        .. Formats.Select(format => new ConversionEdge("csv", format)),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available("GeoJSON, KML, KMZ, GPX, Shapefile ↔ each other and CSV",
        EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var notices = new Notices(step.Notice);
            var dataset = Read(step, cancellationToken);
            progress?.Report(0.4);
            ToWgs84(dataset, notices);
            if (dataset.Features.Count == 0)
                throw new ConversionRefusedException("error.geo_empty");
            _logger.LogDebug("{File}: {Features} features read as {Format}, {Crs}", step.InputPath, dataset.Features.Count, step.From, dataset.Crs.Name);
            if (dataset.RowsWithoutLocation)
                notices.Add("notice.geo_rows_without_location");
            progress?.Report(0.6);

            var outputs = new List<string>();
            try
            {
                Write(dataset, step, outputs, notices, cancellationToken);
            }
            catch
            {
                foreach (var output in outputs)
                    TryDelete(output);
                throw;
            }
            notices.Report();
            progress?.Report(1);
            return outputs;
        }, cancellationToken);

    private static GeoDataset Read(ConversionStep step, CancellationToken cancellationToken) => step.From switch
    {
        "geojson" => GeoJsonFormat.Read(step.InputPath, cancellationToken),
        "kml" => KmlFormat.Read(step.InputPath, zipped: false, cancellationToken),
        "kmz" => KmlFormat.Read(step.InputPath, zipped: true, cancellationToken),
        "gpx" => GpxFormat.Read(step.InputPath, cancellationToken),
        "shp" => ShapefileFormat.Read(step.InputPath, cancellationToken),
        "csv" => GeoCsvFormat.Read(step.InputPath, cancellationToken),
        _ => throw new NotSupportedException($"{step.From} is not a geospatial format."),
    };

    /// <summary>
    /// Puts every position into longitude / latitude, and refuses data whose positions can't be: coordinates outside the
    /// Earth with no coordinate system to explain them are most likely a projected system the file doesn't name.
    /// </summary>
    internal static void ToWgs84(GeoDataset dataset, Notices notices)
    {
        var crs = dataset.Crs;
        if (crs.IsApproximate)
            notices.Add("notice.geo_datum_assumed");
        var features = dataset.Features;
        for (var i = 0; i < features.Count; i++)
        {
            if (features[i].Geometry is not { } geometry)
                continue;
            var converted = crs.IsIdentity ? geometry : geometry.Map(crs.ToLonLat);
            if (!InRange(converted))
            {
                throw dataset.CrsDeclared && !crs.IsGeographic
                    ? new ConversionRefusedException("error.geo_bad_coordinates", crs.Name)
                    : new ConversionRefusedException("error.geo_unknown_crs", dataset.CrsDeclared ? crs.Name : null);
            }
            if (!ReferenceEquals(converted, geometry))
                features[i] = new GeoFeature(converted, features[i].Properties);
        }
        dataset.Crs = GeoCrs.Wgs84;
    }

    private static bool InRange(GeoGeometry geometry)
    {
        foreach (var c in geometry.Coordinates())
        {
            if (!c.IsFinite || c.Y is < -90.000001 or > 90.000001 || c.X is < -360 or > 360)
                return false;
        }
        return true;
    }

    private static void Write(GeoDataset dataset, ConversionStep step, List<string> outputs, Notices notices, CancellationToken cancellationToken)
    {
        switch (step.To)
        {
            case "geojson" or "kml" or "kmz" or "gpx" or "csv":
                {
                    if (step.Output.Allocate(step.To) is not { } path)
                        return;
                    outputs.Add(path);
                    switch (step.To)
                    {
                        case "geojson":
                            GeoJsonFormat.Write(dataset, path, cancellationToken);
                            break;
                        case "kml" or "kmz":
                            KmlFormat.Write(dataset, path, zipped: step.To == "kmz", cancellationToken);
                            break;
                        case "gpx":
                            if (GpxFormat.Write(dataset, path, cancellationToken))
                                notices.Add("notice.geo_gpx_lossy");
                            break;
                        default:
                            GeoCsvFormat.Write(dataset, path, cancellationToken);
                            break;
                    }
                    break;
                }
            case "shp":
                WriteShapefiles(dataset, step, outputs, notices, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"{step.To} is not a geospatial format.");
        }
    }

    /// <summary>One shapefile per kind of geometry; each is the .shp with its .shx, .dbf, .prj and .cpg.</summary>
    private static void WriteShapefiles(GeoDataset dataset, ConversionStep step, List<string> outputs, Notices notices, CancellationToken cancellationToken)
    {
        var groups = ShapefileFormat.Plan(dataset);
        if (groups.Count > 1)
            notices.Add("notice.geo_shapefile_split");
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = groups.Count == 1 ? null : group.Kind switch
            {
                ShapeKind.Point => "_points",
                ShapeKind.Line => "_lines",
                _ => "_polygons",
            };
            if (AllocateShapefile(step.Output, suffix) is not { } shp)
                continue;
            string[] files = [shp, .. Siblings(shp)];
            outputs.AddRange(files);
            var losses = ShapefileFormat.Write(group, shp, cancellationToken);
            if (losses.TextCut)
                notices.Add("notice.geo_text_cut");
            if (losses.ColumnsDropped)
                notices.Add("notice.geo_columns_dropped");
            if (losses.NamesChanged)
                notices.Add("notice.geo_field_names");
        }
    }

    private static readonly string[] SiblingExtensions = ["shx", "dbf", "prj", "cpg"];

    private static string[] Siblings(string shp) => [.. SiblingExtensions.Select(e => Path.ChangeExtension(shp, e))];

    /// <summary>
    /// The path for a .shp whose sibling files (same name, other extensions) are all free. The allocator only knows the
    /// .shp, so when an old .dbf or .prj of the same name is lying around the next name is tried, rather than
    /// writing over files that don't belong to this shapefile. When the .shp itself is being overwritten (the user's
    /// choice), its siblings are overwritten with it.
    /// </summary>
    private static string? AllocateShapefile(IOutputAllocator output, string? suffix)
    {
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var path = output.Allocate("shp", attempt == 1 ? suffix : $"{suffix}_{attempt}");
            if (path is null)
                return null;
            if (File.Exists(path) || Siblings(path).All(sibling => !File.Exists(sibling)))
                return path;
        }
        throw new IOException("No free name for the shapefile.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The notes a conversion collects. A file carries one note, so the most important one is reported:
    /// the assumed datum can move data by hundreds of metres, the rest only say what a format could not hold.
    /// </summary>
    internal sealed class Notices(Action<string>? sink)
    {
        private static readonly string[] Priority =
        [
            "notice.geo_datum_assumed", "notice.geo_gpx_lossy", "notice.geo_text_cut", "notice.geo_columns_dropped",
            "notice.geo_rows_without_location", "notice.geo_field_names", "notice.geo_shapefile_split",
        ];

        private readonly HashSet<string> _keys = [];

        public void Add(string key) => _keys.Add(key);

        public IReadOnlyCollection<string> Keys => _keys;

        public void Report()
        {
            if (sink is null)
                return;
            var key = Priority.FirstOrDefault(_keys.Contains);
            if (key is not null)
                sink(key);
        }
    }
}
