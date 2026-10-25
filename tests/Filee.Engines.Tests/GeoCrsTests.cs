// Coordinate systems of the geospatial engine against PROJ: the .prj definitions GDAL writes (ESRI WKT) and EPSG codes
// must put a projected position within a millimetre of where PROJ says it is; systems Filee can't convert are refused
// with their name; the datum rules; and the WKT / WKT-geometry parsers' errors. The reference positions below come
// from pyproj 3.x (PROJ) for the longitude / latitude next to them.

using Filee.Core.Conversion;
using Filee.Engines.Geo;

namespace Filee.Engines.Tests;

public class GeoCrsTests
{
    internal sealed record Case(int Code, string Wkt, double X, double Y, double Lon, double Lat);

    /// <summary>Systems whose datum PROJ shifts and Filee doesn't: the position is only right to a few hundred metres.</summary>
    private static readonly int[] ShiftedByProj = [27700];

    internal static readonly Case[] Cases =
    [
        // Vienna, UTM 33N
        new(32633, """PROJCS["WGS_1984_UTM_Zone_33N",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",500000.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",15.0],PARAMETER["Scale_Factor",0.9996],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]""", 602065.2074110014, 5340353.594303575, 16.3738, 48.2082),
        // Cape Town, UTM 33S (false northing)
        new(32733, """PROJCS["WGS_1984_UTM_Zone_33S",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",500000.0],PARAMETER["False_Northing",10000000.0],PARAMETER["Central_Meridian",15.0],PARAMETER["Scale_Factor",0.9996],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]""", 816557.7955966464, 6240887.995569803, 18.4241, -33.9249),
        // Tokyo, Web Mercator
        new(3857, """PROJCS["WGS_1984_Web_Mercator_Auxiliary_Sphere",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Mercator_Auxiliary_Sphere"],PARAMETER["False_Easting",0.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",0.0],PARAMETER["Standard_Parallel_1",0.0],PARAMETER["Auxiliary_Sphere_Type",0.0],UNIT["Meter",1.0]]""", 15550408.912046732, 4257980.732184108, 139.6917, 35.6895),
        // Paris, Lambert-93 (Lambert Conformal Conic, two parallels)
        new(2154, """PROJCS["RGF_1993_Lambert_93",GEOGCS["GCS_RGF_1993",DATUM["D_RGF_1993",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",700000.0],PARAMETER["False_Northing",6600000.0],PARAMETER["Central_Meridian",3.0],PARAMETER["Standard_Parallel_1",49.0],PARAMETER["Standard_Parallel_2",44.0],PARAMETER["Latitude_Of_Origin",46.5],UNIT["Meter",1.0]]""", 652469.0227091359, 6862035.259420077, 2.3522, 48.8566),
        // Chicago, Albers CONUS
        new(5070, """PROJCS["NAD_1983_Contiguous_USA_Albers",GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Albers"],PARAMETER["False_Easting",0.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",-96.0],PARAMETER["Standard_Parallel_1",29.5],PARAMETER["Standard_Parallel_2",45.5],PARAMETER["Latitude_Of_Origin",23.0],UNIT["Meter",1.0]]""", 688894.5442286755, 2127632.7534185336, -87.6298, 41.8781),
        // New York, Long Island state plane in US survey feet
        new(2263, """PROJCS["NAD_1983_StatePlane_New_York_Long_Island_FIPS_3104_Feet",GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",984250.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",-74.0],PARAMETER["Standard_Parallel_1",41.0333333333333],PARAMETER["Standard_Parallel_2",40.6666666666667],PARAMETER["Latitude_Of_Origin",40.1666666666667],UNIT["US survey foot",0.304800609601219]]""", 988212.237182985, 211939.27856831398, -73.9857, 40.7484),
        // Sydney, World Mercator (ellipsoidal)
        new(3395, """PROJCS["WGS_1984_World_Mercator",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Mercator"],PARAMETER["False_Easting",0.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",0.0],PARAMETER["Standard_Parallel_1",0.0],UNIT["Meter",1.0]]""", 16832542.27920734, -3987387.019596725, 151.2093, -33.8688),
        // Vilnius, LKS94 (Transverse Mercator, scale 0.9998)
        new(3346, """PROJCS["LKS_1994_Lithuania_TM",GEOGCS["GCS_LKS_1994",DATUM["D_Lithuania_1994",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",500000.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",24.0],PARAMETER["Scale_Factor",0.9998],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]""", 582510.367297761, 6061948.683017084, 25.2797, 54.6872),
        // Zurich, ETRS89 UTM 32N
        new(25832, """PROJCS["ETRS_1989_UTM_Zone_32N",GEOGCS["GCS_ETRS_1989",DATUM["D_ETRS_1989",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",500000.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",9.0],PARAMETER["Scale_Factor",0.9996],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]""", 465403.2844656688, 5247150.839302885, 8.5417, 47.3769),
        // London, British National Grid (Airy; PROJ applies a datum shift Filee doesn't)
        new(27700, """PROJCS["British_National_Grid",GEOGCS["GCS_OSGB_1936",DATUM["D_OSGB_1936",SPHEROID["Airy_1830",6377563.396,299.3249646]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",400000.0],PARAMETER["False_Northing",-100000.0],PARAMETER["Central_Meridian",-2.0],PARAMETER["Scale_Factor",0.9996012717],PARAMETER["Latitude_Of_Origin",49.0],UNIT["Meter",1.0]]""", 530043.1949814068, 180358.20862000342, -0.1276, 51.5072),
        // Montpelier, Vermont state plane (Transverse Mercator, latitude of origin 42.5, scale 0.999964)
        new(32145, """PROJCS["NAD_1983_StatePlane_Vermont_FIPS_4400",GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",500000.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",-72.5],PARAMETER["Scale_Factor",0.999964286],PARAMETER["Latitude_Of_Origin",42.5],UNIT["Meter",1.0]]""", 493787.50490179786, 195543.33416470452, -72.5778, 44.2601),
        // Singapore, SVY21 (Transverse Mercator, latitude of origin 1.3667)
        new(3414, """PROJCS["SVY21_Singapore_TM",GEOGCS["GCS_SVY21",DATUM["D_SVY21",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",28001.642],PARAMETER["False_Northing",38744.572],PARAMETER["Central_Meridian",103.833333333333],PARAMETER["Scale_Factor",1.0],PARAMETER["Latitude_Of_Origin",1.36666666666667],UNIT["Meter",1.0]]""", 26495.534871029464, 37133.86847645403, 103.8198, 1.3521),
        // Mercator with a standard parallel at 41 degrees south
        new(3994, """PROJCS["WGS_1984_Mercator_41",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Mercator"],PARAMETER["False_Easting",0.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",100.0],PARAMETER["Standard_Parallel_1",-41.0],UNIT["Meter",1.0]]""", -6730814.809125746, -6320521.100279815, 20.0, -60.0),
        // Alice Springs area, Australian Albers (southern hemisphere)
        new(3577, """PROJCS["GDA_1994_Australia_Albers",GEOGCS["GCS_GDA_1994",DATUM["D_GDA_1994",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Albers"],PARAMETER["False_Easting",0.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",132.0],PARAMETER["Standard_Parallel_1",-18.0],PARAMETER["Standard_Parallel_2",-36.0],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]""", 176715.9535210848, -2734549.475872395, 133.7751, -25.2744),
        // Melbourne, Vicgrid (Lambert Conformal Conic in the south)
        new(3111, """PROJCS["GDA_1994_VICGRID94",GEOGCS["GCS_GDA_1994",DATUM["D_GDA_1994",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",2500000.0],PARAMETER["False_Northing",2500000.0],PARAMETER["Central_Meridian",145.0],PARAMETER["Standard_Parallel_1",-36.0],PARAMETER["Standard_Parallel_2",-38.0],PARAMETER["Latitude_Of_Origin",-37.0],UNIT["Meter",1.0]]""", 2496750.963165419, 2409712.430055314, 144.9631, -37.8136),
    ];

    public static TheoryData<int> Codes => [.. Cases.Select(c => c.Code)];

    private static double Metres(double lon1, double lat1, double lon2, double lat2)
    {
        // Haversine on the WGS 84 mean radius: good to a few metres, and the tolerances below are far larger or far smaller.
        const double r = 6371008.8;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Sqrt(a));
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Prj_definition_converts_a_position_like_PROJ(int code)
    {
        var c = Cases.Single(x => x.Code == code);
        var crs = GeoCrs.FromWkt(c.Wkt);
        var position = crs.ToLonLat(new Coord(c.X, c.Y));
        if (ShiftedByProj.Contains(code))
        {
            Assert.True(crs.IsApproximate, "a datum Filee doesn't shift is reported");
            Assert.InRange(Metres(position.X, position.Y, c.Lon, c.Lat), 0, 300);
            return;
        }
        Assert.False(crs.IsApproximate);
        // 1e-8 degrees is about a millimetre; the formulas agree with PROJ far closer than that.
        Assert.Equal(c.Lon, position.X, 8);
        Assert.Equal(c.Lat, position.Y, 8);
    }

    [Theory]
    [InlineData(32633)]
    [InlineData(32733)]
    [InlineData(3857)]
    [InlineData(25832)]
    public void Epsg_code_converts_a_position_like_PROJ(int code)
    {
        var c = Cases.Single(x => x.Code == code);
        var crs = GeoCrs.FromEpsg(code)!;
        var position = crs.ToLonLat(new Coord(c.X, c.Y));
        Assert.Equal(c.Lon, position.X, 8);
        Assert.Equal(c.Lat, position.Y, 8);
    }

    [Fact]
    public void Height_is_kept_and_a_missing_height_stays_missing()
    {
        var crs = GeoCrs.FromEpsg(32633)!;
        Assert.Equal(123.5, crs.ToLonLat(new Coord(602065.2074110014, 5340353.594303575, 123.5)).Z);
        Assert.False(crs.ToLonLat(new Coord(602065.2074110014, 5340353.594303575)).HasZ);
    }

    [Theory]
    [InlineData("urn:ogc:def:crs:OGC:1.3:CRS84", true)]
    [InlineData("EPSG:4326", true)]
    [InlineData("urn:ogc:def:crs:EPSG::4326", true)]
    [InlineData("urn:ogc:def:crs:EPSG::3857", true)]
    [InlineData("EPSG:32633", true)]
    [InlineData("EPSG:3035", false)]
    [InlineData("EPSG:", false)]
    [InlineData("something else", false)]
    public void Crs_names_of_GeoJSON_are_understood_or_not(string name, bool known) =>
        Assert.Equal(known, GeoCrs.FromName(name) is not null);

    [Fact]
    public void Geographic_system_in_grads_with_the_Paris_meridian_is_converted_to_degrees_from_Greenwich()
    {
        // As GDAL / ESRI write EPSG:4807: the prime meridian in degrees (Paris is 2.33722917 degrees east), the unit grads.
        var crs = GeoCrs.FromWkt("""GEOGCS["GCS_NTF_Paris",DATUM["Nouvelle_Triangulation_Francaise_(Paris)",SPHEROID["Clarke_1880_IGN",6378249.2,293.466021293627]],PRIMEM["Paris",2.33722917],UNIT["Grad",0.0157079632679489]]""");
        var position = crs.ToLonLat(new Coord(0, 50));
        Assert.Equal(2.33722917, position.X, 9);
        Assert.Equal(45, position.Y, 9);
        Assert.Equal(2.33722917 + 10 * 0.9, crs.ToLonLat(new Coord(10, 50)).X, 9);
        Assert.True(crs.IsApproximate);
    }

    [Fact]
    public void A_projected_system_with_another_prime_meridian_or_angular_unit_is_refused_not_placed_far_away()
    {
        // ESRI's NTF Paris Lambert Zone II (EPSG:27572): its central meridian 0 is Paris, so ignoring PRIMEM puts
        // everything 2.3 degrees (170 km) west of where it is.
        const string paris = """PROJCS["NTF_Paris_Lambert_Zone_II",GEOGCS["GCS_NTF_Paris",DATUM["Nouvelle_Triangulation_Francaise_(Paris)",SPHEROID["Clarke_1880_IGN",6378249.2,293.466021293627]],PRIMEM["Paris",2.33722917],UNIT["Grad",0.0157079632679489]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",600000.0],PARAMETER["False_Northing",2200000.0],PARAMETER["Central_Meridian",0.0],PARAMETER["Standard_Parallel_1",52.0],PARAMETER["Scale_Factor",0.99987742],PARAMETER["Latitude_Of_Origin",52.0],UNIT["Meter",1.0]]""";
        var error = Assert.Throws<ConversionRefusedException>(() => GeoCrs.FromWkt(paris));
        Assert.Equal("error.geo_unsupported_crs", error.ErrorKey);
        Assert.Contains("NTF_Paris_Lambert_Zone_II", error.Detail);
        Assert.Contains("prime meridian", error.Detail);

        // Greenwich, but angles in grads.
        var grads = paris.Replace("PRIMEM[\"Paris\",2.33722917]", "PRIMEM[\"Greenwich\",0.0]");
        var gradError = Assert.Throws<ConversionRefusedException>(() => GeoCrs.FromWkt(grads));
        Assert.Contains("angular unit Grad", gradError.Detail);

        // Another meridian in degrees (Ferro-like) is refused too; Greenwich in degrees still works.
        var ferro = """PROJCS["Test",GEOGCS["g",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Ferro",-17.6666667],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["Central_Meridian",31.0],UNIT["Meter",1.0]]""";
        Assert.Contains("prime meridian Ferro", Assert.Throws<ConversionRefusedException>(() => GeoCrs.FromWkt(ferro)).Detail);
        Assert.NotNull(GeoCrs.FromWkt(ferro.Replace("PRIMEM[\"Ferro\",-17.6666667]", "PRIMEM[\"Greenwich\",0.0]")));
    }

    [Theory]
    [InlineData("""GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]""", false)]
    [InlineData("""GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]""", false)]
    [InlineData("""GEOGCS["GCS_Korea_2000",DATUM["D_Korea_2000",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]""", false)]
    [InlineData("""GEOGCS["GCS_Korean_Datum_1985",DATUM["D_Korean_Datum_1985",SPHEROID["Bessel_1841",6377397.155,299.1528128]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]""", true)]
    [InlineData("""GEOGCS["GCS_European_1950",DATUM["D_European_1950",SPHEROID["International_1924",6378388.0,297.0]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]""", true)]
    [InlineData("""GEOGCS["GCS_North_American_1927",DATUM["D_North_American_1927",SPHEROID["Clarke_1866",6378206.4,294.9786982]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]""", true)]
    public void A_datum_that_is_not_WGS84_compatible_is_marked_approximate(string wkt, bool approximate)
    {
        var crs = GeoCrs.FromWkt(wkt);
        Assert.True(crs.IsGeographic);
        Assert.Equal(approximate, crs.IsApproximate);
    }

    [Theory]
    // Lambert azimuthal equal-area (Europe), polar stereographic, oblique stereographic: projections Filee doesn't have.
    [InlineData("""PROJCS["ETRS89_LAEA_Europe",GEOGCS["GCS_ETRS_1989",DATUM["D_ETRS_1989",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Azimuthal_Equal_Area"],PARAMETER["False_Easting",4321000.0],PARAMETER["False_Northing",3210000.0],PARAMETER["Central_Meridian",10.0],PARAMETER["Latitude_Of_Origin",52.0],UNIT["Meter",1.0]]""", "Lambert_Azimuthal_Equal_Area")]
    [InlineData("""PROJCS["WGS_1984_UPS_North",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Stereographic"],PARAMETER["False_Easting",2000000.0],UNIT["Meter",1.0]]""", "Stereographic")]
    // WKT2 and geocentric / local / vertical systems.
    [InlineData("""PROJCRS["WGS 84 / UTM zone 33N",BASEGEOGCRS["WGS 84"],CONVERSION["UTM zone 33N"]]""", "WGS 84 / UTM zone 33N")]
    [InlineData("""GEOCCS["WGS 84 (geocentric)",DATUM["WGS_1984",SPHEROID["WGS_1984",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["metre",1]]""", "WGS 84 (geocentric)")]
    [InlineData("""LOCAL_CS["Site grid",UNIT["metre",1]]""", "Site grid")]
    [InlineData("""PROJCS["No ellipsoid",GEOGCS["x",DATUM["d"]],PROJECTION["Transverse_Mercator"]]""", "No ellipsoid")]
    [InlineData("""PROJCS["No projection",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]]]]""", "No projection")]
    [InlineData("not a definition at all", "NOT")]
    [InlineData("GEOGCS[\"unclosed\"", "missing closing bracket")]
    [InlineData("GEOGCS[\"unterminated string]", "missing closing quote")]
    [InlineData("", "empty definition")]
    public void A_system_Filee_cannot_convert_is_refused_with_its_name(string wkt, string expectedInDetail)
    {
        var error = Assert.Throws<ConversionRefusedException>(() => GeoCrs.FromWkt(wkt));
        Assert.Equal("error.geo_unsupported_crs", error.ErrorKey);
        Assert.Contains(expectedInDetail, error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_definition_nested_far_too_deep_is_refused_instead_of_overflowing_the_stack()
    {
        var wkt = string.Concat(Enumerable.Repeat("A[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000));
        var error = Assert.Throws<ConversionRefusedException>(() => GeoCrs.FromWkt(wkt));
        Assert.Contains("nested", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_definition_is_refused()
    {
        var error = Assert.Throws<ConversionRefusedException>(() => GeoCrs.FromWkt("GEOGCS[\"" + new string('x', 100_000) + "\"]"));
        Assert.Equal("error.geo_unsupported_crs", error.ErrorKey);
    }

    [Fact]
    public void Text_in_a_definition_can_hold_doubled_quotes_and_unusual_spacing()
    {
        var crs = GeoCrs.FromWkt("  GEOGCS [ \"My \"\"quoted\"\" system\" , DATUM [ \"D_WGS_1984\" , SPHEROID [ \"WGS_1984\" , 6378137.0 , 298.257223563 ] ] , UNIT [ \"Degree\" , 0.0174532925199433 ] ]  ");
        Assert.Equal("My \"quoted\" system", crs.Name);
        Assert.True(crs.IsIdentity);
    }

    [Fact]
    public void Random_text_never_throws_anything_but_a_refusal()
    {
        var random = new Random(7);
        const string alphabet = "GEOCSPROJ[]\",.0123456789-+eE _ABCDHMNTU()";
        for (var i = 0; i < 3000; i++)
        {
            var text = new string([.. Enumerable.Range(0, random.Next(0, 120)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            try
            {
                GeoCrs.FromWkt(text);
            }
            catch (ConversionRefusedException)
            {
            }
        }
    }
}

public class GeoWktTests
{
    private static string RoundTrip(string wkt) => WktFormat.Write(WktFormat.Parse(wkt)!);

    [Theory]
    [InlineData("POINT (1 2)", "POINT (1 2)")]
    [InlineData("point(1.5 -2.25)", "POINT (1.5 -2.25)")]
    [InlineData("POINT Z (1 2 3)", "POINT Z (1 2 3)")]
    [InlineData("POINTZ(1 2 3)", "POINT Z (1 2 3)")]
    [InlineData("POINT ZM (1 2 3 4)", "POINT Z (1 2 3)")]
    [InlineData("POINT M (1 2 9)", "POINT (1 2)")]
    [InlineData("SRID=4326;POINT (1 2)", "POINT (1 2)")]
    [InlineData("LINESTRING (0 0, 1 1, 2 0)", "LINESTRING (0 0, 1 1, 2 0)")]
    [InlineData("POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 2, 2 2, 2 1, 1 1))", "POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 2, 2 2, 2 1, 1 1))")]
    [InlineData("POLYGON ((0 0, 0 4, 4 4, 4 0, 0 0))", "POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0))")] // a clockwise outer ring is written counter-clockwise
    [InlineData("POLYGON ((0 0, 4 0, 4 4, 0 4))", "POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0))")] // an open ring is closed
    [InlineData("MULTIPOINT ((1 2), (3 4))", "MULTIPOINT ((1 2), (3 4))")]
    [InlineData("MULTIPOINT (1 2, 3 4)", "MULTIPOINT ((1 2), (3 4))")]
    [InlineData("MULTILINESTRING ((0 0, 1 1), (2 2, 3 3))", "MULTILINESTRING ((0 0, 1 1), (2 2, 3 3))")]
    [InlineData("MULTIPOLYGON (((0 0, 1 0, 1 1, 0 1, 0 0)), ((5 5, 6 5, 6 6, 5 6, 5 5)))", "MULTIPOLYGON (((0 0, 1 0, 1 1, 0 1, 0 0)), ((5 5, 6 5, 6 6, 5 6, 5 5)))")]
    [InlineData("GEOMETRYCOLLECTION (POINT (1 2), LINESTRING (0 0, 1 1))", "GEOMETRYCOLLECTION (POINT (1 2), LINESTRING (0 0, 1 1))")]
    public void Wkt_is_read_and_written_in_a_canonical_form(string wkt, string? expected)
    {
        if (expected is null)
        {
            Assert.Throws<FormatException>(() => WktFormat.Parse(wkt));
            return;
        }
        Assert.Equal(expected, RoundTrip(wkt));
    }

    [Theory]
    [InlineData("POINT EMPTY")]
    [InlineData("LINESTRING EMPTY")]
    [InlineData("POLYGON EMPTY")]
    [InlineData("MULTIPOLYGON EMPTY")]
    [InlineData("GEOMETRYCOLLECTION EMPTY")]
    [InlineData("MULTIPOINT (EMPTY)")]
    public void Empty_geometry_gives_null(string wkt) => Assert.Null(WktFormat.Parse(wkt));

    [Theory]
    [InlineData("")]
    [InlineData("POINT")]
    [InlineData("POINT (1)")]
    [InlineData("POINT (a b)")]
    [InlineData("POINT (1 2")]
    [InlineData("POINT (1 2) trailing")]
    [InlineData("TRIANGLE ((0 0, 1 0, 0 1, 0 0))")]
    [InlineData("LINESTRING (0 0)")]
    [InlineData("SRID=4326 POINT (1 2)")]
    [InlineData("POINT (1e999 2)")]
    [InlineData("POINT (NaN 2)")]
    public void Malformed_wkt_is_a_format_error(string wkt) => Assert.Throws<FormatException>(() => WktFormat.Parse(wkt));

    [Fact]
    public void Collections_nested_too_deep_are_refused_instead_of_overflowing_the_stack()
    {
        var wkt = string.Concat(Enumerable.Repeat("GEOMETRYCOLLECTION (", 10_000)) + "POINT (1 2)" + new string(')', 10_000);
        Assert.Throws<FormatException>(() => WktFormat.Parse(wkt));
    }

    [Fact]
    public void Random_text_never_throws_anything_but_a_format_error()
    {
        var random = new Random(11);
        const string pieces = "POINT LINESTRING POLYGON MULTI GEOMETRYCOLLECTION Z M EMPTY ( ) , 0 1 2 3.5 -4 1e3 SRID=4326; ";
        var words = pieces.Split(' ');
        for (var i = 0; i < 5000; i++)
        {
            var text = string.Join(' ', Enumerable.Range(0, random.Next(0, 14)).Select(_ => words[random.Next(words.Length)]));
            try
            {
                WktFormat.Parse(text);
            }
            catch (FormatException)
            {
            }
        }
    }
}
