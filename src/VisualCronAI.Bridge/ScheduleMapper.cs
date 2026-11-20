using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VisualCron;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge
{
    /// <summary>
    /// Maps <see cref="ScheduleDto"/> to/from VisualCron's <see cref="TimeClass"/>.
    /// Semantics verified against VisualCron 12's own TimeAPI.GetNextExecutions:
    ///  * Custom trigger, AllDays=true                      → every day
    ///  * Custom trigger, AllDays=false, DayType=false      → Days[0..6] are Monday..Sunday
    ///  * Custom trigger, AllDays=false, DayType=true       → Days[0..30] are day-of-month 1..31
    ///  * Hours[]/Minutes[]/Seconds[] select the times (a grid: every selected hour × every selected minute)
    ///  * Interval trigger, IntervalType=Minutely           → every IntervalValue minutes since InitDate
    /// Times are always server-local; the MCP host converts other time zones before calling the bridge.
    /// </summary>
    public static class ScheduleMapper
    {
        private static readonly string[] WeekdayNames = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        public static ScheduleDto ToDto(TimeClass tt)
        {
            var unmodeled = new ScheduleDto { Mode = ScheduleModes.Unmodeled };
            if (tt == null) return unmodeled;

            if (tt.TimeTriggerType == TimeClass.TimeTriggerT.Interval)
            {
                if (tt.IntervalType == TimeClass.IntervalT.Minutely && tt.IntervalValue > 0)
                    return new ScheduleDto { Mode = ScheduleModes.EveryNMinutes, IntervalMinutes = tt.IntervalValue };
                if (tt.IntervalType == TimeClass.IntervalT.Hourly && tt.IntervalValue > 0)
                    return new ScheduleDto { Mode = ScheduleModes.EveryNMinutes, IntervalMinutes = tt.IntervalValue * 60 };
                return unmodeled;
            }

            // Custom trigger
            if (!tt.AllYears || tt.YearIsBetween || !tt.AllMonths || tt.MonthsIsBetween) return unmodeled;
            if (tt.AllHours || tt.HoursIsBetween || tt.AllMinutes || tt.MinutesIsBetween || tt.AllSeconds || tt.SecondsIsBetween) return unmodeled;
            if (tt.Hours == null || tt.Minutes == null || tt.Seconds == null) return unmodeled;

            var seconds = Indexes(tt.Seconds);
            if (seconds.Count != 1 || seconds[0] != 0) return unmodeled;
            var hours = Indexes(tt.Hours);
            var minutes = Indexes(tt.Minutes);
            if (hours.Count == 0 || minutes.Count == 0) return unmodeled;

            var dto = new ScheduleDto();
            foreach (int h in hours)
                foreach (int m in minutes)
                    dto.Times.Add(h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture));

            if (tt.AllDays)
            {
                dto.Mode = ScheduleModes.Daily;
                return dto;
            }
            if (tt.DaysIsBetween || tt.Days == null) return unmodeled;
            var days = Indexes(tt.Days);
            if (days.Count == 0) return unmodeled;

            if (!tt.DayType)
            {
                if (days.Any(d => d > 6)) return unmodeled;
                dto.Mode = ScheduleModes.Weekly;
                dto.DaysOfWeek = days.Select(d => WeekdayNames[d]).ToList();
                return dto;
            }
            dto.Mode = ScheduleModes.Monthly;
            dto.DaysOfMonth = days.Select(d => d + 1).ToList();
            return dto;
        }

        /// <summary>Builds a fresh TimeClass for the schedule (server-local times).</summary>
        public static TimeClass ToTimeClass(ScheduleDto s, DateTime serverNow)
        {
            if (s == null) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Schedule is required.");
            if (!string.IsNullOrEmpty(s.TimeZone))
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Schedules sent to the bridge must be in server-local time (TimeZone must be empty).");

            switch (s.Mode)
            {
                case ScheduleModes.EveryNMinutes:
                {
                    if (s.IntervalMinutes < 1 || s.IntervalMinutes > 1440)
                        throw new BridgeException(BridgeErrorCodes.InvalidRequest, "IntervalMinutes must be 1..1440.");
                    var tt = new TimeClass(TimeClass.TimeTriggerT.Interval);
                    tt.IntervalType = TimeClass.IntervalT.Minutely;
                    tt.IntervalValue = s.IntervalMinutes;
                    tt.InitDate = serverNow.Date; // align runs to midnight
                    return tt;
                }
                case ScheduleModes.Daily:
                case ScheduleModes.Weekly:
                case ScheduleModes.Monthly:
                {
                    var tt = new TimeClass(TimeClass.TimeTriggerT.Custom);
                    EnsureArrays(tt);
                    tt.AllYears = true; tt.YearIsBetween = false;
                    tt.AllMonths = true; tt.MonthsIsBetween = false;
                    for (int i = 0; i < tt.Months.Length; i++) tt.Months[i] = true;
                    tt.AllHours = false; tt.HoursIsBetween = false;
                    tt.AllMinutes = false; tt.MinutesIsBetween = false;
                    tt.AllSeconds = false; tt.SecondsIsBetween = false;
                    tt.DaysIsBetween = false;

                    var times = ParseTimes(s.Times);
                    var hours = times.Select(t => t.Hour).Distinct().OrderBy(x => x).ToList();
                    var minutes = times.Select(t => t.Minute).Distinct().OrderBy(x => x).ToList();
                    if (hours.Count * minutes.Count != times.Count)
                        throw new BridgeException(BridgeErrorCodes.InvalidRequest,
                            "Times " + string.Join(", ", s.Times) + " do not form an hour×minute grid; use one trigger per time.");
                    Fill(tt.Hours, hours);
                    Fill(tt.Minutes, minutes);
                    Fill(tt.Seconds, new List<int> { 0 });

                    if (s.Mode == ScheduleModes.Daily)
                    {
                        tt.AllDays = true;
                        for (int i = 0; i < tt.Days.Length; i++) tt.Days[i] = false;
                    }
                    else if (s.Mode == ScheduleModes.Weekly)
                    {
                        if (s.DaysOfWeek == null || s.DaysOfWeek.Count == 0)
                            throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Weekly schedules need DaysOfWeek.");
                        tt.AllDays = false;
                        tt.DayType = false;
                        var idx = s.DaysOfWeek.Select(WeekdayIndex).Distinct().ToList();
                        Fill(tt.Days, idx);
                    }
                    else
                    {
                        if (s.DaysOfMonth == null || s.DaysOfMonth.Count == 0)
                            throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Monthly schedules need DaysOfMonth.");
                        if (s.DaysOfMonth.Any(d => d < 1 || d > 31))
                            throw new BridgeException(BridgeErrorCodes.InvalidRequest, "DaysOfMonth must be 1..31.");
                        tt.AllDays = false;
                        tt.DayType = true;
                        Fill(tt.Days, s.DaysOfMonth.Select(d => d - 1).Distinct().ToList());
                    }
                    return tt;
                }
                default:
                    throw new BridgeException(BridgeErrorCodes.Unsupported, "Schedule mode '" + s.Mode + "' cannot be written.");
            }
        }

        /// <summary>Shifts a modeled schedule by <paramref name="shift"/> (trigger offset → server time). Weekly/monthly
        /// schedules whose times would cross midnight become Unmodeled rather than being shown wrongly.</summary>
        public static ScheduleDto Shift(ScheduleDto s, TimeSpan shift)
        {
            if (shift == TimeSpan.Zero || s.Mode == ScheduleModes.Unmodeled || s.Mode == ScheduleModes.EveryNMinutes) return s;
            var times = new List<string>();
            int dayShift = 0; bool mixed = false;
            foreach (var t in s.Times)
            {
                var parsed = TimeSpan.ParseExact(t, @"hh\:mm", CultureInfo.InvariantCulture) + shift;
                int ds = (int)Math.Floor(parsed.TotalDays);
                if (times.Count > 0 && ds != dayShift) mixed = true;
                dayShift = ds;
                var tod = parsed - TimeSpan.FromDays(ds);
                times.Add(tod.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + tod.Minutes.ToString("00", CultureInfo.InvariantCulture));
            }
            if ((dayShift != 0 || mixed) && s.Mode != ScheduleModes.Daily) return new ScheduleDto { Mode = ScheduleModes.Unmodeled };
            return new ScheduleDto { Mode = s.Mode, Times = times, DaysOfWeek = s.DaysOfWeek, DaysOfMonth = s.DaysOfMonth, IntervalMinutes = s.IntervalMinutes };
        }

        public static string Describe(ScheduleDto s)
        {
            switch (s.Mode)
            {
                case ScheduleModes.Daily: return "Every day at " + string.Join(", ", s.Times) + " (server time)";
                case ScheduleModes.Weekly: return "Every " + string.Join(", ", s.DaysOfWeek) + " at " + string.Join(", ", s.Times) + " (server time)";
                case ScheduleModes.Monthly: return "On day " + string.Join(", ", s.DaysOfMonth) + " of every month at " + string.Join(", ", s.Times) + " (server time)";
                case ScheduleModes.EveryNMinutes: return "Every " + s.IntervalMinutes + " minutes";
                default: return "Custom VisualCron schedule";
            }
        }

        private static List<DateTime> ParseTimes(List<string> times)
        {
            if (times == null || times.Count == 0)
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "At least one time (HH:mm) is required.");
            var result = new List<DateTime>();
            foreach (var t in times.Distinct())
            {
                if (!DateTime.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Invalid time '" + t + "'; expected HH:mm (24h).");
                result.Add(parsed);
            }
            return result;
        }

        private static int WeekdayIndex(string name)
        {
            for (int i = 0; i < WeekdayNames.Length; i++)
                if (string.Equals(WeekdayNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Invalid weekday '" + name + "'.");
        }

        private static void EnsureArrays(TimeClass tt)
        {
            if (tt.Hours == null || tt.Hours.Length != 24) tt.Hours = new bool[24];
            if (tt.Minutes == null || tt.Minutes.Length != 60) tt.Minutes = new bool[60];
            if (tt.Seconds == null || tt.Seconds.Length != 60) tt.Seconds = new bool[60];
            if (tt.Days == null || tt.Days.Length != 31) tt.Days = new bool[31];
            if (tt.Months == null || tt.Months.Length != 12) tt.Months = new bool[12];
        }

        private static void Fill(bool[] arr, List<int> selected)
        {
            for (int i = 0; i < arr.Length; i++) arr[i] = selected.Contains(i);
        }

        private static List<int> Indexes(bool[] arr)
        {
            var list = new List<int>();
            for (int i = 0; i < arr.Length; i++) if (arr[i]) list.Add(i);
            return list;
        }
    }
}
