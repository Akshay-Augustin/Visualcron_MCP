using System.Text.Json;
using System.Text.Json.Serialization;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Adf;

namespace VisualCronAI.Core.Diff;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiffKind { Added, Removed, Changed }

public sealed class DiffEntry
{
    /// <summary>Job, Task, Schedule, Dependency, Parameters, ErrorHandling, Notifications.</summary>
    public string Area { get; init; } = "";
    public string Path { get; init; } = "";
    public DiffKind Kind { get; init; }
    public string? Left { get; init; }
    public string? Right { get; init; }

    public override string ToString() => Kind switch
    {
        DiffKind.Added => $"[{Area}] + {Path}: {Right}",
        DiffKind.Removed => $"[{Area}] - {Path}: {Left}",
        _ => $"[{Area}] ~ {Path}: {Left} → {Right}",
    };
}

public sealed class CompareOptions
{
    /// <summary>Environment tokens to normalize before comparing (e.g. DEV.Tokens vs UAT.Tokens), so expected
    /// environment-specific differences can be reported separately.</summary>
    public IReadOnlyList<string> LeftTokens { get; init; } = [];
    public IReadOnlyList<string> RightTokens { get; init; } = [];
    public bool NormalizeEnvironmentTokens { get; init; }
}

/// <summary>Semantic comparison of two jobs (tasks matched by name, then by position). IDs are ignored.</summary>
public static class JobComparer
{
    public static List<DiffEntry> Compare(JobDto left, JobDto right, CompareOptions? options = null)
    {
        options ??= new CompareOptions();
        string N(string? s, bool isLeft)
        {
            s ??= "";
            if (!options.NormalizeEnvironmentTokens) return s;
            var tokens = isLeft ? options.LeftTokens : options.RightTokens;
            for (int i = 0; i < tokens.Count; i++)
                if (!string.IsNullOrEmpty(tokens[i])) s = s.Replace(tokens[i], "{ENV" + i + "}", StringComparison.Ordinal);
            return s;
        }

        var diffs = new List<DiffEntry>();
        void Cmp(string area, string path, string? l, string? r)
        {
            if (!string.Equals(l ?? "", r ?? "", StringComparison.Ordinal))
                diffs.Add(new DiffEntry { Area = area, Path = path, Kind = DiffKind.Changed, Left = l, Right = r });
        }

        // ---- job-level
        Cmp("Job", "name", N(left.Name, true), N(right.Name, false));
        Cmp("Job", "group", left.Group, right.Group);
        Cmp("Job", "description", N(left.Description, true), N(right.Description, false));
        Cmp("Job", "active", left.Active.ToString(), right.Active.ToString());
        Cmp("Job", "runTasksInOrder", left.RunTasksInOrder.ToString(), right.RunTasksInOrder.ToString());
        Cmp("Job", "notStartIfRunning", left.NotStartIfRunning.ToString(), right.NotStartIfRunning.ToString());
        Cmp("Job", "queueJobs", left.QueueJobs.ToString(), right.QueueJobs.ToString());
        Cmp("Job", "timeout", Timeout(left.Timeout), Timeout(right.Timeout));
        SetDiff(diffs, "Notifications", "job.errorNotifications", left.ErrorNotificationIds, right.ErrorNotificationIds);
        SetDiff(diffs, "Notifications", "job.completeNotifications", left.CompleteNotificationIds, right.CompleteNotificationIds);
        SetDiff(diffs, "Job", "job.conditions", left.ConditionSetIds, right.ConditionSetIds);

        // ---- tasks
        var lt = left.Tasks.OrderBy(t => t.Order).ToList();
        var rt = right.Tasks.OrderBy(t => t.Order).ToList();
        var pairs = new List<(TaskDto? L, TaskDto? R)>();
        var unmatchedRight = new List<TaskDto>(rt);
        var unmatchedLeft = new List<TaskDto>();
        foreach (var l in lt)
        {
            var match = unmatchedRight.FirstOrDefault(r => string.Equals(N(r.Name, false), N(l.Name, true), StringComparison.OrdinalIgnoreCase));
            if (match != null) { pairs.Add((l, match)); unmatchedRight.Remove(match); }
            else unmatchedLeft.Add(l);
        }
        // Pair remaining tasks by position (likely renames).
        for (int i = 0; i < Math.Min(unmatchedLeft.Count, unmatchedRight.Count); i++) pairs.Add((unmatchedLeft[i], unmatchedRight[i]));
        foreach (var l in unmatchedLeft.Skip(unmatchedRight.Count)) pairs.Add((l, null));
        foreach (var r in unmatchedRight.Skip(unmatchedLeft.Count)) pairs.Add((null, r));

        foreach (var (l, r) in pairs)
        {
            if (l == null) { diffs.Add(new DiffEntry { Area = "Task", Path = $"task '{r!.Name}'", Kind = DiffKind.Added, Right = DescribeTask(r) }); continue; }
            if (r == null) { diffs.Add(new DiffEntry { Area = "Task", Path = $"task '{l.Name}'", Kind = DiffKind.Removed, Left = DescribeTask(l) }); continue; }
            string p = $"task '{r.Name}'";
            Cmp("Task", p + ".name", N(l.Name, true), N(r.Name, false));
            Cmp("Task", p + ".type", l.TaskType, r.TaskType);
            Cmp("Task", p + ".active", l.Active.ToString(), r.Active.ToString());
            Cmp("Task", p + ".position", lt.IndexOf(l).ToString(), rt.IndexOf(r).ToString());

            var la = AdfTaskFactory.TryRead(l);
            var ra = AdfTaskFactory.TryRead(r);
            if (la != null && ra != null)
            {
                Cmp("Parameters", p + ".adf.pipeline", la.PipelineName, ra.PipelineName);
                Cmp("Parameters", p + ".adf.environment", N(la.Environment, true), N(ra.Environment, false));
                Cmp("Parameters", p + ".adf.timeoutMinutes", la.TimeoutMinutes.ToString(), ra.TimeoutMinutes.ToString());
                foreach (var key in la.Parameters.Keys.Union(ra.Parameters.Keys).OrderBy(k => k))
                {
                    la.Parameters.TryGetValue(key, out var lv);
                    ra.Parameters.TryGetValue(key, out var rv);
                    if (lv == null) diffs.Add(new DiffEntry { Area = "Parameters", Path = $"{p}.adf.param[{key}]", Kind = DiffKind.Added, Right = rv });
                    else if (rv == null) diffs.Add(new DiffEntry { Area = "Parameters", Path = $"{p}.adf.param[{key}]", Kind = DiffKind.Removed, Left = lv });
                    else Cmp("Parameters", $"{p}.adf.param[{key}]", N(lv, true), N(rv, false));
                }
                Cmp("Task", p + ".runnerPath", N(l.Execute!.CmdLine, true), N(r.Execute!.CmdLine, false));
            }
            else if (l.Execute != null || r.Execute != null)
            {
                Cmp("Task", p + ".execute.cmdLine", N(l.Execute?.CmdLine, true), N(r.Execute?.CmdLine, false));
                Cmp("Task", p + ".execute.arguments", N(l.Execute?.Arguments, true), N(r.Execute?.Arguments, false));
                Cmp("Task", p + ".execute.workingDirectory", N(l.Execute?.WorkingDirectory, true), N(r.Execute?.WorkingDirectory, false));
                Cmp("Task", p + ".execute.credentialId", l.Execute?.CredentialId, r.Execute?.CredentialId);
            }
            if (l.Http != null || r.Http != null)
            {
                string U(HttpSettingsDto? h, bool left) => N(AdfHttpPattern.NormalizeTaskRefs(h?.Url), left).Trim();
                Cmp("Task", p + ".http.url", U(l.Http, true), U(r.Http, false));
                Cmp("Task", p + ".http.method", l.Http?.Method, r.Http?.Method);
                Cmp("Task", p + ".http.body", l.Http?.BodyRawMasked == true ? "(withheld)" : N(AdfHttpPattern.NormalizeTaskRefs(l.Http?.BodyRaw), true),
                                              r.Http?.BodyRawMasked == true ? "(withheld)" : N(AdfHttpPattern.NormalizeTaskRefs(r.Http?.BodyRaw), false));
                string Kv(List<KeyValueDto>? kvs, bool left) => string.Join("; ", (kvs ?? []).Select(k => k.Key + "=" + (k.Masked ? "(withheld)" : N(AdfHttpPattern.NormalizeTaskRefs(k.Value), left))));
                Cmp("Task", p + ".http.bodyFields", Kv(l.Http?.BodyFields, true), Kv(r.Http?.BodyFields, false));
                Cmp("Task", p + ".http.headers", Kv(l.Http?.Headers, true), Kv(r.Http?.Headers, false));
            }
            if (l.JsonFilter != null || r.JsonFilter != null)
            {
                Cmp("Task", p + ".jsonFilter.path", l.JsonFilter?.JsonPath, r.JsonFilter?.JsonPath);
                Cmp("Task", p + ".jsonFilter.input", AdfHttpPattern.NormalizeTaskRefs(l.JsonFilter?.Variable).Trim(), AdfHttpPattern.NormalizeTaskRefs(r.JsonFilter?.Variable).Trim());
            }
            if (l.Wait != null || r.Wait != null)
                Cmp("Task", p + ".wait", l.Wait == null ? "" : $"{l.Wait.Hours}h{l.Wait.Minutes}m{l.Wait.Seconds}s", r.Wait == null ? "" : $"{r.Wait.Hours}h{r.Wait.Minutes}m{r.Wait.Seconds}s");
            Cmp("ErrorHandling", p + ".raiseErrorWhenOutput", Rule(l.OutputErrorRule), Rule(r.OutputErrorRule));
            if (l.PowerShell != null || r.PowerShell != null)
            {
                Cmp("Task", p + ".powerShell.file", N(l.PowerShell?.File, true), N(r.PowerShell?.File, false));
                Cmp("Task", p + ".powerShell.text", N(l.PowerShell?.Text, true), N(r.PowerShell?.Text, false));
            }
            if (!l.Editable || !r.Editable)
                Cmp("Task", p + ".summary", N(AdfHttpPattern.NormalizeTaskRefs(l.Summary), true), N(AdfHttpPattern.NormalizeTaskRefs(r.Summary), false));
            Cmp("ErrorHandling", p + ".onError", ErrorPolicy(l), ErrorPolicy(r));
            Cmp("ErrorHandling", p + ".otherFlows", OtherFlows(l), OtherFlows(r));
            Cmp("Task", p + ".timeout", Timeout(l.Timeout), Timeout(r.Timeout));
            SetDiff(diffs, "Notifications", p + ".errorNotifications", l.ErrorNotificationIds, r.ErrorNotificationIds);
        }

        // ---- ADF via HTTP tasks: summarize the target factory / pipeline / parameters
        var lh = AdfHttpPattern.TryRead(left);
        var rh = AdfHttpPattern.TryRead(right);
        if (lh != null || rh != null)
        {
            Cmp("Parameters", "adf.pipeline", lh?.PipelineName, rh?.PipelineName);
            Cmp("Parameters", "adf.factory", lh == null ? null : $"{lh.SubscriptionId}/{lh.ResourceGroup}/{lh.FactoryName}", rh == null ? null : $"{rh.SubscriptionId}/{rh.ResourceGroup}/{rh.FactoryName}");
            Cmp("Parameters", "adf.parameters", lh == null ? null : string.Join(", ", lh.Parameters.Select(x => x.Key + "=" + x.Value)), rh == null ? null : string.Join(", ", rh.Parameters.Select(x => x.Key + "=" + x.Value)));
            Cmp("ErrorHandling", "adf.statusCheck", lh == null ? null : (lh.PollsUntilComplete ? $"poll every {lh.WaitMinutes} min until finished" : $"single check after {lh.WaitMinutes} min"),
                                                 rh == null ? null : (rh.PollsUntilComplete ? $"poll every {rh.WaitMinutes} min until finished" : $"single check after {rh.WaitMinutes} min"));
        }

        // ---- triggers: schedules and dependencies as sets
        var lSched = left.Triggers.Where(t => t.Kind != TriggerKinds.JobEvent).Select(DescribeTrigger).ToList();
        var rSched = right.Triggers.Where(t => t.Kind != TriggerKinds.JobEvent).Select(DescribeTrigger).ToList();
        SetDiff(diffs, "Schedule", "triggers", lSched, rSched);
        var lDeps = left.Triggers.Where(t => t.Kind == TriggerKinds.JobEvent).Select(t => N(DescribeTrigger(t), true)).ToList();
        var rDeps = right.Triggers.Where(t => t.Kind == TriggerKinds.JobEvent).Select(t => N(DescribeTrigger(t), false)).ToList();
        SetDiff(diffs, "Dependency", "dependencies", lDeps, rDeps);
        return diffs;
    }

    public static string DescribeTask(TaskDto t)
    {
        var adf = AdfTaskFactory.TryRead(t);
        string what = adf != null
            ? $"ADF pipeline {adf.PipelineName} (env {adf.Environment}" + (adf.Parameters.Count > 0 ? ", params " + string.Join(", ", adf.Parameters.Select(p => p.Key + "=" + p.Value)) : "") + ")"
            : t.TaskType switch
            {
                TaskTypes.Execute when t.Execute != null => $"Execute {t.Execute.CmdLine} {t.Execute.Arguments}".Trim(),
                TaskTypes.PowerShell when t.PowerShell != null => t.PowerShell.ScriptSource == "File" ? $"PowerShell file {t.PowerShell.File}" : "PowerShell inline script",
                TaskTypes.Http when t.Http != null => $"HTTP {t.Http.Method} {AdfHttpPattern.NormalizeTaskRefs(t.Http.Url).Trim()}",
                TaskTypes.JsonFilter when t.JsonFilter != null => $"JSON filter '{t.JsonFilter.JsonPath}'",
                TaskTypes.Wait when t.Wait != null => $"Wait {t.Wait.Hours}h {t.Wait.Minutes}m {t.Wait.Seconds}s",
                _ => $"{t.TaskType} task" + (string.IsNullOrEmpty(t.Summary) ? "" : $" ({t.Summary})"),
            };
        return $"{what}; on error: {ErrorPolicy(t)}" + (t.Active ? "" : " [inactive]");
    }

    public static string DescribeTrigger(TriggerDto t)
    {
        string state = t.Active ? "" : " [inactive]";
        if (t.Kind == TriggerKinds.JobEvent && t.JobEvent != null)
        {
            string name = string.IsNullOrEmpty(t.JobEvent.UpstreamJobName) ? t.JobEvent.UpstreamJobId : t.JobEvent.UpstreamJobName;
            return $"after job '{name}' {ConditionText(t.JobEvent.Condition)}{state}";
        }
        if (t.Kind == TriggerKinds.Schedule && t.Schedule != null && t.Schedule.Mode != ScheduleModes.Unmodeled)
            return DescribeSchedule(t.Schedule) + state;
        return (t.Description ?? "custom trigger") + state;
    }

    public static string DescribeSchedule(ScheduleDto s)
    {
        string tz = string.IsNullOrEmpty(s.TimeZone) ? "server time" : s.TimeZone;
        string times = string.Join(", ", s.Times.OrderBy(x => x));
        return s.Mode switch
        {
            ScheduleModes.Daily => $"daily at {times} ({tz})",
            ScheduleModes.Weekly => $"every {string.Join("/", s.DaysOfWeek)} at {times} ({tz})",
            ScheduleModes.Monthly => $"monthly on day {string.Join(", ", s.DaysOfMonth.OrderBy(d => d))} at {times} ({tz})",
            ScheduleModes.EveryNMinutes => $"every {s.IntervalMinutes} minutes",
            _ => "custom schedule",
        };
    }

    public static string ErrorPolicy(TaskDto t)
    {
        var f = t.Flows.FirstOrDefault(x => x.EventType == FlowEvents.OnError);
        if (f == null) return "default (stop job)";
        return f.ActionType switch
        {
            FlowActions.RetryTask => $"retry {f.RetryTimes}x every {(f.RetryDelayMinutes > 0 ? f.RetryDelayMinutes + " min" : f.RetryDelaySeconds + " s")}, then {f.RetryFailAction}",
            FlowActions.RunNotification => $"send notification {f.NotificationId}",
            FlowActions.GotoTask => $"go to task {f.TaskId}",
            FlowActions.RunJob => $"run job {f.JobId}",
            _ => f.ActionType,
        };
    }

    private static string OtherFlows(TaskDto t) => string.Join("; ", t.Flows
        .Where(f => f.EventType != FlowEvents.OnError && !(f.EventType == FlowEvents.OnSuccess && f.ActionType == FlowActions.ContinueNextTask && f.Condition is "Always" or ""))
        .Select(f => f.EventType + (f.Condition is "Always" or "" ? "" : $"[{f.Condition} {f.ConditionType} '{f.ConditionValue}']") + "→" + f.ActionType));

    private static string Rule(OutputErrorRuleDto? r) => r is { Enabled: true } ? $"{r.ConditionType} '{r.Value}'" : "none";

    private static string ConditionText(string c) => c switch
    {
        JobEventConditions.CompletedSuccessfully => "completes successfully",
        JobEventConditions.CompletedFailed => "fails",
        JobEventConditions.Completed => "completes",
        JobEventConditions.Started => "starts",
        _ => c,
    };

    private static string Timeout(TimeoutDto? t) => t is { Enabled: true } ? $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}" + (t.ConsiderError ? " (error)" : "") : "none";

    private static void SetDiff(List<DiffEntry> diffs, string area, string path, IEnumerable<string> left, IEnumerable<string> right)
    {
        var l = left.ToList(); var r = right.ToList();
        foreach (var x in l.Except(r)) diffs.Add(new DiffEntry { Area = area, Path = path, Kind = DiffKind.Removed, Left = x });
        foreach (var x in r.Except(l)) diffs.Add(new DiffEntry { Area = area, Path = path, Kind = DiffKind.Added, Right = x });
    }

    /// <summary>Human-readable description of a whole job (used for create proposals).</summary>
    public static List<string> DescribeJob(JobDto j)
    {
        var lines = new List<string>
        {
            $"Job '{j.Name}' in group '{j.Group}' — {(j.Active ? "active" : "INACTIVE")}",
        };
        if (!string.IsNullOrWhiteSpace(j.Description)) lines.Add("Description: " + j.Description);
        lines.Add($"Tasks ({(j.RunTasksInOrder ? "run in order" : "parallel")}):");
        foreach (var (t, i) in j.Tasks.OrderBy(t => t.Order).Select((t, i) => (t, i)))
            lines.Add($"  {i + 1}. {t.Name}: {DescribeTask(t)}");
        lines.Add("Triggers:");
        if (j.Triggers.Count == 0) lines.Add("  (none — manual only)");
        foreach (var t in j.Triggers) lines.Add("  - " + DescribeTrigger(t));
        lines.Add("On failure: " + (j.ErrorNotificationIds.Count == 0 ? "no notification" : "notify " + string.Join(", ", j.ErrorNotificationIds)));
        if (j.Timeout is { Enabled: true }) lines.Add("Timeout: " + Timeout(j.Timeout));
        return lines;
    }

    public static string ToJson(object o) => JsonSerializer.Serialize(o);
}
