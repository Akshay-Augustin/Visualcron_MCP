using System;
using System.Collections.Generic;

namespace VisualCronAI.Contracts
{
    // NOTE: These DTOs are a deliberately *partial* projection of VisualCron's JobClass/TaskClass/TriggerClass.
    // Only fields the MCP server understands are modeled. When the bridge updates an existing job it maps these
    // fields onto the *existing* vendor objects, so any settings that are not modeled here are preserved.
    // These DTOs never carry secrets: VisualCron credentials are referenced by ID only.

    /// <summary>A full VisualCron job as seen by the MCP server.</summary>
    public sealed class JobDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Active { get; set; }
        public bool RunTasksInOrder { get; set; } = true;
        public bool NotStartIfRunning { get; set; } = true;
        public bool QueueJobs { get; set; }
        public TimeoutDto? Timeout { get; set; }
        public List<TaskDto> Tasks { get; set; } = new List<TaskDto>();
        public List<TriggerDto> Triggers { get; set; } = new List<TriggerDto>();
        /// <summary>IDs of VisualCron notifications fired when the job fails (after any retries).</summary>
        public List<string> ErrorNotificationIds { get; set; } = new List<string>();
        public List<string> CompleteNotificationIds { get; set; } = new List<string>();
        public List<string> StartNotificationIds { get; set; } = new List<string>();
        /// <summary>IDs of VisualCron condition sets attached to the job.</summary>
        public List<string> ConditionSetIds { get; set; } = new List<string>();

        /// <summary>Read-only runtime statistics. Ignored on write.</summary>
        public JobStatsDto? Stats { get; set; }
        /// <summary>Read-only: names of the job's variables (values are never exposed; they may hold secrets).</summary>
        public List<string> VariableNames { get; set; } = new List<string>();
        /// <summary>Read-only: next runs (server-local time) computed by VisualCron's TimeAPI from the job's real triggers.</summary>
        public List<DateTime> NextRunsServerTime { get; set; } = new List<DateTime>();
        /// <summary>Optimistic-concurrency token computed by the bridge from the job's current server state. Ignored on write.</summary>
        public string? VersionToken { get; set; }
    }

    public sealed class JobStatsDto
    {
        public string Status { get; set; } = "";
        public DateTime? LastExecution { get; set; }
        public DateTime? NextExecution { get; set; }
        public int LastExitCode { get; set; }
        public string LastResult { get; set; } = "";
        public long ExecutionCount { get; set; }
        public DateTime? DateCreated { get; set; }
        public DateTime? DateModified { get; set; }
        public string CreatedBy { get; set; } = "";
        public string ModifiedBy { get; set; } = "";
    }

    public sealed class TimeoutDto
    {
        public bool Enabled { get; set; }
        public int Hours { get; set; }
        public int Minutes { get; set; }
        public int Seconds { get; set; }
        /// <summary>Treat a timeout as an error (fires error notifications/flows).</summary>
        public bool ConsiderError { get; set; } = true;
    }

    public static class TaskTypes
    {
        public const string Execute = "Execute";
        public const string PowerShell = "PowerShell";
        public const string Http = "HTTP";
        public const string JsonFilter = "JSONFilter";
        public const string Wait = "Wait";
    }

    /// <summary>Key/value pair (HTTP body field or header). Secret-looking values are masked by the bridge:
    /// <see cref="Masked"/> = true and <see cref="Value"/> = "". Writing a masked pair keeps the server's existing value.</summary>
    public sealed class KeyValueDto
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Masked { get; set; }
    }

    public sealed class HttpSettingsDto
    {
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = "";
        /// <summary>NotSet, formdata, xwwwformurlencoded, raw, binary.</summary>
        public string BodyType { get; set; } = "NotSet";
        public string BodyRaw { get; set; } = "";
        /// <summary>True when the raw body contained secrets and was withheld; writing keeps the server's body.</summary>
        public bool BodyRawMasked { get; set; }
        public List<KeyValueDto> BodyFields { get; set; } = new List<KeyValueDto>();
        public List<KeyValueDto> Headers { get; set; } = new List<KeyValueDto>();
        public string ContentType { get; set; } = "";
        public int TimeoutSeconds { get; set; }
        /// <summary>Read-only: VisualCron HTTP auth type (Basic, OAuth...). Credentials themselves are never exposed.</summary>
        public string AuthType { get; set; } = "";
        public string CredentialId { get; set; } = "";
    }

    public sealed class JsonFilterSettingsDto
    {
        /// <summary>Variable or File.</summary>
        public string DataSource { get; set; } = "Variable";
        /// <summary>Input when DataSource=Variable, e.g. {TASK(&lt;taskId&gt;|StdOut)}.</summary>
        public string Variable { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string JsonPath { get; set; } = "";
        /// <summary>JSONPath or JQ.</summary>
        public string Engine { get; set; } = "JSONPath";
    }

    public sealed class WaitSettingsDto
    {
        public int Hours { get; set; }
        public int Minutes { get; set; }
        public int Seconds { get; set; }
    }

    /// <summary>"Raise an error when the task output matches a condition" (VisualCron task error handling).</summary>
    public sealed class OutputErrorRuleDto
    {
        public bool Enabled { get; set; }
        /// <summary>ConditionCheckT: Equal, NotEqual, Contains, NotContains, RegExMatch, RegExNoMatch...</summary>
        public string ConditionType { get; set; } = "RegExNoMatch";
        public string Value { get; set; } = "";
    }

    public sealed class TaskDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int Order { get; set; }
        public bool Active { get; set; } = true;
        /// <summary>VisualCron TaskT enum name, e.g. "Execute", "PowerShell", "SQL".</summary>
        public string TaskType { get; set; } = TaskTypes.Execute;
        /// <summary>True when the MCP server models this task type's settings and may edit them.</summary>
        public bool Editable { get; set; } = true;
        /// <summary>Read-only human description generated by VisualCron for any task type.</summary>
        public string? Summary { get; set; }
        /// <summary>Read-only live statistics (status, executions, last run/result/output).</summary>
        public TaskRuntimeDto? Runtime { get; set; }
        public ExecuteSettingsDto? Execute { get; set; }
        public PowerShellSettingsDto? PowerShell { get; set; }
        public HttpSettingsDto? Http { get; set; }
        public JsonFilterSettingsDto? JsonFilter { get; set; }
        public WaitSettingsDto? Wait { get; set; }
        /// <summary>Raise a task error based on its output (e.g. pipeline status is not Succeeded).</summary>
        public OutputErrorRuleDto? OutputErrorRule { get; set; }
        /// <summary>Task flow rules (what happens on success/error), including retries.</summary>
        public List<FlowDto> Flows { get; set; } = new List<FlowDto>();
        public TimeoutDto? Timeout { get; set; }
        public List<string> ErrorNotificationIds { get; set; } = new List<string>();
    }

    public sealed class TaskRuntimeDto
    {
        public string Status { get; set; } = "";
        public long Executions { get; set; }
        public DateTime? LastExecution { get; set; }
        public int LastExitCode { get; set; }
        public string LastResult { get; set; } = "";
        /// <summary>Last standard output (truncated; redacted by the MCP host).</summary>
        public string LastOutput { get; set; } = "";
    }

    public sealed class ExecuteSettingsDto
    {
        public string CmdLine { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        /// <summary>ID of a VisualCron network credential to run as (never the credential itself).</summary>
        public string CredentialId { get; set; } = "";
        public bool RunHidden { get; set; } = true;
    }

    public sealed class PowerShellSettingsDto
    {
        /// <summary>"File" or "Text".</summary>
        public string ScriptSource { get; set; } = "File";
        public string File { get; set; } = "";
        public string Text { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string CredentialId { get; set; } = "";
    }

    public static class FlowEvents
    {
        public const string OnStart = "OnStart";
        public const string OnError = "OnError";
        public const string OnSuccess = "OnSuccess";
        public const string OnComplete = "OnComplete";
    }

    public static class FlowActions
    {
        public const string ContinueNextTask = "ContinueNextTask";
        public const string StopJob = "StopJob";
        public const string RetryTask = "RetryTask";
        public const string GotoTask = "GotoTask";
        public const string RunJob = "RunJob";
        public const string RunNotification = "RunNotification";
    }

    /// <summary>Mirrors VisualCron FlowClass (subset).</summary>
    public sealed class FlowDto
    {
        public string Id { get; set; } = "";
        /// <summary>FlowEventT name: OnStart, OnError, OnSuccess, OnComplete.</summary>
        public string EventType { get; set; } = FlowEvents.OnError;
        /// <summary>FlowActionT name: ContinueNextTask, StopJob, RetryTask, GotoTask, RunJob, RunNotification.</summary>
        public string ActionType { get; set; } = FlowActions.StopJob;
        public int RetryTimes { get; set; }
        public int RetryDelayMinutes { get; set; }
        public int RetryDelaySeconds { get; set; }
        /// <summary>RetryFailActionT name used after all retries fail: StopJob or GotoTask.</summary>
        public string RetryFailAction { get; set; } = "StopJob";
        public string TaskId { get; set; } = "";
        public string JobId { get; set; } = "";
        public string NotificationId { get; set; } = "";
        /// <summary>FlowEventConditionT: Always (default), IfExitCode, IfOutput, IfOutputError, IfOtherValue.</summary>
        public string Condition { get; set; } = "Always";
        /// <summary>ConditionCheckT used when Condition is not Always (Equal, RegExMatch...).</summary>
        public string ConditionType { get; set; } = "Equal";
        public string ConditionValue { get; set; } = "";
        /// <summary>Read-only diagnostic: VisualCron's second condition value field.</summary>
        public string? ConditionValue2 { get; set; }
    }

    public static class TriggerKinds
    {
        public const string Schedule = "Schedule";
        public const string JobEvent = "JobEvent";
        /// <summary>Any trigger type the MCP server does not model (file, SQL, mail...). Read-only.</summary>
        public const string Other = "Other";
    }

    public sealed class TriggerDto
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = TriggerKinds.Schedule;
        public bool Active { get; set; } = true;
        /// <summary>Read-only description generated by VisualCron.</summary>
        public string? Description { get; set; }
        public bool Editable { get; set; } = true;
        public ScheduleDto? Schedule { get; set; }
        public JobEventDto? JobEvent { get; set; }
    }

    public static class ScheduleModes
    {
        /// <summary>Every day at the given times.</summary>
        public const string Daily = "Daily";
        /// <summary>On the given weekdays at the given times.</summary>
        public const string Weekly = "Weekly";
        /// <summary>On the given days of the month at the given times.</summary>
        public const string Monthly = "Monthly";
        /// <summary>Every N minutes.</summary>
        public const string EveryNMinutes = "EveryNMinutes";
        /// <summary>A VisualCron time trigger that cannot be expressed by the modes above. Read-only.</summary>
        public const string Unmodeled = "Unmodeled";
    }

    public sealed class ScheduleDto
    {
        public string Mode { get; set; } = ScheduleModes.Daily;
        /// <summary>Times of day as "HH:mm" (24h) in <see cref="TimeZone"/>.</summary>
        public List<string> Times { get; set; } = new List<string>();
        /// <summary>For Weekly: "Monday".."Sunday".</summary>
        public List<string> DaysOfWeek { get; set; } = new List<string>();
        /// <summary>For Monthly: 1..31.</summary>
        public List<int> DaysOfMonth { get; set; } = new List<int>();
        /// <summary>For EveryNMinutes.</summary>
        public int IntervalMinutes { get; set; }
        /// <summary>Windows time zone ID the times are expressed in (e.g. "Central Standard Time").
        /// Empty means the VisualCron server's local time zone.</summary>
        public string TimeZone { get; set; } = "";
    }

    public static class JobEventConditions
    {
        public const string CompletedSuccessfully = "JobCompletedSuccessfully";
        public const string CompletedFailed = "JobCompletedFailed";
        public const string Completed = "JobCompleted";
        public const string Started = "JobStarted";
    }

    /// <summary>Dependency trigger: fire this job when another job raises an event.</summary>
    public sealed class JobEventDto
    {
        public string UpstreamJobId { get; set; } = "";
        public string UpstreamJobName { get; set; } = "";
        public string Condition { get; set; } = JobEventConditions.CompletedSuccessfully;
    }

    public sealed class JobSummaryDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public bool Active { get; set; }
        public string Status { get; set; } = "";
        public DateTime? LastExecution { get; set; }
        public DateTime? NextExecution { get; set; }
        public int LastExitCode { get; set; }
        public string LastResult { get; set; } = "";
        public int TaskCount { get; set; }
        public int TriggerCount { get; set; }
    }

    public sealed class JobHistoryEntryDto
    {
        public string ExecutionId { get; set; } = "";
        public DateTime Started { get; set; }
        public double ExecutionSeconds { get; set; }
        public int ExitCode { get; set; }
        public string Result { get; set; } = "";
    }

    public sealed class TaskLogEntryDto
    {
        public string TaskId { get; set; } = "";
        public string TaskName { get; set; } = "";
        public string ExecutionId { get; set; } = "";
        public DateTime Started { get; set; }
        public int ExitCode { get; set; }
        public string Result { get; set; } = "";
        public string StandardOutput { get; set; } = "";
        public string StandardError { get; set; } = "";
        public bool Truncated { get; set; }
    }

    public sealed class NamedRefDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }
}
