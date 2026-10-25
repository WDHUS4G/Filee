using System.Globalization;
using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Data;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Tests;

public class ICalendarTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static string Calendar(params string[] lines) =>
        string.Join("\r\n", ["BEGIN:VCALENDAR", "VERSION:2.0", .. lines, "END:VCALENDAR", ""]);

    private static string Event(string uid, params string[] lines) =>
        string.Join("\r\n", ["BEGIN:VEVENT", "UID:" + uid, .. lines, "END:VEVENT"]);

    private static List<List<string>> Rows(string text) => CsvFormat.Parse(text.TrimStart('\uFEFF'), ',');

    private async Task<string> Convert(string text, string extension, string target)
    {
        var path = Path.Combine(fx.NewFolder(), "source." + extension);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = target });
        Assert.Equal(JobState.Completed, job.State);
        return job.Outputs.Single();
    }

    private async Task<List<List<string>>> ReadCalendar(string text) =>
        Rows(await File.ReadAllTextAsync(await Convert(text, "ics", "csv"), TestContext.Current.CancellationToken));

    private static string Cell(List<List<string>> rows, int row, string name) => rows[row][rows[0].IndexOf(name)];

    private static string Csv(params string[][] rows) =>
        string.Join("\r\n", rows.Select(row => string.Join(",", row.Select(value =>
            value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"")))) + "\r\n";

    [Fact]
    public async Task Google_export_preserves_columns_recurrence_attendees_folding_and_skips_alarm()
    {
        var zone = string.Join("\r\n", "BEGIN:VTIMEZONE", "TZID:Asia/Seoul", "BEGIN:STANDARD",
            "DTSTART:19700101T000000", "TZOFFSETFROM:+0900", "TZOFFSETTO:+0900", "END:STANDARD", "END:VTIMEZONE");
        var rows = await ReadCalendar(Calendar(zone, Event("007",
            "SUMMARY:123", "DTSTART:20261021T000000Z", "DTEND:20261021T010000Z", "LOCATION:true",
            "DESCRIPTION:긴 설명이 접힌 줄의 전반부 ", "\t후반부\\n둘째 줄",
            "RRULE:FREQ=WEEKLY;COUNT=3", "EXDATE;TZID=Asia/Seoul:20261028T090000,20261104T090000",
            "EXDATE;TZID=Asia/Seoul:20261111T090000",
            "ATTENDEE;CN=\"Kim, A\":mailto:a@example.com", "ATTENDEE:mailto:b@example.com",
            "BEGIN:VALARM", "DESCRIPTION:do not replace event description", "END:VALARM"),
            Event("seoul", "DTSTART;TZID=Asia/Seoul:20261021T090000")));
        Assert.Equal(ICalendar.Columns, rows[0]);
        Assert.Equal(3, rows.Count);
        Assert.Equal("2026-10-21T00:00:00Z", Cell(rows, 1, "Start"));
        Assert.Equal("2026-10-21T09:00:00+09:00", Cell(rows, 2, "Start"));
        Assert.Equal("123", Cell(rows, 1, "Title"));
        Assert.Equal("true", Cell(rows, 1, "Location"));
        Assert.Equal("007", Cell(rows, 1, "UID"));
        Assert.Equal("긴 설명이 접힌 줄의 전반부 후반부\n둘째 줄", Cell(rows, 1, "Description"));
        Assert.Equal("FREQ=WEEKLY;COUNT=3", Cell(rows, 1, "RRULE"));
        Assert.Equal("EXDATE;TZID=Asia/Seoul:20261028T090000,20261104T090000\nEXDATE;TZID=Asia/Seoul:20261111T090000", Cell(rows, 1, "EXDATE"));
        Assert.Equal("Kim, A <a@example.com>\nb@example.com", Cell(rows, 1, "Attendees"));
    }

    [Fact]
    public async Task Apple_all_day_exclusive_end_duration_and_unknown_properties()
    {
        var rows = await ReadCalendar(Calendar(
            Event("all", "DTSTART;VALUE=DATE:20261021", "DTEND;VALUE=DATE:20261022", "X-APPLE-STRUCTURED-LOCATION:ignored"),
            Event("duration", "DTSTART:20261021T090000Z", "DURATION:PT1H30M"),
            Event("weeks", "DTSTART;VALUE=DATE:20261021", "DURATION:P2W")));
        Assert.Equal("2026-10-22", Cell(rows, 1, "End"));
        Assert.Equal("true", Cell(rows, 1, "All day"));
        Assert.Equal("", Cell(rows, 1, "Time zone"));
        Assert.Equal("2026-10-21T10:30:00Z", Cell(rows, 2, "End"));
        Assert.Equal("2026-11-04", Cell(rows, 3, "End"));
    }

    [Fact]
    public async Task Duration_crossing_daylight_saving_time_keeps_calendar_days_and_elapsed_hours()
    {
        var customZone = string.Join("\r\n", "BEGIN:VTIMEZONE", "TZID:Custom/Spring", "BEGIN:DAYLIGHT",
            "DTSTART:20260308T020000", "TZOFFSETFROM:-0500", "TZOFFSETTO:-0400", "END:DAYLIGHT", "END:VTIMEZONE");
        var rows = await ReadCalendar(Calendar(customZone,
            Event("spring-clock", "DTSTART;TZID=America/New_York:20260308T013000", "DURATION:PT2H"),
            Event("spring-day", "DTSTART;TZID=America/New_York:20260308T013000", "DURATION:P1D"),
            Event("spring-24h", "DTSTART;TZID=America/New_York:20260308T013000", "DURATION:PT24H"),
            Event("spring-mixed", "DTSTART;TZID=America/New_York:20260307T013000", "DURATION:P1DT2H"),
            Event("fall-clock", "DTSTART;TZID=America/New_York:20261101T003000", "DURATION:PT2H"),
            Event("custom", "DTSTART;TZID=Custom/Spring:20260308T013000", "DURATION:PT2H")));
        Assert.Equal("2026-03-08T04:30:00-04:00", Cell(rows, 1, "End"));
        Assert.Equal("2026-03-09T01:30:00-04:00", Cell(rows, 2, "End"));
        Assert.Equal("2026-03-09T02:30:00-04:00", Cell(rows, 3, "End"));
        Assert.Equal("2026-03-08T04:30:00-04:00", Cell(rows, 4, "End"));
        Assert.Equal("2026-11-01T01:30:00-05:00", Cell(rows, 5, "End"));
        Assert.Equal("2026-03-08T04:30:00-04:00", Cell(rows, 6, "End"));
    }

    [Fact]
    public async Task Outlook_windows_zone_custom_zone_unknown_rules_and_recurrence_instance()
    {
        var fixedZone = string.Join("\r\n", "BEGIN:VTIMEZONE", "TZID:Customized Time Zone", "BEGIN:STANDARD",
            "DTSTART:19700101T000000", "TZOFFSETFROM:+0530", "TZOFFSETTO:+0530", "END:STANDARD", "END:VTIMEZONE");
        var unknown = string.Join("\r\n", "BEGIN:VTIMEZONE", "TZID:Unknown/Rule", "BEGIN:STANDARD",
            "DTSTART:19700101T000000", "TZOFFSETFROM:+0100", "TZOFFSETTO:+0200",
            "RRULE:FREQ=YEARLY;BYMONTH=1;BYDAY=1SU", "END:STANDARD", "END:VTIMEZONE");
        var rows = await ReadCalendar(Calendar(fixedZone, unknown,
            Event("windows", "DTSTART;TZID=Korea Standard Time:20261021T090000"),
            Event("custom", "DTSTART;TZID=Customized Time Zone:20261021T090000"),
            Event("unknown", "DTSTART;TZID=Unknown/Rule:20261021T090000"),
            Event("exception", "DTSTART:20261021T100000Z", "RECURRENCE-ID;TZID=Asia/Seoul:20261021T090000")));
        Assert.Equal("2026-10-21T09:00:00+09:00", Cell(rows, 1, "Start"));
        Assert.Equal("2026-10-21T09:00:00+05:30", Cell(rows, 2, "Start"));
        Assert.Equal("2026-10-21T09:00:00", Cell(rows, 3, "Start"));
        Assert.Equal("Unknown/Rule", Cell(rows, 3, "Time zone"));
        Assert.Equal("RECURRENCE-ID;TZID=Asia/Seoul:20261021T090000", Cell(rows, 4, "Recurrence ID"));
    }

    [Fact]
    public async Task Calendar_order_is_event_todo_event_across_two_calendars()
    {
        var text = Calendar(Event("A", "DTSTART:20261021T090000Z"), "BEGIN:VTODO", "UID:B", "END:VTODO")
            + Calendar(Event("C", "DTSTART:20261021T090000Z"));
        var rows = await ReadCalendar(text);
        Assert.Equal([("event", "A"), ("todo", "B"), ("event", "C")],
            rows.Skip(1).Select(row => (row[0], row[13])).ToArray());
    }

    [Fact]
    public async Task File_timezone_wins_and_transitions_are_scoped_to_each_calendar()
    {
        static string Zone(string id, string from, string to) =>
            string.Join("\r\n", "BEGIN:VTIMEZONE", "TZID:" + id, "BEGIN:STANDARD",
                "DTSTART:20261021T100000", "TZOFFSETFROM:" + from, "TZOFFSETTO:" + to,
                "END:STANDARD", "END:VTIMEZONE");
        var text = Calendar(Zone("America/New_York", "+0100", "+0100"),
            Event("file", "DTSTART;TZID=America/New_York:20261021T090000"))
            + Calendar(Zone("Custom/Change", "+0100", "+0200"),
                Event("before", "DTSTART;TZID=Custom/Change:20261021T090000"),
                Event("after", "DTSTART;TZID=Custom/Change:20261021T110000"))
            + Calendar(Zone("America/New_York", "+0300", "+0300"),
                Event("second", "DTSTART;TZID=America/New_York:20261021T090000"));
        var rows = await ReadCalendar(text);
        Assert.EndsWith("+01:00", Cell(rows, 1, "Start"));
        Assert.EndsWith("+01:00", Cell(rows, 2, "Start"));
        Assert.EndsWith("+02:00", Cell(rows, 3, "Start"));
        Assert.EndsWith("+03:00", Cell(rows, 4, "Start"));
    }

    [Fact]
    public async Task Rdate_transitions_before_between_after_and_tab_fold_with_escapes()
    {
        var zone = string.Join("\n", "BEGIN:VTIMEZONE", "TZID:Custom/Rdate", "BEGIN:STANDARD",
            "DTSTART:20261021T100000", "RDATE:20261121T100000",
            "TZOFFSETFROM:+0100", "TZOFFSETTO:+0200", "END:STANDARD",
            "BEGIN:DAYLIGHT", "DTSTART:20261221T100000", "RDATE:20270121T100000",
            "TZOFFSETFROM:+0200", "TZOFFSETTO:+0300", "END:DAYLIGHT", "END:VTIMEZONE");
        var rows = await ReadCalendar(Calendar(zone,
            Event("first", "DTSTART;TZID=Custom/Rdate:20261020T090000", @"SUMMARY:a\\b\;c\,d\ne\Nlast"),
            Event("middle", "DTSTART;TZID=Custom/Rdate:20261101T090000"),
            Event("last", "DTSTART;TZID=Custom/Rdate:20270122T090000")).Replace("\r\n", "\n"));
        Assert.EndsWith("+01:00", Cell(rows, 1, "Start"));
        Assert.EndsWith("+02:00", Cell(rows, 2, "Start"));
        Assert.EndsWith("+03:00", Cell(rows, 3, "Start"));
        Assert.Equal("a\\b;c,d\ne\nlast", Cell(rows, 1, "Title"));
    }

    [Fact]
    public async Task Todo_only_due_duration_without_dates_and_mixed_end_zones()
    {
        var rows = await ReadCalendar(Calendar(
            "BEGIN:VTODO", "UID:due", "DUE;VALUE=DATE:20261030", "END:VTODO",
            "BEGIN:VTODO", "UID:duration", "DTSTART:20261021T090000Z", "DURATION:PT1H", "END:VTODO",
            "BEGIN:VTODO", "UID:empty", "END:VTODO",
            Event("mixed", "DTSTART;TZID=Asia/Seoul:20261021T090000",
                "DTEND;TZID=America/New_York:20261021T090000")));
        Assert.Equal(5, rows.Count);
        Assert.Equal("", Cell(rows, 1, "Start"));
        Assert.Equal("2026-10-30", Cell(rows, 1, "End"));
        Assert.Equal("false", Cell(rows, 1, "All day"));
        Assert.Equal("2026-10-21T10:00:00Z", Cell(rows, 2, "End"));
        Assert.Equal("", Cell(rows, 3, "End"));
        Assert.Equal("Asia/Seoul", Cell(rows, 4, "Time zone"));
        Assert.EndsWith("-04:00", Cell(rows, 4, "End"));
        var output = await File.ReadAllTextAsync(await Convert(Csv(ICalendar.Columns,
            rows[1].ToArray()), "csv", "ics"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("DTSTART", output);
        Assert.Contains("DUE;VALUE=DATE:20261030", output);
    }

    [Fact]
    public async Task Todo_only_calendar_converts_to_one_todo_row()
    {
        var rows = await ReadCalendar(Calendar(
            "BEGIN:VTODO", "UID:only", "DUE;VALUE=DATE:20261030", "END:VTODO"));
        Assert.Equal(2, rows.Count);
        Assert.Equal("todo", Cell(rows, 1, "Type"));
    }

    [Fact]
    public async Task Malformed_input_keeps_only_closed_components_and_raw_unparseable_dates()
    {
        var rows = await ReadCalendar(Calendar(
            "garbage", "NO-COLON",
            Event("closed", "DTSTART:tomorrow", "DTEND:tomorrow", "EXDATE:abc"),
            Event("bad-duration", "DTSTART:20261021T090000Z", "DURATION:abc"),
            Event("missing-start", "DTEND:20261021T090000Z"))
            + "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:unclosed");
        Assert.Equal(4, rows.Count);
        Assert.Equal("tomorrow", Cell(rows, 1, "Start"));
        Assert.Equal("tomorrow", Cell(rows, 1, "End"));
        Assert.Equal("EXDATE:abc", Cell(rows, 1, "EXDATE"));
        Assert.Equal("", Cell(rows, 2, "End"));
        Assert.Equal("", Cell(rows, 3, "Start"));
        Assert.Equal("2026-10-21T09:00:00Z", Cell(rows, 3, "End"));
    }

    [Fact]
    public async Task Ambiguous_new_york_time_uses_standard_offset()
    {
        var rows = await ReadCalendar(Calendar(Event("dst", "DTSTART;TZID=America/New_York:20261101T013000")));
        Assert.Equal("2026-11-01T01:30:00-05:00", Cell(rows, 1, "Start"));
    }

    [Fact]
    public async Task Empty_calendar_and_invalid_table_fail_without_output()
    {
        var ics = Path.Combine(fx.NewFolder(), "empty.ics");
        await File.WriteAllTextAsync(ics, Calendar("BEGIN:VJOURNAL", "END:VJOURNAL"), TestContext.Current.CancellationToken);
        var csv = Path.Combine(fx.NewFolder(), "empty.csv");
        await File.WriteAllTextAsync(csv, "Title,Start\r\nA,\r\nB,10/21/2026\r\n", TestContext.Current.CancellationToken);
        foreach (var (path, target) in new[] { (ics, "csv"), (csv, "ics") })
        {
            var job = await fx.ConvertAsync([path], new Preset { TargetFormat = target });
            Assert.Equal(JobState.Failed, job.State);
            Assert.Equal("error.conversion_failed", job.Files.Single().ErrorKey);
            Assert.Contains("no iCalendar events", job.Files.Single().ErrorDetail);
            Assert.Empty(job.Outputs);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*." + target));
        }
        var missing = Path.Combine(fx.NewFolder(), "unknown.csv");
        await File.WriteAllTextAsync(missing, "Unknown,Other\r\nA,B\r\n", TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Failed, (await fx.ConvertAsync([missing], new Preset { TargetFormat = "ics" })).State);
    }

    [Fact]
    public async Task Json_route_has_two_data_steps_and_uses_csv_type_inference()
    {
        var planner = fx.Catalog.CreatePlanner();
        var route = planner.Plan("ics", "json");
        Assert.NotNull(route);
        Assert.Equal(2, route.Steps.Count);
        Assert.All(route.Steps, step => Assert.Equal("data", step.Converter.Id));
        var path = await Convert(Calendar(Event("007", "SUMMARY:123", "LOCATION:true",
            "DTSTART;VALUE=DATE:20261021")), "ics", "json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        var row = document.RootElement.EnumerateArray().Single();
        Assert.Equal(ICalendar.Columns, row.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(row.GetProperty("All day").GetBoolean());
        Assert.True(row.GetProperty("Location").GetBoolean());
        Assert.Equal(123, row.GetProperty("Title").GetInt32());
        Assert.Equal("007", row.GetProperty("UID").GetString());
    }

    [Fact]
    public async Task Csv_writer_skips_invalid_events_end_and_unknown_columns_and_uses_leftmost_alias()
    {
        var text = await File.ReadAllTextAsync(await Convert(
            Csv(["Title", "Summary", "Start", "End", "Unknown"],
                ["A", "B", "2026-10-21", "soon", "secret"],
                ["skip", "x", "", "", "secret"],
                ["skip2", "y", "10/21/2026", "", "secret"]),
            "csv", "ics"), TestContext.Current.CancellationToken);
        Assert.Equal(1, text.Split("BEGIN:VEVENT").Length - 1);
        Assert.Contains("SUMMARY:A", text);
        Assert.DoesNotContain("SUMMARY:B", text);
        Assert.DoesNotContain("DTEND", text);
        Assert.DoesNotContain("secret", text);
    }

    [Theory]
    [InlineData("Summary", "Begin", "Finish")]
    [InlineData("Subject", "시작", "종료")]
    [InlineData("제목", "开始", "结束")]
    [InlineData("标题", "Begin", "Finish")]
    [InlineData("title", "START", "End")]
    public async Task Accepted_aliases_and_case_insensitive_headers(string title, string start, string end)
    {
        var output = await File.ReadAllTextAsync(await Convert(Csv([title, start, end],
            ["alias", "2026-10-21", "2026-10-22"]), "csv", "ics"), TestContext.Current.CancellationToken);
        Assert.Contains("SUMMARY:alias", output);
        Assert.Contains("DTSTART;VALUE=DATE:20261021", output);
        Assert.Contains("DTEND;VALUE=DATE:20261022", output);
    }

    [Theory]
    [InlineData("Location", "장소")]
    [InlineData("Location", "地点")]
    [InlineData("Description", "Notes")]
    [InlineData("Description", "설명")]
    [InlineData("Description", "描述")]
    public async Task Location_and_description_aliases(string field, string alias)
    {
        var output = await File.ReadAllTextAsync(await Convert(Csv(["Title", "Start", alias],
            ["A", "2026-10-21", "sample"]), "csv", "ics"), TestContext.Current.CancellationToken);
        Assert.Contains(field.ToUpperInvariant() + ":sample", output);
    }

    [Fact]
    public async Task Output_contract_uid_stamp_bom_folding_timezone_and_text_escaping()
    {
        var row = new[] { "event", "제목, 쉼표; 역슬래시\\ 줄\n둘", "2026-07-21T09:00:00+09:00",
            "2026-07-21T10:00:00+09:00", "false", "Asia/Seoul", "", new string('가', 55),
            "Kim, A <a@example.com>", "Kim; A <b@example.com>", "FREQ=DAILY;COUNT=2",
            "EXDATE;TZID=Asia/Seoul:20260722T090000", "RECURRENCE-ID;TZID=Asia/Seoul:20260720T090000", "" };
        var csv = Csv(ICalendar.Columns, row);
        var before = DateTime.UtcNow.AddSeconds(-1);
        var first = await Convert(csv, "csv", "ics");
        var bytes = await File.ReadAllBytesAsync(first, TestContext.Current.CancellationToken);
        var after = DateTime.UtcNow.AddSeconds(1);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Filee//Filee//EN\r\n", text);
        Assert.Contains("BEGIN:VTIMEZONE\r\nTZID:Asia/Seoul\r\n", text);
        Assert.Contains("TZOFFSETFROM:+0900\r\nTZOFFSETTO:+0900", text);
        Assert.Contains("SUMMARY:제목\\, 쉼표\\; 역슬래시\\\\ 줄\\n둘", text);
        Assert.Contains("ORGANIZER;CN=\"Kim, A\":mailto:a@example.com", text);
        Assert.Contains("ATTENDEE;CN=\"Kim; A\":mailto:b@example.com", text);
        Assert.Contains("EXDATE;TZID=Asia/Seoul:20260722T090000", text);
        Assert.Contains("RECURRENCE-ID;TZID=Asia/Seoul:20260720T090000", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        Assert.All(text.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));
        var unfolded = text.Replace("\r\n ", "");
        var stamp = unfolded.Split("\r\n").Single(line => line.StartsWith("DTSTAMP:"))[8..];
        var parsed = DateTime.ParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.InRange(parsed, before.AddSeconds(-1), after);
        var uid = unfolded.Split("\r\n").Single(line => line.StartsWith("UID:"));
        var second = await File.ReadAllTextAsync(await Convert(csv, "csv", "ics"), TestContext.Current.CancellationToken);
        Assert.Contains(uid, second.Replace("\r\n ", ""));
        var roundtrip = await ReadCalendar(text);
        Assert.Equal("Kim, A <a@example.com>", Cell(roundtrip, 1, "Organizer"));
        Assert.Equal("Kim; A <b@example.com>", Cell(roundtrip, 1, "Attendees"));
    }

    [Fact]
    public async Task Injected_rrule_and_exdate_cannot_create_events_or_alarms()
    {
        var row = Enumerable.Repeat("", ICalendar.Columns.Length).ToArray();
        row[1] = "Safe";
        row[2] = "2026-10-22T09:00:00Z";
        row[10] = "FREQ=DAILY\nBEGIN:VALARM";
        row[11] = "EXDATE:20261022T090000Z\r\nEND:VEVENT\nBEGIN:VEVENT\rSUMMARY:Injected"
            + "\nexdate;VALUE=DATE:20261023\rEXDATEX:20261024T090000Z";
        var second = (string[])row.Clone();
        second[1] = "Second";
        second[10] = "FREQ=WEEKLY\rX-INJECT:1";
        second[11] = "";
        var text = (await File.ReadAllTextAsync(await Convert(Csv(ICalendar.Columns, row, second),
            "csv", "ics"), TestContext.Current.CancellationToken)).Replace("\r\n ", "");
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Count(line => line == "BEGIN:VEVENT"));
        Assert.DoesNotContain("VALARM", text);
        Assert.DoesNotContain("Injected", text);
        Assert.DoesNotContain("RRULE:", text);
        Assert.Contains("EXDATE:20261022T090000Z", lines);
        Assert.Contains("exdate;VALUE=DATE:20261023", lines);
        Assert.DoesNotContain("EXDATEX:20261024T090000Z", lines);
        var stack = new Stack<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:", StringComparison.Ordinal))
                stack.Push(line[6..]);
            if (line.StartsWith("END:", StringComparison.Ordinal))
                Assert.Equal(stack.Pop(), line[4..]);
        }
        Assert.Empty(stack);
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "BEGIN", "END", "VERSION", "PRODID", "UID", "DTSTAMP", "SUMMARY",
            "DTSTART", "DTEND", "DUE", "LOCATION", "DESCRIPTION", "ORGANIZER",
            "ATTENDEE", "RRULE", "EXDATE", "RECURRENCE-ID", "TZID",
            "TZOFFSETFROM", "TZOFFSETTO", "TZNAME", "RDATE",
        };
        foreach (var line in lines)
        {
            Assert.Matches(@"^[A-Za-z-]+(?:;[^:\r\n]+)?:[^\r\n]*$", line);
            Assert.Contains(line.Split([';', ':'], 2)[0].ToUpperInvariant(), names);
        }
    }

    [Theory]
    [InlineData("bad\nBEGIN:VEVENT")]
    [InlineData("bad\rEND:VEVENT")]
    public async Task Uid_with_line_break_uses_generated_uid(string uid)
    {
        var text = (await File.ReadAllTextAsync(await Convert(Csv(["Title", "Start", "UID"],
            ["Safe", "2026-10-21", uid]), "csv", "ics"), TestContext.Current.CancellationToken))
            .Replace("\r\n ", "");
        Assert.Matches(@"(?m)^UID:[0-9a-f]{16}@filee\r?$", text);
        Assert.Contains("SUMMARY:Safe\r\nDTSTART;VALUE=DATE:20261021", text);
        Assert.Equal(1, text.Split("BEGIN:VEVENT").Length - 1);
    }

    [Theory]
    [InlineData("Asia/Seoul\nX-INJECT:1")]
    [InlineData("Asia/Seoul\rX-INJECT:1")]
    [InlineData("Asia/Seoul:X")]
    [InlineData("Asia/Seoul;X")]
    [InlineData("Asia/Seoul\"X")]
    public async Task Invalid_timezone_cell_is_discarded_for_dates_and_definitions(string timezone)
    {
        var text = await File.ReadAllTextAsync(await Convert(Csv(["Title", "Start", "Time zone"],
            ["Safe", "2026-10-21T09:00:00+09:00", timezone]), "csv", "ics"),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("TZID", text);
        Assert.DoesNotContain("VTIMEZONE", text);
        Assert.Contains("SUMMARY:Safe\r\nDTSTART:20261021T000000Z", text);
        Assert.Equal(1, text.Split("BEGIN:VEVENT").Length - 1);
    }

    [Fact]
    public async Task Invalid_timezone_cannot_borrow_another_events_valid_zone()
    {
        var text = (await File.ReadAllTextAsync(await Convert(Csv(
            ["Title", "Start", "Time zone"],
            ["Valid", "2026-10-21T09:00:00+09:00", "Asia/Seoul"],
            ["Invalid", "2026-10-21T09:00:00+09:00", "Asia/Seoul;X"]),
            "csv", "ics"), TestContext.Current.CancellationToken)).Replace("\r\n ", "");
        Assert.Contains("BEGIN:VTIMEZONE\r\nTZID:Asia/Seoul", text);
        Assert.Contains("SUMMARY:Valid\r\nDTSTART;TZID=Asia/Seoul:20261021T090000", text);
        Assert.Contains("SUMMARY:Invalid\r\nDTSTART:20261021T000000Z", text);
    }

    [Fact]
    public async Task Recurrence_id_accepts_only_its_own_content_lines()
    {
        var text = (await File.ReadAllTextAsync(await Convert(Csv(["Title", "Start", "Recurrence ID"],
            ["Safe", "2026-10-21", "RECURRENCE-ID;TZID=Asia/Seoul:20261021T090000\nX-INJECT:1"
                + "\rrecurrence-id:20261022T090000Z\r\nRECURRENCE-IDX:20261023T090000Z"]),
            "csv", "ics"), TestContext.Current.CancellationToken)).Replace("\r\n ", "");
        Assert.Contains("RECURRENCE-ID;TZID=Asia/Seoul:20261021T090000\r\n", text);
        Assert.Contains("recurrence-id:20261022T090000Z\r\n", text);
        Assert.DoesNotContain("X-INJECT", text);
        Assert.DoesNotContain("RECURRENCE-IDX", text);
    }

    [Fact]
    public async Task People_remove_quotes_and_line_breaks_without_new_content_lines()
    {
        var text = (await File.ReadAllTextAsync(await Convert(Csv(
            ["Title", "Start", "Organizer", "Attendees"],
            ["Safe", "2026-10-21", "Lee\r\nX-INJECT:1, B <o@example.com>",
                "Kim \"A\", B <a@example.com>\r\nPark\rInjected, C <b@example.com>"]),
            "csv", "ics"), TestContext.Current.CancellationToken)).Replace("\r\n ", "");
        Assert.Contains("ORGANIZER;CN=\"LeeX-INJECT:1, B\":mailto:o@example.com\r\n", text);
        Assert.Contains("ATTENDEE;CN=\"Kim A, B\":mailto:a@example.com\r\n", text);
        Assert.Contains("ATTENDEE;CN=\"ParkInjected, C\":mailto:b@example.com\r\n", text);
        Assert.DoesNotContain("\r\nX-INJECT:", text);
        Assert.DoesNotContain("\r\nInjected,", text);
        Assert.Equal(2, text.Split("ATTENDEE;").Length - 1);
    }

    [Fact]
    public async Task Long_combining_sequence_folds_within_seventy_five_octets_and_roundtrips()
    {
        var title = "e" + new string('\u0301', 40);
        var text = await File.ReadAllTextAsync(await Convert(
            Csv(["Title", "Start"], [title, "2026-10-21"]), "csv", "ics"), TestContext.Current.CancellationToken);
        Assert.All(text.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));
        var rows = await ReadCalendar(text);
        Assert.Equal(title, Cell(rows, 1, "Title"));
    }

    [Fact]
    public async Task Calendar_defaults_type_ignores_all_day_and_preserves_due_only_todo()
    {
        var rows = new[]
        {
            new[] { "", "blank", "2026-10-21T09:00:00Z", "", "true", "", "", "", "", "", "", "", "", "one" },
            new[] { "meeting", "other", "2026-10-21", "", "", "", "", "", "", "", "", "", "", "two" },
            new[] { "todo", "due", "", "2026-10-21T09:00:00+09:00", "false", "Asia/Seoul", "", "", "", "", "", "", "", "three" },
        };
        var text = await File.ReadAllTextAsync(await Convert(Csv([ICalendar.Columns, .. rows]), "csv", "ics"), TestContext.Current.CancellationToken);
        Assert.Equal(2, text.Split("BEGIN:VEVENT").Length - 1);
        Assert.Contains("DTSTART:20261021T090000Z", text);
        Assert.Contains("DTSTART;VALUE=DATE:20261021", text);
        Assert.Contains("BEGIN:VTODO", text);
        var back = await ReadCalendar(text);
        Assert.Equal("", Cell(back, 3, "Start"));
        Assert.Equal("2026-10-21T09:00:00+09:00", Cell(back, 3, "End"));
        Assert.Equal("", Cell(back, 3, "Time zone"));
    }

    [Fact]
    public async Task Canonical_rows_roundtrip_all_fourteen_columns()
    {
        var rows = new[]
        {
            new[] { "event", "all", "2026-10-21", "2026-10-22", "true", "", "", "", "", "", "", "", "", "all" },
            new[] { "event", "seoul", "2026-10-21T09:00:00+09:00", "2026-10-21T10:00:00+09:00", "false", "Asia/Seoul", "", "", "", "", "", "", "", "seoul" },
            new[] { "event", "summer", "2026-07-21T09:00:00-04:00", "2026-07-21T10:00:00-04:00", "false", "America/New_York", "", "", "", "", "", "", "", "summer" },
            new[] { "event", "winter", "2026-12-21T09:00:00-05:00", "2026-12-21T10:00:00-05:00", "false", "America/New_York", "", "", "", "", "", "", "", "winter" },
            new[] { "event", "overlap", "2026-11-01T01:30:00-05:00", "", "false", "America/New_York", "", "", "", "", "", "", "", "overlap" },
            new[] { "event", "gap", "2026-03-08T02:30:00-05:00", "", "false", "America/New_York", "", "", "", "", "", "", "", "gap" },
            new[] { "event", "utc", "2026-10-21T00:00:00Z", "", "false", "", "", "", "", "", "", "", "", "utc" },
            new[] { "event", "float", "2026-10-21T09:00:00", "", "false", "", "", "", "", "", "", "", "", "float" },
            new[] { "todo", "task", "", "2026-10-21T09:00:00", "false", "", "", "", "", "", "", "", "", "todo" },
            new[] { "event", "a,b;c\\d\nsecond", "2026-10-21T09:00:00Z", "", "false", "", "", "x,y;z\\q\nagain",
                "name <a@example.com>", "one <b@example.com>\ntwo <c@example.com>", "FREQ=WEEKLY;COUNT=2",
                "EXDATE:20261028T090000Z\nEXDATE:20261104T090000Z", "RECURRENCE-ID:20261021T090000Z", "details" },
            new[] { "event", "uri", "2026-10-21T09:00:00Z", "", "false", "", "", "", "urn:organizer:abc", "", "", "", "", "uri" },
        };
        var original = Csv([ICalendar.Columns, .. rows]);
        var output = await Convert(original, "csv", "ics");
        var back = Rows(await File.ReadAllTextAsync(await Convert(await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken),
            "ics", "csv"), TestContext.Current.CancellationToken));
        Assert.Equal(rows.Length + 1, back.Count);
        for (var i = 0; i < rows.Length; i++)
            Assert.Equal(rows[i], back[i + 1]);
    }

    [Fact]
    public async Task Noncanonical_rows_follow_loss_and_offset_rules()
    {
        var rows = new[]
        {
            new[] { "event", "generated", "2026-10-21T09:00:00Z", "", "false", "", "", "", "", "", "", "", "", "" },
            new[] { "event", "bad end", "2026-10-21T09:00:00Z", "tomorrow", "false", "", "", "", "", "", "", "", "", "bad" },
            new[] { "event", "floating end", "2026-10-21T09:00:00+09:00", "2026-10-21T10:00:00", "false", "Asia/Seoul", "", "", "", "", "", "", "", "floating" },
            new[] { "event", "overlap front", "2026-11-01T01:30:00-04:00", "", "false", "America/New_York", "", "", "", "", "", "", "", "front" },
            new[] { "event", "unknown", "2026-10-21T09:00:00+09:00", "", "false", "Unknown/Area", "", "", "", "", "", "", "", "unknown" },
            new[] { "event", "numeric", "2026-10-21T09:00:00+09:00", "", "false", "", "", "", "", "", "", "", "", "numeric" },
            new[] { "event", "invalid exdates", "2026-10-21T09:00:00Z", "", "false", "", "", "", "", "",
                "", "EXDATEX:20261028T090000\nEXDATE :20261029T090000", "", "exdates" },
            new[] { "event", "missing start", "", "2026-10-21T09:00:00Z", "false", "", "", "", "", "", "", "", "", "missing" },
        };
        var path = await Convert(Csv([ICalendar.Columns, .. rows]), "csv", "ics");
        var back = await ReadCalendar(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(8, back.Count);
        Assert.EndsWith("@filee", Cell(back, 1, "UID"));
        Assert.Equal("", Cell(back, 2, "End"));
        Assert.Equal("2026-10-21T10:00:00+09:00", Cell(back, 3, "End"));
        Assert.Equal("2026-11-01T01:30:00-05:00", Cell(back, 4, "Start"));
        Assert.Equal("", Cell(back, 5, "Time zone"));
        Assert.Equal("2026-10-21T00:00:00Z", Cell(back, 5, "Start"));
        Assert.Equal("2026-10-21T00:00:00Z", Cell(back, 6, "Start"));
        Assert.Equal("", Cell(back, 7, "EXDATE"));
    }

    [Fact]
    public async Task Fixed_zone_uses_earliest_start_or_todo_end_and_reverts_historical_offsets()
    {
        var row1 = new[] { "todo", "old", "", "2013-01-01T09:00:00+04:00", "false", "Europe/Moscow", "", "", "", "", "", "", "", "old" };
        var row2 = new[] { "event", "new", "2026-01-01T09:00:00+03:00", "", "false", "Europe/Moscow", "", "", "", "", "", "", "", "new" };
        var solo = await File.ReadAllTextAsync(await Convert(Csv(ICalendar.Columns, row1),
            "csv", "ics"), TestContext.Current.CancellationToken);
        Assert.Contains("TZOFFSETFROM:+0400\r\nTZOFFSETTO:+0400", solo);
        var oldEvent = (string[])row1.Clone();
        oldEvent[0] = "event";
        oldEvent[2] = oldEvent[3];
        var output = await File.ReadAllTextAsync(await Convert(Csv(ICalendar.Columns, oldEvent, row2),
            "csv", "ics"), TestContext.Current.CancellationToken);
        Assert.Contains("TZOFFSETFROM:+0400\r\nTZOFFSETTO:+0400", output);
        var back = await ReadCalendar(output);
        Assert.EndsWith("+04:00", Cell(back, 2, "Start"));
    }

    [Fact]
    public void Timezone_builder_observes_transition_wall_times_and_validates_rules()
    {
        var utc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var newYork = ICalendar.BuildVTimeZone(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), utc);
        Assert.Contains("BEGIN:DAYLIGHT\r\nDTSTART:20260308T020000\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0400\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU", newYork);
        Assert.Contains("BEGIN:STANDARD\r\nDTSTART:20261101T020000\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU", newYork);
        var berlin = ICalendar.BuildVTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), utc);
        Assert.Contains("BEGIN:DAYLIGHT\r\nDTSTART:20260329T020000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU", berlin);
        Assert.Contains("BEGIN:STANDARD\r\nDTSTART:20261025T030000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nRRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU", berlin);
        var rules = new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 21),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 9, 21)),
        };
        var custom = TimeZoneInfo.CreateCustomTimeZone("Fixed/Date", TimeSpan.FromHours(1), "Fixed/Date", "Standard", "Daylight", rules);
        var fixedRule = ICalendar.BuildVTimeZone(custom, utc);
        Assert.Contains("BYMONTH=3;BYMONTHDAY=21", fixedRule);
        Assert.Contains("BYMONTH=9;BYMONTHDAY=21", fixedRule);
    }

    [Fact]
    public void Format_registration_and_new_data_donut_include_ics_and_ical()
    {
        Assert.Equal("ics", FormatRegistry.Detect("x.ics")?.Id);
        Assert.Equal("ics", FormatRegistry.Detect("x.ICAL")?.Id);
        var profile = BuiltInData.CreateProfiles().Single(p => p.Id == "data");
        Assert.Contains("ics", profile.Extensions);
        Assert.Contains("ical", profile.Extensions);
    }
}
