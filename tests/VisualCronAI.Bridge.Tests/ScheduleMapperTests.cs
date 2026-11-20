using System;
using System.Collections.Generic;
using System.Linq;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge.Tests
{
    /// <summary>
    /// Verifies schedule mapping with VisualCron's *own* scheduling engine (TimeAPI.GetNextExecutions), so these
    /// tests prove the real server would fire at the intended times.
    /// </summary>
    public class ScheduleMapperTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 19, 10, 0, 0); // Thursday

        private static List<DateTime> NextRuns(ScheduleDto s, int count = 6)
        {
            var job = new JobDto { Triggers = { new TriggerDto { Kind = TriggerKinds.Schedule, Schedule = s } } };
            return JobOperations.BuildPreview(job, Now, "test", count).NextExecutionsServerTime;
        }

        [Fact]
        public void Daily_at_0200_runs_every_day_at_2am()
        {
            var runs = NextRuns(new ScheduleDto { Mode = ScheduleModes.Daily, Times = { "02:00" } });
            Assert.Equal(6, runs.Count);
            Assert.All(runs, r => Assert.Equal(new TimeSpan(2, 0, 0), r.TimeOfDay));
            Assert.Equal(new DateTime(2026, 11, 20, 2, 0, 0), runs[0]);
            Assert.Equal(new DateTime(2026, 11, 25, 2, 0, 0), runs[5]);
        }

        [Fact]
        public void Daily_grid_of_two_times_runs_both()
        {
            var runs = NextRuns(new ScheduleDto { Mode = ScheduleModes.Daily, Times = { "06:00", "18:00" } }, 4);
            Assert.Equal(new[]
            {
                new DateTime(2026, 11, 19, 18, 0, 0), new DateTime(2026, 11, 20, 6, 0, 0),
                new DateTime(2026, 11, 20, 18, 0, 0), new DateTime(2026, 11, 21, 6, 0, 0),
            }, runs);
        }

        [Fact]
        public void Weekly_weekdays_skip_weekends()
        {
            var runs = NextRuns(new ScheduleDto
            {
                Mode = ScheduleModes.Weekly,
                Times = { "03:30" },
                DaysOfWeek = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" },
            }, 6);
            Assert.All(runs, r => Assert.NotEqual(DayOfWeek.Saturday, r.DayOfWeek));
            Assert.All(runs, r => Assert.NotEqual(DayOfWeek.Sunday, r.DayOfWeek));
            Assert.All(runs, r => Assert.Equal(new TimeSpan(3, 30, 0), r.TimeOfDay));
            Assert.Equal(new DateTime(2026, 11, 20, 3, 30, 0), runs[0]); // Fri
            Assert.Equal(new DateTime(2026, 11, 23, 3, 30, 0), runs[1]); // Mon
        }

        [Fact]
        public void Weekly_sunday_only()
        {
            var runs = NextRuns(new ScheduleDto { Mode = ScheduleModes.Weekly, Times = { "23:15" }, DaysOfWeek = { "sunday" } }, 3);
            Assert.All(runs, r => Assert.Equal(DayOfWeek.Sunday, r.DayOfWeek));
        }

        [Fact]
        public void Monthly_on_1st_and_15th()
        {
            var runs = NextRuns(new ScheduleDto { Mode = ScheduleModes.Monthly, Times = { "06:00" }, DaysOfMonth = { 1, 15 } }, 3);
            Assert.Equal(new[]
            {
                new DateTime(2026, 12, 1, 6, 0, 0), new DateTime(2026, 12, 15, 6, 0, 0), new DateTime(2027, 1, 1, 6, 0, 0),
            }, runs);
        }

        [Fact]
        public void Every_15_minutes_aligned_to_midnight()
        {
            var runs = NextRuns(new ScheduleDto { Mode = ScheduleModes.EveryNMinutes, IntervalMinutes = 15 }, 3);
            Assert.Equal(new[]
            {
                new DateTime(2026, 11, 19, 10, 15, 0), new DateTime(2026, 11, 19, 10, 30, 0), new DateTime(2026, 11, 19, 10, 45, 0),
            }, runs);
        }

        [Theory]
        [MemberData(nameof(RoundTripCases))]
        public void Schedules_round_trip_through_TimeClass(ScheduleDto s)
        {
            var tt = ScheduleMapper.ToTimeClass(s, Now);
            var back = ScheduleMapper.ToDto(tt);
            Assert.True(JobMapper.SameSchedule(s, back), "Round trip changed the schedule: " + back.Mode);
        }

        public static IEnumerable<object[]> RoundTripCases()
        {
            yield return new object[] { new ScheduleDto { Mode = ScheduleModes.Daily, Times = { "02:00" } } };
            yield return new object[] { new ScheduleDto { Mode = ScheduleModes.Daily, Times = { "01:00", "01:30", "13:00", "13:30" } } };
            yield return new object[] { new ScheduleDto { Mode = ScheduleModes.Weekly, Times = { "03:00" }, DaysOfWeek = { "Monday", "Friday" } } };
            yield return new object[] { new ScheduleDto { Mode = ScheduleModes.Monthly, Times = { "06:45" }, DaysOfMonth = { 1, 31 } } };
            yield return new object[] { new ScheduleDto { Mode = ScheduleModes.EveryNMinutes, IntervalMinutes = 30 } };
        }

        [Fact]
        public void Non_grid_times_are_rejected()
        {
            var ex = Assert.Throws<BridgeException>(() =>
                ScheduleMapper.ToTimeClass(new ScheduleDto { Mode = ScheduleModes.Daily, Times = { "02:15", "14:30" } }, Now));
            Assert.Equal(BridgeErrorCodes.InvalidRequest, ex.Code);
        }

        [Fact]
        public void Non_server_time_zone_is_rejected_by_bridge()
        {
            var ex = Assert.Throws<BridgeException>(() =>
                ScheduleMapper.ToTimeClass(new ScheduleDto { Mode = ScheduleModes.Daily, Times = { "02:00" }, TimeZone = "Central Standard Time" }, Now));
            Assert.Contains("server-local", ex.Message);
        }

        [Theory]
        [InlineData("25:00")]
        [InlineData("2am")]
        [InlineData("")]
        public void Invalid_times_are_rejected(string time)
        {
            Assert.Throws<BridgeException>(() =>
                ScheduleMapper.ToTimeClass(new ScheduleDto { Mode = ScheduleModes.Daily, Times = { time } }, Now));
        }
    }
}
