using System.Text.RegularExpressions;
using VisualCronAI.Adf.Models;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Core.Validation;

/// <summary>Context for validation. Known-ID sets are optional; when supplied, references are checked against them.</summary>
public sealed class ValidationContext
{
    public required string Environment { get; init; }
    public required EnvironmentOptions EnvironmentOptions { get; init; }
    public bool IsNewJob { get; init; }
    public JobDto? Before { get; init; }
    public IReadOnlySet<string>? KnownNotificationIds { get; init; }
    public IReadOnlySet<string>? KnownCredentialIds { get; init; }
    public IReadOnlyDictionary<string, string>? KnownJobs { get; init; } // id -> name
}

/// <summary>Validates job definitions before they can become approvable proposals. Treats all input as untrusted.</summary>
public static partial class JobValidator
{
    public const int MaxTasks = 50;
    public const int MaxTriggers = 20;

    [GeneratedRegex(@"^[A-Za-z0-9 _\-\.\(\)\[\]&]{1,200}$")]
    private static partial Regex JobNameRegex();
    // ':' is required: environments are encoded as group prefixes ("DEV: ADF-D365 Loads").
    [GeneratedRegex(@"^[A-Za-z0-9 _\-\.\(\)\[\]&/\\:]{0,200}$")]
    private static partial Regex GroupRegex();
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidRegex();
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimeRegex();

    public static bool IsGuid(string? s) => s != null && GuidRegex().IsMatch(s);

    public static ValidationReport Validate(JobDto job, ValidationContext ctx)
    {
        var r = new ValidationReport();
        bool nameChanged = ctx.IsNewJob || ctx.Before == null || !string.Equals(ctx.Before.Name, job.Name, StringComparison.Ordinal);

        // ---- job
        if (string.IsNullOrWhiteSpace(job.Name)) r.Error("job.name", "Job name is required.");
        else if (nameChanged && !JobNameRegex().IsMatch(job.Name))
            r.Error("job.name", "Job name may contain letters, digits, spaces and _ - . ( ) [ ] & (max 200).");
        if (!GroupRegex().IsMatch(job.Group ?? "")) r.Error("job.group", "Job group contains invalid characters or is too long.");
        if ((job.Description ?? "").Length > 2000) r.Error("job.description", "Description is longer than 2000 characters.");
        ScanSecrets(r, "job.description", job.Description);
        ValidateTimeout(r, "job.timeout", job.Timeout);

        CheckIds(r, "job.errorNotificationIds", job.ErrorNotificationIds, ctx.KnownNotificationIds, "notification");
        CheckIds(r, "job.completeNotificationIds", job.CompleteNotificationIds, ctx.KnownNotificationIds, "notification");
        CheckIds(r, "job.startNotificationIds", job.StartNotificationIds, ctx.KnownNotificationIds, "notification");

        var groups = ctx.EnvironmentOptions.WritableJobGroups;
        if (!GroupPattern.MatchesAny(groups, job.Group))
            r.Error("job.group", $"Environment {ctx.Environment} only allows writes to job groups: {string.Join(", ", groups)}.");
        var scope = ctx.EnvironmentOptions.JobGroups;
        if (!GroupPattern.MatchesAny(scope, job.Group))
            r.Error("job.group", $"Job group '{job.Group}' does not belong to environment {ctx.Environment} ({string.Join(", ", scope)}).");

        // ---- tasks
        if (job.Tasks.Count == 0) r.Error("job.tasks", "A job needs at least one task.");
        if (job.Tasks.Count > MaxTasks) r.Error("job.tasks", $"At most {MaxTasks} tasks are supported.");
        var dupNames = job.Tasks.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        // VisualCron allows duplicate task names (real jobs have two "JSON filter" tasks); refer to such tasks by ID or position.
        foreach (var d in dupNames) r.Warn("job.tasks", $"Task name '{d}' is used more than once; refer to these tasks by ID or position.");
        var taskIds = new HashSet<string>(job.Tasks.Select(t => t.Id).Where(id => !string.IsNullOrEmpty(id)));

        for (int i = 0; i < job.Tasks.Count; i++)
            ValidateTask(r, $"tasks[{i}]", job.Tasks[i], taskIds, ctx);

        // ---- triggers
        if (job.Triggers.Count > MaxTriggers) r.Error("job.triggers", $"At most {MaxTriggers} triggers are supported.");
        for (int i = 0; i < job.Triggers.Count; i++)
            ValidateTrigger(r, $"triggers[{i}]", job.Triggers[i], job, ctx);
        if (!job.Triggers.Any(t => t.Active))
            r.Warn("job.triggers", "The job has no active trigger; it will only run when started manually.");

        // ---- tasks must not read other jobs' task output ({TASK(id|...)} of a task outside this job)
        foreach (var (task, where, foreignId) in Adf.AdfHttpPattern.ForeignReferences(job))
        {
            string msg = $"Task '{task.Name}' (#{task.Order}) {where} reads task {foreignId[..8]}… which is not part of this job (copied from another job?).";
            if (ctx.IsNewJob) r.Error($"tasks.{task.Order}.references", msg); else r.Warn($"tasks.{task.Order}.references", msg);
        }

        // ---- ADF via HTTP tasks: the job must call this environment's factory
        var adf = Adf.AdfHttpPattern.TryRead(job);
        var eo = ctx.EnvironmentOptions;
        if (adf != null)
        {
            void Target(string what, string actual, string expected)
            {
                if (string.IsNullOrWhiteSpace(expected) || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return;
                string msg = $"ADF {what} is '{actual}' but environment {ctx.Environment} uses '{expected}'.";
                if (ctx.IsNewJob) r.Error("adf." + what, msg); else r.Warn("adf." + what, msg);
            }
            Target("subscription", adf.SubscriptionId, eo.AdfSubscriptionId);
            Target("resourceGroup", adf.ResourceGroup, eo.AdfResourceGroup);
            Target("factory", adf.FactoryName, eo.AdfFactoryName);
            if (!adf.PollsUntilComplete)
                r.Warn("adf.polling", $"Pipeline status is checked once after a fixed {adf.WaitMinutes}-minute wait; a failed or still-running pipeline is reported as Success. Use AdfPollUntilComplete to fix.");
        }

        // ---- operational warnings
        if (job.ErrorNotificationIds.Count == 0 && !job.Tasks.Any(t => t.Flows.Any(f => f.ActionType == FlowActions.RunNotification)))
            r.Warn("job.errorNotificationIds", "No failure notification is configured.");
        if (!job.Active) r.Warn("job.active", "The job will be inactive (it will not run until enabled).");
        return r;
    }

    private static void ValidateTask(ValidationReport r, string path, TaskDto t, HashSet<string> taskIds, ValidationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 200) r.Error(path + ".name", "Task name is required (max 200).");
        ScanSecrets(r, path + ".description", t.Description);
        ValidateTimeout(r, path + ".timeout", t.Timeout);
        CheckIds(r, path + ".errorNotificationIds", t.ErrorNotificationIds, ctx.KnownNotificationIds, "notification");

        bool isNewTask = ctx.IsNewJob || string.IsNullOrEmpty(t.Id) || ctx.Before?.Tasks.All(b => b.Id != t.Id) == true;
        if (isNewTask && t.TaskType is not (TaskTypes.Execute or TaskTypes.PowerShell or TaskTypes.Http or TaskTypes.JsonFilter or TaskTypes.Wait))
            r.Error(path + ".taskType", $"New tasks must be Execute, PowerShell, HTTP, JSONFilter or Wait (got '{t.TaskType}').");

        if (t.Editable && t.TaskType == TaskTypes.Execute)
        {
            var e = t.Execute;
            if (e == null) { r.Error(path + ".execute", "Execute settings are required."); }
            else
            {
                if (string.IsNullOrWhiteSpace(e.CmdLine) || e.CmdLine.Length > 1024) r.Error(path + ".execute.cmdLine", "Command line is required (max 1024).");
                if (e.Arguments.Length > 8000) r.Error(path + ".execute.arguments", "Arguments exceed 8000 characters.");
                if (e.WorkingDirectory.Length > 260) r.Error(path + ".execute.workingDirectory", "Working directory is too long.");
                if (e.CmdLine.IndexOfAny(['\r', '\n']) >= 0 || e.Arguments.IndexOfAny(['\r', '\n']) >= 0)
                    r.Error(path + ".execute", "Command line and arguments must be single-line.");
                ScanSecrets(r, path + ".execute.cmdLine", e.CmdLine);
                ScanSecrets(r, path + ".execute.arguments", e.Arguments);
                CheckIds(r, path + ".execute.credentialId", string.IsNullOrEmpty(e.CredentialId) ? [] : [e.CredentialId], ctx.KnownCredentialIds, "credential");
                if (AdfRunnerArguments.IsRunnerCommand(e.CmdLine)) ValidateAdfTask(r, path, t, e, ctx);
            }
        }
        else if (t.Editable && t.TaskType == TaskTypes.PowerShell)
        {
            var p = t.PowerShell;
            if (p == null) { r.Error(path + ".powerShell", "PowerShell settings are required."); }
            else
            {
                if (p.ScriptSource is not ("File" or "Text")) r.Error(path + ".powerShell.scriptSource", "ScriptSource must be File or Text.");
                if (p.ScriptSource == "File" && string.IsNullOrWhiteSpace(p.File)) r.Error(path + ".powerShell.file", "Script file path is required.");
                if (p.ScriptSource == "Text" && string.IsNullOrWhiteSpace(p.Text)) r.Error(path + ".powerShell.text", "Script text is required.");
                if (p.Text.Length > 20000) r.Error(path + ".powerShell.text", "Script text exceeds 20000 characters.");
                ScanSecrets(r, path + ".powerShell.text", p.Text);
                CheckIds(r, path + ".powerShell.credentialId", string.IsNullOrEmpty(p.CredentialId) ? [] : [p.CredentialId], ctx.KnownCredentialIds, "credential");
            }
        }

        else if (t.Editable && t.TaskType == TaskTypes.Http)
        {
            var h = t.Http;
            if (h == null) { r.Error(path + ".http", "HTTP settings are required."); }
            else
            {
                string url = h.Url.Trim();
                if (!(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("{")))
                    r.Error(path + ".http.url", "URL must start with https:// (or a VisualCron variable).");
                if (url.Length > 4000) r.Error(path + ".http.url", "URL is too long.");
                if (h.Method.ToUpperInvariant() is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD"))
                    r.Error(path + ".http.method", $"Unsupported HTTP method '{h.Method}'.");
                ScanSecrets(r, path + ".http.url", h.Url);
                if (!h.BodyRawMasked) ScanSecrets(r, path + ".http.bodyRaw", h.BodyRaw);
                foreach (var kv in h.BodyFields.Concat(h.Headers).Where(kv => !kv.Masked))
                    ScanSecrets(r, $"{path}.http[{kv.Key}]", kv.Key + "=" + kv.Value);
            }
        }
        else if (t.Editable && t.TaskType == TaskTypes.JsonFilter)
        {
            if (t.JsonFilter == null) r.Error(path + ".jsonFilter", "JSON filter settings are required.");
            else if (string.IsNullOrWhiteSpace(t.JsonFilter.JsonPath)) r.Error(path + ".jsonFilter.jsonPath", "JSON path is required.");
        }
        else if (t.Editable && t.TaskType == TaskTypes.Wait)
        {
            var w = t.Wait;
            if (w == null) r.Error(path + ".wait", "Wait settings are required.");
            else if (w.Hours is < 0 or > 24 || w.Minutes is < 0 or > 59 || w.Seconds is < 0 or > 59) r.Error(path + ".wait", "Wait must be between 0 and 24 hours.");
        }

        foreach (var (f, i) in t.Flows.Select((f, i) => (f, i)))
        {
            string fp = $"{path}.flows[{i}]";
            if (f.EventType is not (FlowEvents.OnStart or FlowEvents.OnError or FlowEvents.OnSuccess or FlowEvents.OnComplete or "OnRunningLongerThan"))
                r.Error(fp + ".eventType", $"Unknown flow event '{f.EventType}'.");
            if (f.ActionType is not (FlowActions.ContinueNextTask or FlowActions.StopJob or FlowActions.RetryTask or FlowActions.GotoTask or FlowActions.RunJob or FlowActions.RunNotification or "RetryJob"))
                r.Error(fp + ".actionType", $"Unknown flow action '{f.ActionType}'.");
            if (f.RetryTimes is < 0 or > 10) r.Error(fp + ".retryTimes", "Retry count must be 0..10.");
            if (f.RetryDelayMinutes is < 0 or > 1440 || f.RetryDelaySeconds is < 0 or > 59) r.Error(fp + ".retryDelay", "Retry delay is out of range.");
            if (f.ActionType == FlowActions.GotoTask && !taskIds.Contains(f.TaskId)) r.Error(fp + ".taskId", "GotoTask target is not a task of this job.");
            if (f.ActionType == FlowActions.RunNotification)
                CheckIds(r, fp + ".notificationId", [f.NotificationId], ctx.KnownNotificationIds, "notification");
            if (f.ActionType == FlowActions.RunJob && ctx.KnownJobs != null && !ctx.KnownJobs.ContainsKey(f.JobId))
                r.Error(fp + ".jobId", $"RunJob target {f.JobId} does not exist in {ctx.Environment}.");
        }
        if (t.Flows.Count(f => f.EventType == FlowEvents.OnError) > 1)
            r.Warn(path + ".flows", "More than one OnError flow; VisualCron evaluates them in order.");
    }

    private static void ValidateAdfTask(ValidationReport r, string path, TaskDto t, ExecuteSettingsDto e, ValidationContext ctx)
    {
        AdfRunnerArguments args;
        try { args = AdfRunnerArguments.Parse(e.Arguments); }
        catch (FormatException ex) { r.Error(path + ".execute.arguments", "Invalid ADF runner arguments: " + ex.Message); return; }
        foreach (var err in args.Validate()) r.Error(path + ".adf", err);

        string expectedEnv = string.IsNullOrWhiteSpace(ctx.EnvironmentOptions.AdfEnvironment) ? ctx.Environment : ctx.EnvironmentOptions.AdfEnvironment;
        if (!string.Equals(args.Environment, expectedEnv, StringComparison.OrdinalIgnoreCase))
            r.Error(path + ".adf.environment", $"Task targets ADF environment '{args.Environment}' but this VisualCron environment is {ctx.Environment} (expected '{expectedEnv}').");
        if (!string.IsNullOrWhiteSpace(ctx.EnvironmentOptions.AdfRunnerPath) &&
            !string.Equals(e.CmdLine.Trim('"'), ctx.EnvironmentOptions.AdfRunnerPath, StringComparison.OrdinalIgnoreCase))
            r.Warn(path + ".execute.cmdLine", $"ADF runner path differs from the configured path for {ctx.Environment} ({ctx.EnvironmentOptions.AdfRunnerPath}).");
        if (!t.Flows.Any(f => f.EventType == FlowEvents.OnError && f.ActionType == FlowActions.RetryTask))
            r.Warn(path + ".flows", "ADF task has no retry on error.");
    }

    private static void ValidateTrigger(ValidationReport r, string path, TriggerDto t, JobDto job, ValidationContext ctx)
    {
        switch (t.Kind)
        {
            case TriggerKinds.Schedule:
                if (t.Schedule == null) { r.Error(path, "Schedule trigger without schedule."); return; }
                ValidateSchedule(r, path + ".schedule", t.Schedule);
                break;
            case TriggerKinds.JobEvent:
                var e = t.JobEvent;
                if (e == null) { r.Error(path, "JobEvent trigger without details."); return; }
                if (!IsGuid(e.UpstreamJobId)) r.Error(path + ".upstreamJobId", "Upstream job ID must be a GUID.");
                else if (e.UpstreamJobId == job.Id) r.Error(path + ".upstreamJobId", "A job cannot depend on itself.");
                else if (ctx.KnownJobs != null && !ctx.KnownJobs.ContainsKey(e.UpstreamJobId))
                    r.Error(path + ".upstreamJobId", $"Upstream job {e.UpstreamJobId} does not exist in {ctx.Environment}.");
                if (e.Condition is not (JobEventConditions.CompletedSuccessfully or JobEventConditions.CompletedFailed or JobEventConditions.Completed or JobEventConditions.Started))
                    r.Error(path + ".condition", $"Unknown dependency condition '{e.Condition}'.");
                break;
            case TriggerKinds.Other:
                if (string.IsNullOrEmpty(t.Id)) r.Error(path, "Triggers of kind Other cannot be created.");
                break;
            default:
                r.Error(path + ".kind", $"Unknown trigger kind '{t.Kind}'.");
                break;
        }
    }

    public static void ValidateSchedule(ValidationReport r, string path, ScheduleDto s)
    {
        switch (s.Mode)
        {
            case ScheduleModes.EveryNMinutes:
                if (s.IntervalMinutes is < 1 or > 1440) r.Error(path + ".intervalMinutes", "Interval must be 1..1440 minutes.");
                return;
            case ScheduleModes.Daily:
            case ScheduleModes.Weekly:
            case ScheduleModes.Monthly:
                if (s.Times.Count == 0) r.Error(path + ".times", "At least one time (HH:mm) is required.");
                if (s.Times.Count > 48) r.Error(path + ".times", "At most 48 times per trigger.");
                foreach (var time in s.Times)
                    if (!TimeRegex().IsMatch(time)) r.Error(path + ".times", $"Invalid time '{time}' (use 24h HH:mm).");
                if (s.Mode == ScheduleModes.Weekly)
                {
                    if (s.DaysOfWeek.Count == 0) r.Error(path + ".daysOfWeek", "Weekly schedules need at least one weekday.");
                    foreach (var d in s.DaysOfWeek)
                        if (!Enum.TryParse<DayOfWeek>(d, true, out _)) r.Error(path + ".daysOfWeek", $"Invalid weekday '{d}'.");
                }
                if (s.Mode == ScheduleModes.Monthly)
                {
                    if (s.DaysOfMonth.Count == 0) r.Error(path + ".daysOfMonth", "Monthly schedules need at least one day.");
                    if (s.DaysOfMonth.Any(d => d is < 1 or > 31)) r.Error(path + ".daysOfMonth", "Days of month must be 1..31.");
                    if (s.DaysOfMonth.Any(d => d > 28)) r.Warn(path + ".daysOfMonth", "Days 29-31 do not occur in every month.");
                }
                return;
            case ScheduleModes.Unmodeled:
                return;
            default:
                r.Error(path + ".mode", $"Unknown schedule mode '{s.Mode}'.");
                return;
        }
    }

    private static void ValidateTimeout(ValidationReport r, string path, TimeoutDto? t)
    {
        if (t == null || !t.Enabled) return;
        if (t.Hours is < 0 or > 168 || t.Minutes is < 0 or > 59 || t.Seconds is < 0 or > 59 || t.Hours + t.Minutes + t.Seconds == 0)
            r.Error(path, "Timeout values are out of range.");
    }

    private static void CheckIds(ValidationReport r, string path, IEnumerable<string> ids, IReadOnlySet<string>? known, string kind)
    {
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !(char.IsLetterOrDigit(c) || c == '-')))
                r.Error(path, $"Invalid {kind} ID '{id}'.");
            else if (known != null && !known.Contains(id))
                r.Error(path, $"{char.ToUpper(kind[0]) + kind[1..]} {id} does not exist on this server.");
        }
    }

    private static void ScanSecrets(ValidationReport r, string path, string? text)
    {
        foreach (var hit in SecretScanner.Scan(text))
            r.Error(path, $"Looks like it contains a secret ({hit}). Store secrets in Azure Key Vault or a VisualCron credential and reference them instead.");
    }
}
