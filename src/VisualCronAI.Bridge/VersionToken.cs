using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge
{
    /// <summary>
    /// Optimistic-concurrency token: hash of the job's modeled state plus VisualCron's DateModified, so a change to
    /// either a modeled field or any unmodeled setting (which bumps DateModified) invalidates prepared proposals.
    /// </summary>
    public static class VersionToken
    {
        public static string Compute(JobDto dto)
        {
            string canonical = Canonical(dto, includeIds: true);
            long modified = dto.Stats?.DateModified?.Ticks ?? 0;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical + "|" + modified));
                return "v1-" + BitConverter.ToString(bytes, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Canonical JSON of the modeled, writable state (no stats, no generated descriptions).</summary>
        public static string Canonical(JobDto dto, bool includeIds)
        {
            var projection = new
            {
                id = includeIds ? dto.Id : "",
                dto.Name,
                dto.Group,
                dto.Description,
                dto.Active,
                dto.RunTasksInOrder,
                dto.NotStartIfRunning,
                dto.QueueJobs,
                timeout = dto.Timeout,
                errorNotifications = Sorted(dto.ErrorNotificationIds),
                completeNotifications = Sorted(dto.CompleteNotificationIds),
                startNotifications = Sorted(dto.StartNotificationIds),
                conditions = Sorted(dto.ConditionSetIds),
                tasks = dto.Tasks.OrderBy(t => t.Order).Select(t => new
                {
                    id = includeIds ? t.Id : "",
                    t.Name,
                    t.Active,
                    t.TaskType,
                    execute = t.Editable ? t.Execute : null,
                    powerShell = t.Editable ? t.PowerShell : null,
                    http = t.Editable ? t.Http : null,
                    jsonFilter = t.Editable ? t.JsonFilter : null,
                    wait = t.Editable ? t.Wait : null,
                    outputErrorRule = t.OutputErrorRule,
                    flows = t.Flows.Select(f => new
                    {
                        f.EventType, f.ActionType, f.RetryTimes, f.RetryDelayMinutes, f.RetryDelaySeconds,
                        f.RetryFailAction, taskId = includeIds ? f.TaskId : (string.IsNullOrEmpty(f.TaskId) ? "" : "*"),
                        f.JobId, f.NotificationId, f.Condition, f.ConditionType, f.ConditionValue,
                    }).ToList(),
                    timeout = t.Timeout,
                    errorNotifications = Sorted(t.ErrorNotificationIds),
                }).ToList(),
                triggers = dto.Triggers.Select(tr => new
                {
                    id = includeIds ? tr.Id : "",
                    tr.Kind,
                    tr.Active,
                    schedule = tr.Schedule == null ? null : new
                    {
                        tr.Schedule.Mode,
                        times = Sorted(tr.Schedule.Times),
                        days = tr.Schedule.DaysOfWeek.Select(d => d.ToLowerInvariant()).OrderBy(d => d).ToList(),
                        monthDays = tr.Schedule.DaysOfMonth.OrderBy(d => d).ToList(),
                        tr.Schedule.IntervalMinutes,
                    },
                    jobEvent = tr.JobEvent == null ? null : new { tr.JobEvent.UpstreamJobId, tr.JobEvent.Condition },
                    other = tr.Kind == TriggerKinds.Other ? tr.Description : null,
                }).OrderBy(t => includeIds ? t.id : JsonSerializer.Serialize(t)).ToList(),
            };
            return JsonSerializer.Serialize(projection);
        }

        private static List<string> Sorted(IEnumerable<string>? items) =>
            (items ?? Enumerable.Empty<string>()).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>Compares requested vs. read-back job state, ignoring IDs that VisualCron may assign.</summary>
    public static class JobStateComparer
    {
        public static List<string> Differences(JobDto expected, JobDto actual)
        {
            var diffs = new List<string>();
            void Check(string what, object? a, object? b)
            {
                string sa = JsonSerializer.Serialize(a), sb = JsonSerializer.Serialize(b);
                if (sa != sb) diffs.Add(what + ": expected " + sa + " but server has " + sb);
            }

            Check("name", expected.Name, actual.Name);
            if (!string.IsNullOrWhiteSpace(expected.Group)) Check("group", expected.Group, actual.Group);
            Check("description", expected.Description ?? "", actual.Description ?? "");
            Check("active", expected.Active, actual.Active);
            Check("runTasksInOrder", expected.RunTasksInOrder, actual.RunTasksInOrder);
            Check("errorNotifications", Sort(expected.ErrorNotificationIds), Sort(actual.ErrorNotificationIds));
            Check("taskCount", expected.Tasks.Count, actual.Tasks.Count);
            Check("triggerCount", expected.Triggers.Count, actual.Triggers.Count);

            var exTasks = expected.Tasks.OrderBy(t => t.Order).ToList();
            var acTasks = actual.Tasks.OrderBy(t => t.Order).ToList();
            for (int i = 0; i < Math.Min(exTasks.Count, acTasks.Count); i++)
            {
                var e = exTasks[i]; var a = acTasks[i];
                string p = "task[" + (i + 1) + "] '" + e.Name + "' ";
                Check(p + "name", e.Name, a.Name);
                Check(p + "type", e.TaskType, a.TaskType);
                Check(p + "active", e.Active, a.Active);
                if (e.Editable && e.Execute != null) Check(p + "execute", e.Execute, a.Execute);
                if (e.Editable && e.PowerShell != null) Check(p + "powershell", e.PowerShell, a.PowerShell);
                if (e.Editable && e.Http != null) Check(p + "http", e.Http, a.Http);
                if (e.Editable && e.JsonFilter != null) Check(p + "jsonFilter", e.JsonFilter, a.JsonFilter);
                if (e.Editable && e.Wait != null) Check(p + "wait", e.Wait, a.Wait);
                if (e.OutputErrorRule != null) Check(p + "outputErrorRule", e.OutputErrorRule, a.OutputErrorRule);
                Check(p + "flows", FlowKey(e.Flows), FlowKey(a.Flows));
            }

            var exTrig = expected.Triggers.Select(TriggerKey).OrderBy(s => s).ToList();
            var acTrig = actual.Triggers.Select(TriggerKey).OrderBy(s => s).ToList();
            Check("triggers", exTrig, acTrig);
            return diffs;
        }

        private static List<string> Sort(List<string> l) => l.OrderBy(x => x, StringComparer.Ordinal).ToList();

        private static List<string> FlowKey(List<FlowDto> flows) => flows
            .Select(f => f.EventType + "/" + f.ActionType + "/r" + (f.ActionType == FlowActions.RetryTask ? f.RetryTimes : 0)
                         + "/d" + (f.ActionType == FlowActions.RetryTask ? f.RetryDelayMinutes * 60 + f.RetryDelaySeconds : 0)
                         + "/n" + f.NotificationId + "/j" + f.JobId
                         + (f.Condition == "Always" || string.IsNullOrEmpty(f.Condition) ? "" : "/c" + f.Condition + ":" + f.ConditionType + ":" + f.ConditionValue)
                         + (f.ActionType == FlowActions.GotoTask ? "/goto" : ""))
            .ToList(); // order matters: VisualCron evaluates flows in sequence

        private static string TriggerKey(TriggerDto t)
        {
            string s = t.Kind + "/" + t.Active;
            if (t.Schedule != null)
                s += "/" + t.Schedule.Mode + "/" + string.Join(",", t.Schedule.Times.OrderBy(x => x)) + "/" +
                     string.Join(",", t.Schedule.DaysOfWeek.Select(d => d.ToLowerInvariant()).OrderBy(x => x)) + "/" +
                     string.Join(",", t.Schedule.DaysOfMonth.OrderBy(x => x)) + "/" + t.Schedule.IntervalMinutes;
            if (t.JobEvent != null) s += "/" + t.JobEvent.UpstreamJobId + "/" + t.JobEvent.Condition;
            return s;
        }
    }
}
