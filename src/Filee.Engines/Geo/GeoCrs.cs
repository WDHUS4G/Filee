// Coordinate reference systems, just enough to put every file into longitude / latitude: geographic systems (degrees)
// and the projections most projected data uses (UTM and other Transverse Mercator, Mercator and Web Mercator, Lambert
// Conformal Conic, Albers) read from a .prj file (ESRI or OGC WKT) or an EPSG code. A system outside that list is
// refused with its name rather than guessed, because coordinates in the wrong system land on the wrong place on Earth.
// Datums are not shifted: WGS 84, NAD83, ETRS89 and the other systems tied to ITRF agree within a metre or two, and
// anything else is converted as if it were WGS 84 and the file gets a notice (see IsApproximate).

using System.Globalization;
using System.Text;
using Filee.Core.Conversion;

namespace Filee.Engines.Geo;

/// <summary>A coordinate system that can turn its positions into longitude / latitude in degrees.</summary>
internal abstract class GeoCrs
{
    private const double Degrees = 180 / Math.PI;

    /// <summary>The system of GeoJSON, KML and GPX.</summary>
    public static GeoCrs Wgs84 { get; } = new GeographicCrs("WGS 84", 1, 0, approximate: false);

    /// <summary>Name for messages, e.g. "NAD83 / UTM zone 18N".</summary>
    public string Name { get; private set; } = "";

    /// <summary>True for degrees of longitude / latitude (no projection).</summary>
    public virtual bool IsGeographic => false;

    /// <summary>True if positions are already longitude / latitude in degrees from Greenwich, so nothing needs converting.</summary>
    public virtual bool IsIdentity => false;

    /// <summary>
    /// True if the datum is not one that agrees with WGS 84 within a few metres, so positions are converted as if it
    /// were (they can be off by up to hundreds of metres).
    /// </summary>
    public bool IsApproximate { get; private set; }

    /// <summary>The position in degrees of longitude (X) and latitude (Y); the height is kept.</summary>
    public abstract Coord ToLonLat(Coord position);

    /// <summary>Reads a .prj / WKT definition; <see cref="ConversionRefusedException"/> if it is unknown or unsupported.</summary>
    public static GeoCrs FromWkt(string wkt)
    {
        if (wkt.Length > 1 << 16)
            throw Unsupported("(definition too long)");
        WktNode root;
        try
        {
            root = WktNode.Parse(wkt);
        }
        catch (FormatException ex)
        {
            throw Unsupported(ex.Message);
        }
        // WKT2 (ISO 19162) uses other keywords for the same things; PROJ writes it into .prj files only rarely.
        if (root.Name is "GEOGCRS" or "GEODCRS" or "PROJCRS" or "PROJECTEDCRS" or "BOUNDCRS" or "COMPOUNDCRS")
            throw Unsupported(root.Text(0) ?? root.Name);
        return root.Name switch
        {
            "GEOGCS" => FromGeographic(root),
            "PROJCS" => FromProjected(root),
            "COMPD_CS" => root.Child("PROJCS") is { } projected ? FromProjected(projected)
                : root.Child("GEOGCS") is { } geographic ? FromGeographic(geographic)
                : throw Unsupported(root.Text(0) ?? "COMPD_CS"),
            "GEOCCS" or "LOCAL_CS" or "VERT_CS" => throw Unsupported(root.Text(0) ?? root.Name),
            _ => throw Unsupported(root.Name),
        };
    }

    /// <summary>The system of an EPSG code, or null if it is not one Filee knows.</summary>
    public static GeoCrs? FromEpsg(int code)
    {
        switch (code)
        {
            case 4326:
                return Wgs84;
            case 4269 or 4258 or 4283 or 7844 or 4490 or 6318 or 4167 or 4612 or 6668 or 4674 or 4171 or 9777:
                // NAD83, ETRS89, GDA94, GDA2020, CGCS2000, NAD83(NSRS2007), NZGD2000, JGD2000, JGD2011, SIRGAS 2000, RGF93, Korea 2000
                return new GeographicCrs($"EPSG:{code}", 1, 0, approximate: false);
            case 3857 or 900913 or 3785 or 102100 or 102113:
                return new MercatorCrs("WGS 84 / Pseudo-Mercator", Ellipsoid.Sphere(6378137), 0, 1, 0, 0);
        }
        if (code is >= 32601 and <= 32660)
            return Utm(code, code - 32600, south: false, Ellipsoid.Wgs84, "WGS 84");
        if (code is >= 32701 and <= 32760)
            return Utm(code, code - 32700, south: true, Ellipsoid.Wgs84, "WGS 84");
        if (code is >= 26901 and <= 26923)
            return Utm(code, code - 26900, south: false, Ellipsoid.Grs80, "NAD83");
        if (code is >= 25828 and <= 25838)
            return Utm(code, code - 25800, south: false, Ellipsoid.Grs80, "ETRS89");
        return null;
    }

    /// <summary>A GeoJSON 2008 <c>crs</c> name such as "EPSG:4326" or "urn:ogc:def:crs:OGC:1.3:CRS84", or null.</summary>
    public static GeoCrs? FromName(string name)
    {
        var text = name.Trim();
        if (text.Contains("CRS84", StringComparison.OrdinalIgnoreCase) || text.Contains("CRS:84", StringComparison.OrdinalIgnoreCase))
            return Wgs84;
        var digits = text.Length - 1;
        while (digits >= 0 && char.IsAsciiDigit(text[digits]))
            digits--;
        return digits < text.Length - 1 && text.Contains("EPSG", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(text.AsSpan(digits + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            ? FromEpsg(code)
            : null;
    }

    internal static ConversionRefusedException Unsupported(string detail) => new("error.geo_unsupported_crs", detail);

    private static GeoCrs Utm(int code, int zone, bool south, Ellipsoid ellipsoid, string datum) =>
        new TransverseMercatorCrs($"{datum} / UTM zone {zone}{(south ? 'S' : 'N')} (EPSG:{code})", ellipsoid,
            latitudeOrigin: 0, centralMeridian: (zone * 6 - 183) / Degrees, scale: 0.9996,
            falseEasting: 500000, falseNorthing: south ? 10000000 : 0, unit: 1);

    private static GeoCrs FromGeographic(WktNode node)
    {
        var name = node.Text(0) ?? "geographic";
        var datum = node.Child("DATUM")?.Text(0) ?? name;
        var compatible = IsWgs84Compatible(datum, node.Child("DATUM")?.Child("SPHEROID"));
        var unit = node.Child("UNIT")?.Number(1) ?? Math.PI / 180;
        var meridian = node.Child("PRIMEM")?.Number(1) ?? 0;
        // Angles in radians (UNIT factor) → degrees. PRIMEM is in degrees from Greenwich whatever the unit of the system:
        // GDAL and ESRI both write the Paris meridian as 2.33722917 next to UNIT["Grad"], not as 2.5969 grads.
        var toDegrees = Math.Abs(unit * Degrees - 1) < 1e-9 ? 1 : unit * Degrees;
        return new GeographicCrs(name, toDegrees, meridian, approximate: !compatible);
    }

    private static GeoCrs FromProjected(WktNode node)
    {
        var name = node.Text(0) ?? "projected";
        var geographic = node.Child("GEOGCS") ?? throw Unsupported(name);
        var datum = geographic.Child("DATUM");
        var spheroid = datum?.Child("SPHEROID");
        if (spheroid is null || spheroid.Number(1) is not { } axis || axis <= 0)
            throw Unsupported($"{name} (no ellipsoid)");
        // The projection's own longitudes are counted from the prime meridian and its angles in the unit of the geographic
        // system. Neither is applied here, and ignoring them puts the data far from where it is (NTF Paris Lambert II is
        // 2.3 degrees west of its true place), so a system with another prime meridian or angular unit is refused.
        if (geographic.Child("PRIMEM") is { } primeMeridian && Math.Abs(primeMeridian.Number(1) ?? 0) > 1e-9)
            throw Unsupported($"{name} (prime meridian {primeMeridian.Text(0)} {primeMeridian.Number(1)?.ToString(CultureInfo.InvariantCulture)}°)");
        if (geographic.Child("UNIT") is { } angular && angular.Number(1) is { } angularFactor && Math.Abs(angularFactor * Degrees - 1) > 1e-9)
            throw Unsupported($"{name} (angular unit {angular.Text(0)})");
        var ellipsoid = Ellipsoid.FromInverseFlattening(axis, spheroid.Number(2) ?? 0);
        var approximate = !IsWgs84Compatible(datum?.Text(0) ?? geographic.Text(0) ?? "", spheroid);
        var unit = node.Children("UNIT").LastOrDefault()?.Number(1) ?? 1;
        if (unit <= 0)
            throw Unsupported($"{name} (unit)");
        var projection = node.Child("PROJECTION")?.Text(0) ?? throw Unsupported(name);
        var parameters = node.Children("PARAMETER").Where(p => p.Text(0) is not null && p.Number(1) is not null)
            .GroupBy(p => Key(p.Text(0)!)).ToDictionary(g => g.Key, g => g.First().Number(1)!.Value);

        double Get(string key, double fallback = 0) => parameters.TryGetValue(key, out var value) ? value : fallback;
        double Angle(string key) => Get(key) / Degrees;
        var falseEasting = Get("falseeasting") * unit;
        var falseNorthing = Get("falsenorthing") * unit;
        var centre = Angle("centralmeridian");
        var origin = Angle("latitudeoforigin");

        GeoCrs result;
        switch (Key(projection))
        {
            case "transversemercator" or "gausskruger" or "gausskrugerzone" or "tmerc":
                result = new TransverseMercatorCrs(name, ellipsoid, origin, centre, Get("scalefactor", 1), falseEasting, falseNorthing, unit);
                break;
            case "mercatorauxiliarysphere" or "popularvisualisationpseudomercator"
                or "popularvisualizationpseudomercator":
                result = new MercatorCrs(name, Ellipsoid.Sphere(axis), centre, 1, falseEasting, falseNorthing, unit);
                break;
            case "mercator" or "mercator1sp" or "mercator2sp":
                {
                    var sphere = name.Contains("Pseudo", StringComparison.OrdinalIgnoreCase) || name.Contains("Web_Mercator", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Web Mercator", StringComparison.OrdinalIgnoreCase);
                    var body = sphere ? Ellipsoid.Sphere(axis) : ellipsoid;
                    var k0 = Get("scalefactor", 1);
                    if (parameters.ContainsKey("standardparallel1"))
                        k0 = MercatorScale(body, Angle("standardparallel1"));
                    result = new MercatorCrs(name, body, centre, k0, falseEasting, falseNorthing, unit);
                    break;
                }
            case "lambertconformalconic" or "lambertconformalconic2sp" or "lambertconformalconic1sp" or "lambertconformalconicbelgium":
                {
                    var first = parameters.ContainsKey("standardparallel1") ? Angle("standardparallel1") : origin;
                    var second = parameters.ContainsKey("standardparallel2") ? Angle("standardparallel2") : first;
                    result = new LambertConformalCrs(name, ellipsoid, origin, centre, first, second, Get("scalefactor", 1), falseEasting, falseNorthing, unit);
                    break;
                }
            case "albersconicequalarea" or "albers" or "albersequalareaconic":
                result = new AlbersCrs(name, ellipsoid, origin, centre, Angle("standardparallel1"), Angle("standardparallel2"), falseEasting, falseNorthing, unit);
                break;
            default:
                throw Unsupported($"{name} ({projection})");
        }
        if (!approximate)
            return result;
        result.IsApproximate = true;
        return result;
    }

    private static double MercatorScale(Ellipsoid ellipsoid, double parallel)
    {
        var sin = Math.Sin(parallel);
        return Math.Cos(parallel) / Math.Sqrt(1 - ellipsoid.E2 * sin * sin);
    }

    private static string Key(string name) => new([.. name.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>Datums that agree with WGS 84 within about two metres (ITRF-based systems), by name.</summary>
    private static readonly string[] Compatible =
    [
        "WGS1984", "WGS84", "NORTHAMERICAN1983", "NAD83", "ETRS89", "ETRS1989", "EUROPEANTERRESTRIALREFERENCESYSTEM1989",
        "GRS1980", "KOREA2000", "CHINA2000", "CGCS2000", "CHINAGEODETICCOORDINATESYSTEM2000", "JGD2011", "JGD2000",
        "JAPANGEODETICDATUM", "GDA94", "GDA2020", "GEOCENTRICDATUMOFAUSTRALIA", "NZGD2000", "NEWZEALANDGEODETICDATUM2000",
        "SIRGAS2000", "SIRGAS", "RGF93", "RESEAUGEODESIQUEFRANCAIS1993", "ITRF", "HARN", "NSRS2007",
    ];

    /// <summary>
    /// True for a datum named in <see cref="Compatible"/>, or on the WGS 84 / GRS 80 ellipsoid: modern national datums
    /// (LKS94, RGF93, SWEREF99, ...) are all realisations of ITRF on GRS 80 and agree with WGS 84 within a metre or two.
    /// Older datums use other ellipsoids (Bessel, Airy, Clarke, International) and are shifted by tens to hundreds of metres.
    /// </summary>
    private static bool IsWgs84Compatible(string datum, WktNode? spheroid)
    {
        if (spheroid?.Number(1) is { } axis && spheroid.Number(2) is { } inverse && Math.Abs(axis - 6378137) < 0.01 && Math.Abs(inverse - 298.2572) < 0.001)
            return true;
        var key = Key(datum.StartsWith("D_", StringComparison.Ordinal) ? datum[2..] : datum).ToUpperInvariant();
        return key.Length > 0 && Compatible.Any(name => key.Contains(name, StringComparison.Ordinal));
    }

    // ---- Systems ----

    private sealed class GeographicCrs : GeoCrs
    {
        private readonly double _toDegrees;
        private readonly double _meridian;

        public GeographicCrs(string name, double toDegrees, double meridian, bool approximate)
        {
            Name = name;
            _toDegrees = toDegrees;
            _meridian = meridian;
            IsApproximate = approximate;
        }

        public override bool IsGeographic => true;

        public override bool IsIdentity => _toDegrees == 1 && _meridian == 0;

        public override Coord ToLonLat(Coord position) =>
            _toDegrees == 1 && _meridian == 0 ? position : new Coord(position.X * _toDegrees + _meridian, position.Y * _toDegrees, position.Z);
    }

    /// <summary>A projection given by the inverse formula that maps metres to radians (longitude, latitude).</summary>
    private abstract class ProjectedBase : GeoCrs
    {
        private readonly double _unit;

        protected ProjectedBase(string name, double unit)
        {
            Name = name;
            _unit = unit;
        }

        /// <summary>Longitude and latitude in radians of a position in metres.</summary>
        protected abstract (double Lon, double Lat) Inverse(double x, double y);

        public override Coord ToLonLat(Coord position)
        {
            var (lon, lat) = Inverse(position.X * _unit, position.Y * _unit);
            return new Coord(lon * Degrees, lat * Degrees, position.Z);
        }
    }

    private sealed class TransverseMercatorCrs : ProjectedBase
    {
        // Krüger's series in the third flattening n (Karney 2011), accurate to nanometres far from the central meridian.
        private readonly double _centre, _scale, _falseEasting, _falseNorthing, _radius, _originArc;
        private readonly double[] _beta = new double[7];
        private readonly double[] _delta = new double[7];

        public TransverseMercatorCrs(string name, Ellipsoid ellipsoid, double latitudeOrigin, double centralMeridian, double scale,
            double falseEasting, double falseNorthing, double unit) : base(name, unit)
        {
            _centre = centralMeridian;
            _scale = scale;
            _falseEasting = falseEasting;
            _falseNorthing = falseNorthing;
            var n = ellipsoid.Flattening / (2 - ellipsoid.Flattening);
            var n2 = n * n;
            var n3 = n2 * n;
            var n4 = n3 * n;
            var n5 = n4 * n;
            var n6 = n5 * n;
            _radius = ellipsoid.SemiMajor / (1 + n) * (1 + n2 / 4 + n4 / 64 + n6 / 256);
            _beta[1] = n / 2 - 2 * n2 / 3 + 37 * n3 / 96 - n4 / 360 - 81 * n5 / 512 + 96199 * n6 / 604800;
            _beta[2] = n2 / 48 + n3 / 15 - 437 * n4 / 1440 + 46 * n5 / 105 - 1118711 * n6 / 3870720;
            _beta[3] = 17 * n3 / 480 - 37 * n4 / 840 - 209 * n5 / 4480 + 5569 * n6 / 90720;
            _beta[4] = 4397 * n4 / 161280 - 11 * n5 / 504 - 830251 * n6 / 7257600;
            _beta[5] = 4583 * n5 / 161280 - 108847 * n6 / 3991680;
            _beta[6] = 20648693 * n6 / 638668800;
            _delta[1] = 2 * n - 2 * n2 / 3 - 2 * n3 + 116 * n4 / 45 + 26 * n5 / 45 - 2854 * n6 / 675;
            _delta[2] = 7 * n2 / 3 - 8 * n3 / 5 - 227 * n4 / 45 + 2704 * n5 / 315 + 2323 * n6 / 945;
            _delta[3] = 56 * n3 / 15 - 136 * n4 / 35 - 1262 * n5 / 105 + 73814 * n6 / 2835;
            _delta[4] = 4279 * n4 / 630 - 332 * n5 / 35 - 399572 * n6 / 14175;
            _delta[5] = 4174 * n5 / 315 - 144838 * n6 / 6237;
            _delta[6] = 601676 * n6 / 22275;
            _originArc = latitudeOrigin == 0 ? 0 : ellipsoid.MeridianArc(latitudeOrigin);
        }

        protected override (double Lon, double Lat) Inverse(double x, double y)
        {
            var xi = (y - _falseNorthing + _scale * _originArc) / (_scale * _radius);
            var eta = (x - _falseEasting) / (_scale * _radius);
            var xi2 = xi;
            var eta2 = eta;
            for (var j = 1; j <= 6; j++)
            {
                xi2 -= _beta[j] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
                eta2 -= _beta[j] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            }
            var chi = Math.Asin(Math.Sin(xi2) / Math.Cosh(eta2));
            var lat = chi;
            for (var j = 1; j <= 6; j++)
                lat += _delta[j] * Math.Sin(2 * j * chi);
            return (_centre + Math.Atan2(Math.Sinh(eta2), Math.Cos(xi2)), lat);
        }
    }

    private sealed class MercatorCrs : ProjectedBase
    {
        private readonly Ellipsoid _ellipsoid;
        private readonly double _centre, _scale, _falseEasting, _falseNorthing;

        public MercatorCrs(string name, Ellipsoid ellipsoid, double centralMeridian, double scale, double falseEasting, double falseNorthing,
            double unit = 1) : base(name, unit)
        {
            _ellipsoid = ellipsoid;
            _centre = centralMeridian;
            _scale = scale;
            _falseEasting = falseEasting;
            _falseNorthing = falseNorthing;
        }

        protected override (double Lon, double Lat) Inverse(double x, double y)
        {
            var a = _ellipsoid.SemiMajor * _scale;
            var t = Math.Exp(-(y - _falseNorthing) / a);
            var lat = Math.PI / 2 - 2 * Math.Atan(t);
            var e = _ellipsoid.E;
            if (e > 0)
            {
                for (var i = 0; i < 12; i++)
                {
                    var sin = e * Math.Sin(lat);
                    var next = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - sin) / (1 + sin), e / 2));
                    var done = Math.Abs(next - lat) < 1e-14;
                    lat = next;
                    if (done)
                        break;
                }
            }
            return (_centre + (x - _falseEasting) / a, lat);
        }
    }

    private sealed class LambertConformalCrs : ProjectedBase
    {
        private readonly Ellipsoid _ellipsoid;
        private readonly double _centre, _falseEasting, _falseNorthing, _n, _f, _rho0;

        public LambertConformalCrs(string name, Ellipsoid ellipsoid, double latitudeOrigin, double centralMeridian, double first, double second,
            double scale, double falseEasting, double falseNorthing, double unit) : base(name, unit)
        {
            _ellipsoid = ellipsoid;
            _centre = centralMeridian;
            _falseEasting = falseEasting;
            _falseNorthing = falseNorthing;
            var m1 = Math.Cos(first) / Math.Sqrt(1 - ellipsoid.E2 * Math.Pow(Math.Sin(first), 2));
            var t1 = T(first);
            if (Math.Abs(first - second) < 1e-12)
            {
                _n = Math.Sin(first);
            }
            else
            {
                var m2 = Math.Cos(second) / Math.Sqrt(1 - ellipsoid.E2 * Math.Pow(Math.Sin(second), 2));
                _n = (Math.Log(m1) - Math.Log(m2)) / (Math.Log(t1) - Math.Log(T(second)));
            }
            _f = ellipsoid.SemiMajor * scale * m1 / (_n * Math.Pow(t1, _n));
            _rho0 = _f * Math.Pow(T(latitudeOrigin), _n);
        }

        private double T(double lat)
        {
            var sin = _ellipsoid.E * Math.Sin(lat);
            return Math.Tan(Math.PI / 4 - lat / 2) / Math.Pow((1 - sin) / (1 + sin), _ellipsoid.E / 2);
        }

        protected override (double Lon, double Lat) Inverse(double x, double y)
        {
            var dx = x - _falseEasting;
            var dy = _rho0 - (y - _falseNorthing);
            var sign = Math.Sign(_n);
            var rho = sign * Math.Sqrt(dx * dx + dy * dy);
            var theta = Math.Atan2(sign * dx, sign * dy);
            var t = Math.Pow(rho / _f, 1 / _n);
            var lat = Math.PI / 2 - 2 * Math.Atan(t);
            var e = _ellipsoid.E;
            for (var i = 0; i < 12 && e > 0; i++)
            {
                var sin = e * Math.Sin(lat);
                var next = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - sin) / (1 + sin), e / 2));
                var done = Math.Abs(next - lat) < 1e-14;
                lat = next;
                if (done)
                    break;
            }
            return (_centre + theta / _n, lat);
        }
    }

    private sealed class AlbersCrs : ProjectedBase
    {
        private readonly Ellipsoid _ellipsoid;
        private readonly double _centre, _falseEasting, _falseNorthing, _n, _c, _rho0;

        public AlbersCrs(string name, Ellipsoid ellipsoid, double latitudeOrigin, double centralMeridian, double first, double second,
            double falseEasting, double falseNorthing, double unit) : base(name, unit)
        {
            _ellipsoid = ellipsoid;
            _centre = centralMeridian;
            _falseEasting = falseEasting;
            _falseNorthing = falseNorthing;
            var m1 = M(first);
            var q1 = Q(first);
            _n = Math.Abs(first - second) < 1e-12 ? Math.Sin(first) : (m1 * m1 - M(second) * M(second)) / (Q(second) - q1);
            _c = m1 * m1 + _n * q1;
            _rho0 = ellipsoid.SemiMajor * Math.Sqrt(Math.Max(0, _c - _n * Q(latitudeOrigin))) / _n;
        }

        private double M(double lat) => Math.Cos(lat) / Math.Sqrt(1 - _ellipsoid.E2 * Math.Pow(Math.Sin(lat), 2));

        private double Q(double lat)
        {
            var sin = Math.Sin(lat);
            var e = _ellipsoid.E;
            if (e < 1e-12)
                return 2 * sin;
            return (1 - _ellipsoid.E2) * (sin / (1 - _ellipsoid.E2 * sin * sin) - Math.Log((1 - e * sin) / (1 + e * sin)) / (2 * e));
        }

        protected override (double Lon, double Lat) Inverse(double x, double y)
        {
            var dx = x - _falseEasting;
            var dy = _rho0 - (y - _falseNorthing);
            var sign = Math.Sign(_n);
            var rho = Math.Sqrt(dx * dx + dy * dy);
            var theta = Math.Atan2(sign * dx, sign * dy);
            var a = _ellipsoid.SemiMajor;
            var q = (_c - rho * rho * _n * _n / (a * a)) / _n;
            var e = _ellipsoid.E;
            var lat = Math.Asin(Math.Clamp(q / 2, -1, 1));
            if (e > 1e-12)
            {
                var polar = Q(Math.PI / 2);
                if (Math.Abs(Math.Abs(q) - polar) < 1e-10)
                    return (_centre + theta / _n, Math.Sign(q) * Math.PI / 2);
                for (var i = 0; i < 20; i++)
                {
                    var sin = Math.Sin(lat);
                    var w = 1 - _ellipsoid.E2 * sin * sin;
                    var step = w * w / (2 * Math.Cos(lat))
                               * (q / (1 - _ellipsoid.E2) - sin / w + Math.Log((1 - e * sin) / (1 + e * sin)) / (2 * e));
                    lat += step;
                    if (Math.Abs(step) < 1e-14)
                        break;
                }
            }
            return (_centre + theta / _n, lat);
        }
    }

    // ---- Ellipsoid ----

    private sealed class Ellipsoid
    {
        public static readonly Ellipsoid Wgs84 = FromInverseFlattening(6378137, 298.257223563);
        public static readonly Ellipsoid Grs80 = FromInverseFlattening(6378137, 298.257222101);

        private Ellipsoid(double semiMajor, double flattening)
        {
            SemiMajor = semiMajor;
            Flattening = flattening;
            E2 = flattening * (2 - flattening);
            E = Math.Sqrt(E2);
        }

        public double SemiMajor { get; }
        public double Flattening { get; }
        public double E2 { get; }
        public double E { get; }

        public static Ellipsoid FromInverseFlattening(double semiMajor, double inverseFlattening) =>
            new(semiMajor, inverseFlattening > 1 ? 1 / inverseFlattening : 0);

        public static Ellipsoid Sphere(double radius) => new(radius, 0);

        /// <summary>The distance along the meridian from the equator to <paramref name="lat"/> (radians), in metres.</summary>
        public double MeridianArc(double lat)
        {
            var e4 = E2 * E2;
            var e6 = e4 * E2;
            return SemiMajor * ((1 - E2 / 4 - 3 * e4 / 64 - 5 * e6 / 256) * lat
                                - (3 * E2 / 8 + 3 * e4 / 32 + 45 * e6 / 1024) * Math.Sin(2 * lat)
                                + (15 * e4 / 256 + 45 * e6 / 1024) * Math.Sin(4 * lat)
                                - 35 * e6 / 3072 * Math.Sin(6 * lat));
        }
    }
}

/// <summary>A node of a WKT definition: <c>NAME[arg, arg, NAME[...]]</c>; arguments are text, numbers or nodes.</summary>
internal sealed class WktNode(string name, List<object> items)
{
    private const int MaxDepth = 24;

    public string Name { get; } = name;
    public List<object> Items { get; } = items;

    /// <summary>The quoted text or word at <paramref name="index"/>, or null.</summary>
    public string? Text(int index) => index < Items.Count ? Items[index] as string : null;

    /// <summary>The number at <paramref name="index"/>, or null.</summary>
    public double? Number(int index) => index < Items.Count && Items[index] is double value ? value : null;

    public WktNode? Child(string name) => Children(name).FirstOrDefault();

    public IEnumerable<WktNode> Children(string name) =>
        Items.OfType<WktNode>().Where(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses a definition; <see cref="FormatException"/> says what is wrong with it.</summary>
    public static WktNode Parse(string text)
    {
        var position = 0;
        var node = ReadNode(text, ref position, 0) ?? throw new FormatException("empty definition");
        return node;
    }

    private static WktNode? ReadNode(string text, ref int position, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("definition nested too deeply");
        SkipSpace(text, ref position);
        var start = position;
        while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_'))
            position++;
        if (position == start)
            return null;
        var name = text[start..position].ToUpperInvariant();
        SkipSpace(text, ref position);
        var items = new List<object>();
        if (position >= text.Length || text[position] is not ('[' or '('))
            return new WktNode(name, items);
        var close = text[position] == '[' ? ']' : ')';
        position++;
        while (true)
        {
            SkipSpace(text, ref position);
            if (position >= text.Length)
                throw new FormatException("missing closing bracket");
            var ch = text[position];
            if (ch == close)
            {
                position++;
                return new WktNode(name, items);
            }
            if (ch == ',')
            {
                position++;
            }
            else if (ch == '"')
            {
                var builder = new StringBuilder();
                position++;
                while (true)
                {
                    if (position >= text.Length)
                        throw new FormatException("missing closing quote");
                    if (text[position] == '"')
                    {
                        if (position + 1 < text.Length && text[position + 1] == '"')
                        {
                            builder.Append('"');
                            position += 2;
                            continue;
                        }
                        position++;
                        break;
                    }
                    builder.Append(text[position++]);
                }
                items.Add(builder.ToString());
            }
            else if (char.IsAsciiDigit(ch) || ch is '-' or '+' or '.')
            {
                var from = position;
                while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] is '-' or '+' or '.' or 'e' or 'E'))
                    position++;
                if (!double.TryParse(text.AsSpan(from, position - from), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new FormatException($"bad number '{text[from..position]}'");
                items.Add(number);
            }
            else if (ReadNode(text, ref position, depth + 1) is { } child)
            {
                // A word with no brackets, like the axis direction NORTH, is kept as text.
                items.Add(child.Items.Count == 0 ? child.Name : child);
            }
            else
            {
                throw new FormatException($"unexpected '{ch}'");
            }
        }
    }

    private static void SkipSpace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
    }
}
