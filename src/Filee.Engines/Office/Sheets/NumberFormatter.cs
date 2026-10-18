// Excel number formats → the text a cell shows, shared by the XLSX, XLS and ODS readers so a value looks the same
// whichever file it came from. Formatting is done by ExcelNumberFormat (MIT); dates are Excel serial days.

using System.Globalization;
using ExcelNumberFormat;

namespace Filee.Engines.Office.Sheets;

/// <summary>Formats cell values with Excel number format codes; parsed formats are cached per instance.</summary>
internal sealed class NumberFormatter(bool date1904 = false)
{
    /// <summary>Built-in number formats (ECMA-376 18.8.30); 14 uses the ISO date order most locales read.</summary>
    public static readonly IReadOnlyDictionary<int, string> BuiltInFormats = new Dictionary<int, string>
    {
        [0] = "General",
        [1] = "0",
        [2] = "0.00",
        [3] = "#,##0",
        [4] = "#,##0.00",
        [9] = "0%",
        [10] = "0.00%",
        [11] = "0.00E+00",
        [12] = "# ?/?",
        [13] = "# ??/??",
        [14] = "yyyy-mm-dd",
        [15] = "d-mmm-yy",
        [16] = "d-mmm",
        [17] = "mmm-yy",
        [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM",
        [20] = "h:mm",
        [21] = "h:mm:ss",
        [22] = "yyyy-mm-dd h:mm",
        [37] = "#,##0 ;(#,##0)",
        [38] = "#,##0 ;[Red](#,##0)",
        [39] = "#,##0.00;(#,##0.00)",
        [40] = "#,##0.00;[Red](#,##0.00)",
        [45] = "mm:ss",
        [46] = "[h]:mm:ss",
        [47] = "mmss.0",
        [48] = "##0.0E+0",
        [49] = "@",
    };

    private readonly Dictionary<string, NumberFormat> _formats = [];

    /// <summary>True for a missing format or "General" (any case).</summary>
    public static bool IsGeneral(string? format) =>
        string.IsNullOrEmpty(format) || format.Equals("General", StringComparison.OrdinalIgnoreCase);

    /// <summary>The text Excel shows for <paramref name="value"/> under <paramref name="format"/>.</summary>
    public string Format(double value, string? format)
    {
        if (IsGeneral(format) || Parse(format!) is not { } parsed)
            return General(value);
        try
        {
            return parsed.Format(value, CultureInfo.InvariantCulture, date1904);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return General(value);
        }
    }

    /// <summary>True when <paramref name="format"/> shows a date or time of day (not a duration like [h]:mm).</summary>
    public bool IsDate(string? format) =>
        !IsGeneral(format) && Parse(format!) is { IsDateTimeFormat: true, IsTimeSpanFormat: false };

    private NumberFormat? Parse(string format)
    {
        if (!_formats.TryGetValue(format, out var parsed))
            _formats[format] = parsed = new NumberFormat(format);
        return parsed.IsValid ? parsed : null;
    }

    /// <summary>Excel's General format: up to 11 significant characters, no trailing zeros.</summary>
    public static string General(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e11)
            return value.ToString("0", CultureInfo.InvariantCulture);
        var text = value.ToString("G10", CultureInfo.InvariantCulture);
        return text.Contains('E') ? value.ToString("0.#####E+00", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>
    /// A date as Excel serial days. Excel counts the non-existent 29 February 1900, so its serials before
    /// 1 March 1900 are one lower than OLE Automation dates.
    /// </summary>
    public static double ToSerial(DateTime date)
    {
        var oa = date.ToOADate();
        return oa is >= 1 and < 61 ? oa - 1 : oa;
    }

    /// <summary>The date of an Excel serial (the inverse of <see cref="ToSerial"/>).</summary>
    public static DateTime FromSerial(double serial) =>
        DateTime.FromOADate(serial is >= 0 and < 60 ? serial + 1 : serial);
}
