using VisualCronAI.Adf.Models;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Adf;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Models;
using VisualCronAI.Core.Scheduling;

namespace VisualCronAI.Core.Services;

/// <summary>Turns natural tool inputs into JobDto pieces (server-local schedules, ADF runner tasks...).</summary>
public static class JobSpecBuilder
{
    public const int DefaultRetryDelayMinutes = 5;

    public static TaskDto BuildTask(TaskInput input, string environment, EnvironmentOptions env)
    {
        string type = string.IsNullOrWhiteSpace(input.Type) ? "AdfPipeline" : input.Type.Trim();
        int retries = input.RetryTimes ?? 0;
        int delay = input.RetryDelayMinutes ?? DefaultRetryDelayMinutes;
        TaskDto task;

        if (type.Equals("AdfPipeline", StringComparison.OrdinalIgnoreCase) || type.Equals("ADF", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(input.PipelineName)) throw new VisualCronAIException("AdfPipeline tasks need PipelineName.");
            task = AdfTaskFactory.CreateTask(environment, env, input.Name ?? "", input.PipelineName.Trim(), input.Parameters,
                input.TimeoutMinutes ?? 0, input.PollSeconds ?? 0, retries, delay);
        }
        else if (type.Equals(TaskTypes.Execute, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(input.CmdLine)) throw new VisualCronAIException("Execute tasks need CmdLine.");
            task = new TaskDto
            {
                Name = input.Name ?? Path.GetFileName(input.CmdLine),
                TaskType = TaskTypes.Execute,
                Execute = new ExecuteSettingsDto
                {
                    CmdLine = input.CmdLine.Trim(),
                    Arguments = input.Arguments ?? "",
                    WorkingDirectory = input.WorkingDirectory ?? "",
                    CredentialId = input.CredentialId ?? "",
                },
                Flows = [new FlowDto { EventType = FlowEvents.OnSuccess, ActionType = FlowActions.ContinueNextTask }],
            };
            AdfTaskFactory.SetErrorPolicy(task, retries, delay, input.ContinueOnError ?? false);
        }
        else if (type.Equals(TaskTypes.PowerShell, StringComparison.OrdinalIgnoreCase))
        {
            bool isFile = !string.IsNullOrWhiteSpace(input.ScriptFile);
            if (!isFile && string.IsNullOrWhiteSpace(input.ScriptText)) throw new VisualCronAIException("PowerShell tasks need ScriptFile or ScriptText.");
            task = new TaskDto
            {
                Name = input.Name ?? (isFile ? Path.GetFileName(input.ScriptFile!) : "PowerShell script"),
                TaskType = TaskTypes.PowerShell,
                PowerShell = new PowerShellSettingsDto
                {
                    ScriptSource = isFile ? "File" : "Text",
                    File = input.ScriptFile ?? "",
                    Text = input.ScriptText ?? "",
                    WorkingDirectory = input.WorkingDirectory ?? "",
                    CredentialId = input.CredentialId ?? "",
                },
                Flows = [new FlowDto { EventType = FlowEvents.OnSuccess, ActionType = FlowActions.ContinueNextTask }],
            };
            AdfTaskFactory.SetErrorPolicy(task, retries, delay, input.ContinueOnError ?? false);
        }
        else
        {
            throw new VisualCronAIException($"Unsupported task type '{type}'. Use AdfPipeline, Execute or PowerShell.");
        }

        task.Active = input.Active ?? true;
        return task;
    }

    /// <summary>Converts schedule inputs to server-local schedule triggers.</summary>
    public static (List<TriggerDto> Triggers, List<string> Warnings) BuildSchedules(IEnumerable<ScheduleInput> inputs, ServerInfoDto server, DateTime referenceUtc)
    {
        var triggers = new List<TriggerDto>();
        var warnings = new List<string>();
        var serverTz = ScheduleNormalizer.ResolveServerTimeZone(server);
        foreach (var s in inputs)
        {
            var dto = new ScheduleDto
            {
                Mode = NormalizeMode(s.Mode),
                Times = (s.Times ?? []).Select(NormalizeTime).ToList(),
                DaysOfWeek = (s.DaysOfWeek ?? []).Select(d => d.Trim()).ToList(),
                DaysOfMonth = s.DaysOfMonth ?? [],
                IntervalMinutes = s.IntervalMinutes ?? 0,
                TimeZone = s.TimeZone?.Trim() ?? "",
            };
            var report = new ValidationReport();
            Validation.JobValidator.ValidateSchedule(report, "schedule", dto);
            if (!report.IsValid) throw new VisualCronAIException(string.Join(" ", report.Errors.Select(e => e.Message)));
            var result = ScheduleNormalizer.ToServerLocal(dto, serverTz, referenceUtc);
            warnings.AddRange(result.Warnings);
            if (result.Schedules.Count > 1)
                warnings.Add($"Times {string.Join(", ", dto.Times)} were split into {result.Schedules.Count} triggers (VisualCron selects every hour × every minute).");
            triggers.AddRange(result.Schedules.Select(x => new TriggerDto { Kind = TriggerKinds.Schedule, Active = s.Active, Schedule = x }));
        }
        return (triggers, warnings);
    }

    public static TriggerDto BuildDependency(string upstreamId, string upstreamName, string condition)
    {
        string c = string.IsNullOrWhiteSpace(condition) ? JobEventConditions.CompletedSuccessfully : condition.Trim();
        if (c.Equals("success", StringComparison.OrdinalIgnoreCase) || c.Equals("CompletedSuccessfully", StringComparison.OrdinalIgnoreCase)) c = JobEventConditions.CompletedSuccessfully;
        else if (c.Equals("failure", StringComparison.OrdinalIgnoreCase) || c.Equals("CompletedFailed", StringComparison.OrdinalIgnoreCase)) c = JobEventConditions.CompletedFailed;
        else if (c.Equals("Completed", StringComparison.OrdinalIgnoreCase)) c = JobEventConditions.Completed;
        else if (c.Equals("Started", StringComparison.OrdinalIgnoreCase)) c = JobEventConditions.Started;
        return new TriggerDto
        {
            Kind = TriggerKinds.JobEvent,
            JobEvent = new JobEventDto { UpstreamJobId = upstreamId, UpstreamJobName = upstreamName, Condition = c },
        };
    }

    private static string NormalizeMode(string mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "daily" or "day" or "everyday" => ScheduleModes.Daily,
        "weekly" or "week" => ScheduleModes.Weekly,
        "monthly" or "month" => ScheduleModes.Monthly,
        "everynminutes" or "interval" or "minutes" => ScheduleModes.EveryNMinutes,
        _ => mode ?? "",
    };

    /// <summary>Accepts "2:00", "02:00", "2" → "02:00".</summary>
    private static string NormalizeTime(string t)
    {
        t = (t ?? "").Trim();
        if (int.TryParse(t, out int h) && h is >= 0 and <= 23) return $"{h:00}:00";
        var parts = t.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out h) && int.TryParse(parts[1], out int m)) return $"{h:00}:{m:00}";
        return t;
    }

    /// <summary>Re-numbers task orders 1..n in list order.</summary>
    public static void Renumber(List<TaskDto> tasks)
    {
        for (int i = 0; i < tasks.Count; i++) tasks[i].Order = i + 1;
    }

    public static bool IsAdfTask(TaskDto t) => t.Execute != null && AdfRunnerArguments.IsRunnerCommand(t.Execute.CmdLine);
}
