// iCalendar VEVENT and VTODO ↔ the fourteen-column CSV calendar table.
// Unknown components are ignored; recurrence rules and exception content lines are preserved, not expanded.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Data;

internal static partial class ICalendar
{
    internal static readonly string[] Columns =
        ["Type", "Title", "Start", "End", "All day", "Time zone", "Location", "Description",
         "Organizer", "Attendees", "RRULE", "EXDATE", "Recurrence ID", "UID"];

    internal sealed record Property(string Name, string Value, string Raw, Dictionary<string, string> Parameters)
    {
        public string Parameter(string name) => Parameters.GetValueOrDefault(name, "");
    }

    internal sealed class Component(string type, Dictionary<string, ZoneDefinition>? zones = null)
    {
        public string Type { get; } = type;
        public List<Property> Properties { get; } = [];
        public Dictionary<string, ZoneDefinition> Zones { get; } = zones ?? new(StringComparer.OrdinalIgnoreCase);
        public string[]? Cells { get; init; }

        public Property? First(string name) => Properties.FirstOrDefault(p => p.Name == name);
        public IEnumerable<Property> All(string name) => Properties.Where(p => p.Name == name);
    }

    internal sealed class ZoneDefinition
    {
        public bool HasRule { get; set; }
        public List<(DateTime Local, TimeSpan From, TimeSpan To)> Transitions { get; } = [];
    }

    private static Property? Parse(string line)
    {
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }
        if (colon <= 0)
            return null;
        var parts = new List<string>();
        var start = 0;
        quoted = false;
        for (var i = 0; i < colon; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            if (line[i] == ';' && !quoted)
            {
                parts.Add(line[start..i]);
                start = i + 1;
            }
        }
        parts.Add(line[start..colon]);
        if (parts[0].Length == 0 || parts[0].Any(char.IsWhiteSpace))
            return null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals > 0)
                parameters[part[..equals].ToUpperInvariant()] = part[(equals + 1)..].Trim('"');
        }
        return new Property(parts[0].ToUpperInvariant(), line[(colon + 1)..], line, parameters);
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        var started = false;
        foreach (var line in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            if (started && line.Length > 0 && line[0] is ' ' or '\t')
            {
                current.Append(line.AsSpan(1));
                continue;
            }
            if (started)
                yield return current.ToString();
            current.Clear().Append(line);
            started = true;
        }
        if (started)
            yield return current.ToString();
    }

    public static List<Component> Read(byte[] bytes)
    {
        var result = new List<Component>();
        var stack = new Stack<Component>();
        Dictionary<string, ZoneDefinition>? zones = null;
        foreach (var line in Unfold(DocumentReaders.DecodeText(bytes)))
        {
            var property = Parse(line);
            if (property is null)
                continue;
            if (property.Name == "BEGIN")
            {
                var type = property.Value.ToUpperInvariant();
                if (type == "VCALENDAR" && stack.Count == 0)
                    zones = new Dictionary<string, ZoneDefinition>(StringComparer.OrdinalIgnoreCase);
                stack.Push(new Component(type, zones));
            }
            else if (property.Name == "END")
            {
                if (stack.Count == 0 || stack.Peek().Type != property.Value.ToUpperInvariant())
                    continue;
                var closed = stack.Pop();
                if (closed.Type is "STANDARD" or "DAYLIGHT" && stack.Count > 0 && stack.Peek().Type == "VTIMEZONE")
                {
                    var from = closed.First("TZOFFSETFROM")?.Value;
                    var to = closed.First("TZOFFSETTO")?.Value;
                    var dates = closed.All("DTSTART").Concat(closed.All("RDATE")).SelectMany(p => p.Value.Split(','));
                    if (TryOffset(from, out var oldOffset) && TryOffset(to, out var newOffset))
                        stack.Peek().Properties.Add(new Property("X-FILEE-OBSERVANCE",
                            $"{string.Join(',', dates)}|{oldOffset:c}|{newOffset:c}|{(closed.First("RRULE") is null ? "0" : "1")}",
                            "", new Dictionary<string, string>()));
                }
                if (stack.Count == 1 && stack.Peek().Type == "VCALENDAR")
                {
                    if (closed.Type is "VEVENT" or "VTODO")
                        result.Add(closed);
                    else if (closed.Type == "VTIMEZONE")
                        AddZone(closed);
                }
                if (closed.Type == "VCALENDAR")
                    zones = null;
            }
            else if (stack.Count > 0)
                stack.Peek().Properties.Add(property);
        }
        return result;
    }

    private static void AddZone(Component component)
    {
        var id = component.First("TZID")?.Value;
        if (string.IsNullOrEmpty(id))
            return;
        var definition = new ZoneDefinition();
        // Observances are accumulated while reading, without exposing nested properties on the parent.
        foreach (var entry in component.Properties.Where(p => p.Name == "X-FILEE-OBSERVANCE"))
        {
            var parts = entry.Value.Split('|');
            if (parts.Length < 3 || !TimeSpan.TryParse(parts[1], CultureInfo.InvariantCulture, out var from)
                || !TimeSpan.TryParse(parts[2], CultureInfo.InvariantCulture, out var to))
                continue;
            definition.HasRule |= parts.Length > 3 && parts[3] == "1";
            foreach (var date in parts[0].Split(','))
                if (DateTime.TryParseExact(date, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var local))
                    definition.Transitions.Add((local, from, to));
        }
        component.Zones[id] = definition;
    }

    private static bool TryOffset(string? value, out TimeSpan offset)
    {
        offset = default;
        if (value is not { Length: 5 or 7 } || value[0] is not ('+' or '-')
            || !int.TryParse(value.AsSpan(1, 2), out var hours)
            || !int.TryParse(value.AsSpan(3, 2), out var minutes))
            return false;
        var seconds = 0;
        if (value.Length == 7 && !int.TryParse(value.AsSpan(5, 2), out seconds))
            return false;
        offset = new TimeSpan(hours, minutes, seconds) * (value[0] == '-' ? -1 : 1);
        return true;
    }

    private static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
                result.Append(value[++i] is 'n' or 'N' ? '\n' : value[i]);
            else
                result.Append(value[i]);
        }
        return result.ToString();
    }

    private static string Person(Property property)
    {
        var address = property.Value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            ? property.Value[7..] : property.Value;
        var name = property.Parameter("CN");
        return name.Length == 0 ? address : $"{name} <{address}>";
    }

    private static readonly string[] DateFormats = ["yyyyMMdd", "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmmss'Z'"];

    private static bool ParseIcsDate(Property property, out DateTime value, out bool date, out bool utc)
    {
        date = property.Parameter("VALUE").Equals("DATE", StringComparison.OrdinalIgnoreCase) || property.Value.Length == 8;
        utc = property.Value.EndsWith('Z');
        return DateTime.TryParseExact(property.Value, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out value);
    }

    private static TimeSpan? Offset(string id, DateTime local, Dictionary<string, ZoneDefinition> zones)
    {
        if (zones.TryGetValue(id, out var definition) && !definition.HasRule && definition.Transitions.Count > 0)
        {
            var ordered = definition.Transitions.OrderBy(t => t.Local).ToList();
            var found = ordered.LastOrDefault(t => t.Local <= local);
            return found == default ? ordered[0].From : found.To;
        }
        try { return TimeZoneInfo.FindSystemTimeZoneById(id).GetUtcOffset(local); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    // Resolve the offset at an instant after adding the clock part of a DURATION. The local
    // wall time may have crossed a daylight-saving transition since DTSTART.
    private static TimeSpan? OffsetAtInstant(string id, DateTimeOffset instant, Dictionary<string, ZoneDefinition> zones)
    {
        if (zones.TryGetValue(id, out var definition) && !definition.HasRule && definition.Transitions.Count > 0)
        {
            var ordered = definition.Transitions.OrderBy(t => new DateTimeOffset(t.Local, t.From)).ToList();
            var found = ordered.LastOrDefault(t => new DateTimeOffset(t.Local, t.From) <= instant);
            return found == default ? ordered[0].From : found.To;
        }
        try { return TimeZoneInfo.FindSystemTimeZoneById(id).GetUtcOffset(instant); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    private static string DateCell(Property? property, Dictionary<string, ZoneDefinition> zones)
    {
        if (property is null)
            return "";
        if (!ParseIcsDate(property, out var value, out var date, out var utc))
            return property.Value;
        if (date)
            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (utc)
            return text + "Z";
        var id = property.Parameter("TZID");
        var offset = id.Length == 0 ? null : Offset(id, value, zones);
        return offset is null ? text : new DateTimeOffset(value, offset.Value).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private readonly record struct CalendarDuration(TimeSpan Days, TimeSpan Clock);

    private static bool Duration(string text, out CalendarDuration duration)
    {
        duration = default;
        var sign = 1;
        if (text.StartsWith('-') || text.StartsWith('+'))
        {
            sign = text[0] == '-' ? -1 : 1;
            text = text[1..];
        }
        if (!text.StartsWith('P'))
            return false;
        text = text[1..];
        var time = false;
        var digits = "";
        var any = false;
        var days = TimeSpan.Zero;
        var clock = TimeSpan.Zero;
        foreach (var ch in text)
        {
            if (ch == 'T' && !time)
            {
                time = true;
                continue;
            }
            if (char.IsAsciiDigit(ch))
            {
                digits += ch;
                continue;
            }
            if (digits.Length == 0 || !int.TryParse(digits, out var number))
                return false;
            var part = ch switch
            {
                'W' when !time => TimeSpan.FromDays(number * 7d),
                'D' when !time => TimeSpan.FromDays(number),
                'H' when time => TimeSpan.FromHours(number),
                'M' when time => TimeSpan.FromMinutes(number),
                'S' when time => TimeSpan.FromSeconds(number),
                _ => TimeSpan.MinValue,
            };
            if (part < TimeSpan.Zero)
                return false;
            if (ch is 'W' or 'D')
                days += part;
            else
                clock += part;
            digits = "";
            any = true;
        }
        duration = new CalendarDuration(days * sign, clock * sign);
        return any && digits.Length == 0;
    }

    public static List<List<string>> ToRows(List<Component> components)
    {
        var rows = new List<List<string>> { Columns.ToList() };
        foreach (var component in components)
        {
            var start = component.First("DTSTART");
            var end = component.First(component.Type == "VTODO" ? "DUE" : "DTEND");
            var endCell = DateCell(end, component.Zones);
            if (end is null && start is not null && ParseIcsDate(start, out var startDate, out var date, out var utc)
                && component.First("DURATION") is { } span && Duration(span.Value, out var duration))
            {
                // Calendar days keep the local wall time; hours, minutes and seconds are elapsed time (RFC 5545).
                var dayEnd = startDate + duration.Days;
                var computed = dayEnd + duration.Clock;
                var synthetic = new Property(start.Name, computed.ToString(date ? "yyyyMMdd" : "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
                    + (utc && !date ? "Z" : ""), "", start.Parameters);
                endCell = DateCell(synthetic, component.Zones);
                var zone = start.Parameter("TZID");
                if (!date && !utc && duration.Clock != TimeSpan.Zero && zone.Length > 0
                    && Offset(zone, dayEnd, component.Zones) is { } dayOffset)
                {
                    var instant = new DateTimeOffset(dayEnd, dayOffset).Add(duration.Clock);
                    if (OffsetAtInstant(zone, instant, component.Zones) is { } endOffset)
                        endCell = instant.ToOffset(endOffset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                }
            }
            rows.Add(
            [
                component.Type == "VTODO" ? "todo" : "event",
                Unescape(component.First("SUMMARY")?.Value ?? ""),
                DateCell(start, component.Zones),
                endCell,
                start?.Parameter("VALUE").Equals("DATE", StringComparison.OrdinalIgnoreCase) == true ? "true" : "false",
                start is not null && start.Parameter("VALUE") != "DATE" && start.Value.Length != 8 ? start.Parameter("TZID") : "",
                Unescape(component.First("LOCATION")?.Value ?? ""),
                Unescape(component.First("DESCRIPTION")?.Value ?? ""),
                component.First("ORGANIZER") is { } organizer ? Person(organizer) : "",
                string.Join("\n", component.All("ATTENDEE").Select(Person)),
                component.First("RRULE")?.Value ?? "",
                string.Join("\n", component.All("EXDATE").Select(p => p.Raw)),
                component.First("RECURRENCE-ID")?.Raw ?? "",
                component.First("UID")?.Value ?? "",
            ]);
        }
        return rows;
    }

    private static int Field(string name)
    {
        for (var i = 0; i < Columns.Length; i++)
            if (name.Equals(Columns[i], StringComparison.OrdinalIgnoreCase))
                return i;
        return name.ToLowerInvariant() switch
        {
            "summary" or "subject" or "제목" or "标题" => 1,
            "begin" or "시작" or "开始" => 2,
            "finish" or "종료" or "结束" => 3,
            "장소" or "地点" => 6,
            "notes" or "설명" or "描述" => 7,
            _ => -1,
        };
    }

    public static List<Component> FromRows(List<List<string>> rows)
    {
        if (rows.Count == 0)
            throw new InvalidDataException("The table has no iCalendar events or to-dos.");
        var fields = rows[0].Select(Field).ToArray();
        if (!fields.Contains(1) && !fields.Contains(2))
            throw new InvalidDataException("The table has no iCalendar events or to-dos.");
        var result = new List<Component>();
        foreach (var row in rows.Skip(1))
        {
            var cells = new string[Columns.Length];
            Array.Fill(cells, "");
            for (var i = 0; i < Math.Min(fields.Length, row.Count); i++)
                if (fields[i] >= 0 && cells[fields[i]].Length == 0 && !rows[0].Take(i).Any(h => Field(h) == fields[i]))
                    cells[fields[i]] = row[i];
            var todo = cells[0].Equals("todo", StringComparison.OrdinalIgnoreCase);
            if (!todo && !TryCsvDate(cells[2], out _, out _))
                continue;
            result.Add(new Component(todo ? "VTODO" : "VEVENT") { Cells = cells });
        }
        return result;
    }

    private static bool TryCsvDate(string text, out DateTimeOffset instant, out bool date)
    {
        date = text.Length == 10 && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (date)
        {
            instant = new DateTimeOffset(DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture), TimeSpan.Zero);
            return true;
        }
        return DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant);
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n")
            .Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");

    private static void Line(StringBuilder output, string text)
    {
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > 75)
            {
                output.Append("\r\n ");
                bytes = 1;
            }
            output.Append(rune.ToString());
            bytes += size;
        }
        output.Append("\r\n");
    }

    private static string DateLine(string name, string text, string zone, TimeZoneInfo? systemZone)
    {
        if (!TryCsvDate(text, out var instant, out var date))
            return "";
        if (date)
            return $"{name};VALUE=DATE:{instant:yyyyMMdd}";
        var raw = DateTime.ParseExact(text[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (systemZone is not null && zone.Length > 0)
        {
            var local = text.Length == 19 || (!text.EndsWith('Z') && instant.Offset == systemZone.GetUtcOffset(raw))
                ? raw : TimeZoneInfo.ConvertTime(instant, systemZone).DateTime;
            return $"{name};TZID={zone}:{local:yyyyMMdd'T'HHmmss}";
        }
        if (text.Length == 19)
            return $"{name}:{raw:yyyyMMdd'T'HHmmss}";
        return $"{name}:{instant.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}";
    }

    private static string PersonLine(string name, string cell)
    {
        cell = cell.Replace("\r", "").Replace("\n", "");
        var marker = cell.LastIndexOf(" <", StringComparison.Ordinal);
        var display = marker >= 0 && cell.EndsWith('>') ? cell[..marker] : "";
        var address = display.Length > 0 ? cell[(marker + 2)..^1] : cell;
        if (!address.Contains(':'))
            address = "mailto:" + address;
        if (display.Length == 0)
            return name + ":" + address;
        display = display.Replace("\"", "");
        if (display.IndexOfAny([';', ':', ',']) >= 0)
            display = "\"" + display + "\"";
        return $"{name};CN={display}:{address}";
    }

    public static string Write(List<Component> components)
    {
        var output = new StringBuilder();
        Line(output, "BEGIN:VCALENDAR");
        Line(output, "VERSION:2.0");
        Line(output, "PRODID:-//Filee//Filee//EN");
        var zones = new Dictionary<string, (TimeZoneInfo Zone, DateTimeOffset Instant, bool HasStart)>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in components)
        {
            var cells = component.Cells!;
            var zoneId = cells[5].IndexOfAny(['\r', '\n', ':', ';', '"']) < 0 ? cells[5] : "";
            if (zoneId.Length == 0)
                continue;
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                var parsedStart = TryCsvDate(cells[2], out var start, out var startDate);
                var hasStart = cells[2].Length > 0 && parsedStart && !startDate;
                var candidate = hasStart
                    ? start : TryCsvDate(cells[3], out var end, out var endDate) && !endDate ? end : (DateTimeOffset?)null;
                if (candidate is { } instant && (!zones.TryGetValue(zoneId, out var previous)
                    || hasStart && !previous.HasStart || hasStart == previous.HasStart && instant < previous.Instant))
                    zones[zoneId] = (zone, instant, hasStart);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        foreach (var (id, definition) in zones)
            foreach (var line in BuildVTimeZone(definition.Zone, definition.Instant, id).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                Line(output, line);

        foreach (var component in components)
        {
            var cells = component.Cells!;
            var zoneId = cells[5].IndexOfAny(['\r', '\n', ':', ';', '"']) < 0 ? cells[5] : "";
            TimeZoneInfo? zone = null;
            if (zones.TryGetValue(zoneId, out var definition))
                zone = definition.Zone;
            Line(output, "BEGIN:" + component.Type);
            var uid = cells[13].IndexOfAny(['\r', '\n']) >= 0 || cells[13].Length == 0
                ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', cells))))[..16] + "@filee"
                : cells[13];
            Line(output, "UID:" + uid);
            Line(output, "DTSTAMP:" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            if (cells[1].Length > 0)
                Line(output, "SUMMARY:" + Escape(cells[1]));
            var start = DateLine("DTSTART", cells[2], zoneId, zone);
            if (start.Length > 0)
                Line(output, start);
            var end = DateLine(component.Type == "VTODO" ? "DUE" : "DTEND", cells[3], zoneId, zone);
            if (end.Length > 0)
                Line(output, end);
            if (cells[6].Length > 0)
                Line(output, "LOCATION:" + Escape(cells[6]));
            if (cells[7].Length > 0)
                Line(output, "DESCRIPTION:" + Escape(cells[7]));
            if (cells[8].Length > 0)
                Line(output, PersonLine("ORGANIZER", cells[8]));
            foreach (var attendee in cells[9].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Line(output, PersonLine("ATTENDEE", attendee));
            if (cells[10].Length > 0 && cells[10].IndexOfAny(['\r', '\n']) < 0)
                Line(output, "RRULE:" + cells[10]);
            foreach (var (index, name) in new[] { (11, "EXDATE"), (12, "RECURRENCE-ID") })
                foreach (var line in cells[index].Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries))
                    if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith(name + ";", StringComparison.OrdinalIgnoreCase))
                        Line(output, line);
            Line(output, "END:" + component.Type);
        }
        Line(output, "END:VCALENDAR");
        return output.ToString();
    }

    private sealed record Transition(DateTimeOffset Instant, DateTime Local, TimeSpan From, TimeSpan To)
    {
        public bool Daylight => To > From;
    }

    private static List<Transition> Transitions(TimeZoneInfo zone, int year)
    {
        var found = new List<Transition>();
        var until = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var previous = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var offset = zone.GetUtcOffset(previous);
        for (var next = previous.AddHours(1); next <= until; next = next.AddHours(1))
        {
            var current = zone.GetUtcOffset(next);
            if (current != offset)
            {
                var lower = previous;
                var upper = next;
                while ((upper - lower).TotalMinutes > 1)
                {
                    var middle = lower.AddMinutes(Math.Floor((upper - lower).TotalMinutes / 2));
                    if (zone.GetUtcOffset(middle) == offset)
                        lower = middle;
                    else
                        upper = middle;
                }
                found.Add(new Transition(upper, upper.UtcDateTime + offset, offset, current));
            }
            previous = next;
            offset = current;
        }
        return found;
    }

    private static string Rule(Transition transition, TimeZoneInfo zone, int year)
    {
        var day = transition.Local;
        var weekday = day.DayOfWeek.ToString()[..2].ToUpperInvariant();
        var nth = (day.Day - 1) / 7 + 1;
        var candidates = new[]
        {
            $"BYDAY=-1{weekday}",
            $"BYDAY={nth}{weekday}",
            $"BYMONTHDAY={day.Day}",
        };
        var matches = Enumerable.Range(year, 3).SelectMany(y => Transitions(zone, y))
            .Where(t => t.Daylight == transition.Daylight && t.Local.Month == day.Month).ToList();
        foreach (var candidate in candidates)
        {
            if (matches.Count != 3)
                break;
            if (matches.All(t => candidate == $"BYMONTHDAY={t.Local.Day}" ||
                candidate == $"BYDAY={(t.Local.AddDays(7).Month != t.Local.Month ? "-1" : (t.Local.Day - 1) / 7 + 1)}{t.Local.DayOfWeek.ToString()[..2].ToUpperInvariant()}"))
                return $"FREQ=YEARLY;BYMONTH={day.Month};{candidate}";
        }
        return $"FREQ=YEARLY;BYMONTH={day.Month};BYDAY={nth}{weekday}";
    }

    private static string OffsetText(TimeSpan offset) =>
        $"{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs((int)offset.TotalHours):00}{Math.Abs(offset.Minutes):00}";

    /// <summary>Builds observances from actual UTC transitions, rather than adjustment-rule metadata.</summary>
    internal static string BuildVTimeZone(TimeZoneInfo zone, DateTimeOffset instant, string? tzid = null)
    {
        var year = instant.Year;
        var transitions = Transitions(zone, year);
        var output = new StringBuilder();
        Line(output, "BEGIN:VTIMEZONE");
        Line(output, "TZID:" + (tzid ?? zone.Id));
        if (transitions.Count < 2)
        {
            var offset = OffsetText(zone.GetUtcOffset(instant));
            Line(output, "BEGIN:STANDARD");
            Line(output, "DTSTART:19700101T000000");
            Line(output, "TZOFFSETFROM:" + offset);
            Line(output, "TZOFFSETTO:" + offset);
            Line(output, "END:STANDARD");
        }
        else
        {
            foreach (var transition in transitions)
            {
                var type = transition.Daylight ? "DAYLIGHT" : "STANDARD";
                Line(output, "BEGIN:" + type);
                Line(output, "DTSTART:" + transition.Local.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
                Line(output, "TZOFFSETFROM:" + OffsetText(transition.From));
                Line(output, "TZOFFSETTO:" + OffsetText(transition.To));
                Line(output, "RRULE:" + Rule(transition, zone, year));
                Line(output, "END:" + type);
            }
        }
        Line(output, "END:VTIMEZONE");
        return output.ToString();
    }
}
