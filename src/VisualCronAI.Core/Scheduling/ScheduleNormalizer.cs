using System.Globalization;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Core.Scheduling;

/// <summary>
/// Converts schedules expressed in a requested time zone ("2 AM Central") into the VisualCron server's local time,
/// which is what VisualCron time triggers use. Also splits time lists that do not form an hour×minute grid into one
/// schedule per time (VisualCron custom triggers select every hour × every minute).
/// </summary>
public static class ScheduleNormalizer
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CT"] = "Central Standard Time", ["CST"] = "Central Standard Time", ["CDT"] = "Central Standard Time",
        ["Central"] = "Central Standard Time", ["Central Time"] = "Central Standard Time", ["US Central"] = "Central Standard Time",
        ["ET"] = "Eastern Standard Time", ["EST"] = "Eastern Standard Time", ["EDT"] = "Eastern Standard Time",
        ["Eastern"] = "Eastern Standard Time", ["Eastern Time"] = "Eastern Standard Time",
        ["MT"] = "Mountain Standard Time", ["MST"] = "Mountain Standard Time", ["MDT"] = "Mountain Standard Time", ["Mountain"] = "Mountain Standard Time",
        ["PT"] = "Pacific Standard Time", ["PST"] = "Pacific Standard Time", ["PDT"] = "Pacific Standard Time", ["Pacific"] = "Pacific Standard Time",
        ["UTC"] = "UTC", ["GMT"] = "GMT Standard Time", ["IST"] = "India Standard Time",
    };

    public static TimeZoneInfo ResolveTimeZone(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new VisualCronAIException("Time zone is empty.");
        string id = Aliases.TryGetValue(name.Trim(), out var alias) ? alias : name.Trim();
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) && TimeZoneInfo.TryFindSystemTimeZoneById(win, out tz)) return tz;
        foreach (var z in TimeZoneInfo.GetSystemTimeZones())
            if (string.Equals(z.StandardName, id, StringComparison.OrdinalIgnoreCase) || string.Equals(z.DisplayName, id, StringComparison.OrdinalIgnoreCase))
                return z;
        throw new VisualCronAIException($"Unknown time zone '{name}'. Use a Windows ID (\"Central Standard Time\"), an IANA ID (\"America/Chicago\") or CT/ET/MT/PT/UTC.");
    }

    /// <summary>Best-effort resolution of the VisualCron server's time zone; falls back to a fixed offset.</summary>
    public static TimeZoneInfo ResolveServerTimeZone(ServerInfoDto server)
    {
        if (!string.IsNullOrWhiteSpace(server.TimeZoneName))
        {
            try { return ResolveTimeZone(server.TimeZoneName); } catch (VisualCronAIException) { }
        }
        var offset = TimeSpan.FromMinutes(server.UtcOffsetMinutes);
        return TimeZoneInfo.CreateCustomTimeZone($"UTC{offset:+hh\\:mm;-hh\\:mm}", offset, $"Server (UTC{offset:+hh\\:mm;-hh\\:mm})", $"UTC{offset:+hh\\:mm;-hh\\:mm}");
    }

    public sealed record Result(List<ScheduleDto> Schedules, List<string> Warnings);

    /// <summary>Returns server-local schedules (TimeZone = "") equivalent to <paramref name="s"/>.</summary>
    public static Result ToServerLocal(ScheduleDto s, TimeZoneInfo serverTz, DateTime referenceUtc)
    {
        var warnings = new List<string>();
        var copy = Clone(s);
        if (copy.Mode is ScheduleModes.EveryNMinutes or ScheduleModes.Unmodeled || string.IsNullOrWhiteSpace(copy.TimeZone))
        {
            copy.TimeZone = "";
            return new Result(SplitGrid(copy), warnings);
        }

        var sourceTz = ResolveTimeZone(copy.TimeZone);
        copy.TimeZone = "";
        if (SameRules(sourceTz, serverTz, referenceUtc))
            return new Result(SplitGrid(copy), warnings);

        // Convert every time; track the day shift so weekly schedules keep the intended weekday.
        var converted = new List<(string Time, int DayShift)>();
        foreach (var t in s.Times)
        {
            var tod = TimeOnly.ParseExact(t, "HH:mm", CultureInfo.InvariantCulture);
            var (now, shift) = Convert(tod, sourceTz, serverTz, referenceUtc);
            var (winter, _) = Convert(tod, sourceTz, serverTz, new DateTime(referenceUtc.Year, 1, 15, 12, 0, 0, DateTimeKind.Utc));
            var (summer, _) = Convert(tod, sourceTz, serverTz, new DateTime(referenceUtc.Year, 7, 15, 12, 0, 0, DateTimeKind.Utc));
            if (winter != summer)
                warnings.Add($"{t} {sourceTz.Id} is {winter:HH\\:mm} server time in winter but {summer:HH\\:mm} in summer (the zones change DST differently). " +
                             $"VisualCron triggers use fixed server-local times, so the trigger was set to {now:HH\\:mm} (correct today); review it when DST changes.");
            converted.Add((now.ToString("HH:mm", CultureInfo.InvariantCulture), shift));
        }

        var shifts = converted.Select(c => c.DayShift).Distinct().ToList();
        if (shifts.Count > 1 && copy.Mode != ScheduleModes.Daily)
            throw new VisualCronAIException("Some times cross midnight when converted to the server time zone and others do not; create separate schedules for them.");
        int dayShift = shifts.FirstOrDefault();
        if (dayShift != 0 && copy.Mode == ScheduleModes.Monthly)
            throw new VisualCronAIException($"Converting this monthly schedule to the server time zone ({serverTz.Id}) moves it to another day; express it in server time instead.");
        if (dayShift != 0 && copy.Mode == ScheduleModes.Weekly)
        {
            copy.DaysOfWeek = copy.DaysOfWeek
                .Select(d => ((int)Enum.Parse<DayOfWeek>(d, true) + dayShift + 7) % 7)
                .Select(i => ((DayOfWeek)i).ToString()).ToList();
            warnings.Add($"Weekdays were shifted by {dayShift} day(s) because the time crosses midnight in the server time zone.");
        }
        copy.Times = converted.Select(c => c.Time).Distinct().ToList();
        warnings.Add($"Times converted from {sourceTz.Id} to server time zone {serverTz.Id}: {string.Join(", ", s.Times)} → {string.Join(", ", copy.Times)}.");
        return new Result(SplitGrid(copy), warnings);
    }

    private static (TimeOnly Time, int DayShift) Convert(TimeOnly tod, TimeZoneInfo from, TimeZoneInfo to, DateTime referenceUtc)
    {
        var refInSource = TimeZoneInfo.ConvertTimeFromUtc(referenceUtc, from).Date;
        var local = DateTime.SpecifyKind(refInSource + tod.ToTimeSpan(), DateTimeKind.Unspecified);
        if (from.IsInvalidTime(local)) local = local.AddHours(1);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, from);
        var target = TimeZoneInfo.ConvertTimeFromUtc(utc, to);
        return (TimeOnly.FromDateTime(target), (target.Date - refInSource).Days);
    }

    private static bool SameRules(TimeZoneInfo a, TimeZoneInfo b, DateTime referenceUtc)
    {
        if (a.Id == b.Id) return true;
        foreach (var month in new[] { 1, 4, 7, 10 })
        {
            var d = new DateTime(referenceUtc.Year, month, 15, 12, 0, 0, DateTimeKind.Utc);
            if (a.GetUtcOffset(d) != b.GetUtcOffset(d)) return false;
        }
        return true;
    }

    /// <summary>VisualCron custom triggers fire at every selected hour × every selected minute.</summary>
    public static List<ScheduleDto> SplitGrid(ScheduleDto s)
    {
        if (s.Mode is ScheduleModes.EveryNMinutes or ScheduleModes.Unmodeled || s.Times.Count <= 1) return [s];
        var times = s.Times.Distinct().ToList();
        var hours = times.Select(t => t[..2]).Distinct().ToList();
        var minutes = times.Select(t => t[3..]).Distinct().ToList();
        if (hours.Count * minutes.Count == times.Count) return [s];
        return times.Select(t => { var c = Clone(s); c.Times = [t]; return c; }).ToList();
    }

    public static ScheduleDto Clone(ScheduleDto s) => new()
    {
        Mode = s.Mode,
        Times = [.. s.Times],
        DaysOfWeek = [.. s.DaysOfWeek],
        DaysOfMonth = [.. s.DaysOfMonth],
        IntervalMinutes = s.IntervalMinutes,
        TimeZone = s.TimeZone,
    };
}
