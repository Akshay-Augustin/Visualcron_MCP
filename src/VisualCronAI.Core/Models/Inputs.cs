using System.ComponentModel;

namespace VisualCronAI.Core.Models;

// Tool input models. All values arrive from the AI model and are treated as untrusted; they are validated by
// JobSpecBuilder/JobValidator before anything can become an approvable proposal.

public sealed class TaskInput
{
    [Description("Task name. Defaults to 'Run ADF pipeline <name>' for ADF tasks.")]
    public string? Name { get; set; }
    [Description("AdfPipeline (default), Execute or PowerShell.")]
    public string Type { get; set; } = "AdfPipeline";
    [Description("AdfPipeline: ADF pipeline name, e.g. p_D365_Product_Load.")]
    public string? PipelineName { get; set; }
    [Description("AdfPipeline: pipeline parameters. Key may carry a type suffix (name:int, name:bool, name:float, name:json). Values may use VisualCron variables like {DATEFORMAT(yyyy-MM-dd)}. Never put secrets here.")]
    public Dictionary<string, string>? Parameters { get; set; }
    [Description("AdfPipeline: max minutes to wait for the pipeline before failing (default 120).")]
    public int? TimeoutMinutes { get; set; }
    [Description("AdfPipeline: seconds between status polls (default 30).")]
    public int? PollSeconds { get; set; }
    [Description("Execute: program to run.")]
    public string? CmdLine { get; set; }
    [Description("Execute: arguments.")]
    public string? Arguments { get; set; }
    [Description("Execute/PowerShell: working directory.")]
    public string? WorkingDirectory { get; set; }
    [Description("Execute/PowerShell: ID of an existing VisualCron credential to run as (see visualcron_list_credentials).")]
    public string? CredentialId { get; set; }
    [Description("PowerShell: script file path on the VisualCron server.")]
    public string? ScriptFile { get; set; }
    [Description("PowerShell: inline script text.")]
    public string? ScriptText { get; set; }
    [Description("Retries after a failure (0-10). Default 0 for Execute/PowerShell, 0 for ADF unless specified.")]
    public int? RetryTimes { get; set; }
    [Description("Minutes between retries (default 5).")]
    public int? RetryDelayMinutes { get; set; }
    [Description("If true and no retries remain, continue with the next task instead of stopping the job.")]
    public bool? ContinueOnError { get; set; }
    public bool? Active { get; set; }
}

public sealed class ScheduleInput
{
    [Description("Daily, Weekly, Monthly or EveryNMinutes.")]
    public string Mode { get; set; } = "Daily";
    [Description("Times of day, 24h HH:mm, e.g. [\"02:00\"].")]
    public List<string>? Times { get; set; }
    [Description("Weekly: Monday..Sunday.")]
    public List<string>? DaysOfWeek { get; set; }
    [Description("Monthly: 1..31.")]
    public List<int>? DaysOfMonth { get; set; }
    [Description("EveryNMinutes: interval in minutes (1-1440).")]
    public int? IntervalMinutes { get; set; }
    [Description("Time zone of the times: Windows ID (Central Standard Time), IANA ID (America/Chicago) or CT/ET/MT/PT/UTC. Empty = VisualCron server time.")]
    public string? TimeZone { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class DependencyInput
{
    [Description("Upstream job ID or exact job name (same environment).")]
    public string Job { get; set; } = "";
    [Description("JobCompletedSuccessfully (default), JobCompletedFailed, JobCompleted or JobStarted.")]
    public string Condition { get; set; } = "JobCompletedSuccessfully";
}

public sealed class JobDraftInput
{
    [Description("Target environment, e.g. DEV.")]
    public string Environment { get; set; } = "";
    [Description("Job name, e.g. D365_Product_Daily_Load.")]
    public string Name { get; set; } = "";
    [Description("VisualCron job group (folder). Defaults to the environment's DefaultJobGroup.")]
    public string? Group { get; set; }
    public string? Description { get; set; }
    [Description("Tasks in execution order. Tasks run sequentially; a failed task stops the job unless ContinueOnError.")]
    public List<TaskInput> Tasks { get; set; } = [];
    [Description("Time-based schedules.")]
    public List<ScheduleInput>? Schedules { get; set; }
    [Description("Run this job when other jobs complete (dependency triggers).")]
    public List<DependencyInput>? DependsOn { get; set; }
    [Description("IDs of existing VisualCron notifications to send when the job fails (see visualcron_list_notifications).")]
    public List<string>? ErrorNotificationIds { get; set; }
    [Description("Create the job active (default true).")]
    public bool Active { get; set; } = true;
    [Description("Job timeout in minutes (0/null = none).")]
    public int? TimeoutMinutes { get; set; }
}

public sealed class JobChangesInput
{
    public string? Name { get; set; }
    public string? Group { get; set; }
    public string? Description { get; set; }
    public bool? RunTasksInOrder { get; set; }
    public bool? NotStartIfRunning { get; set; }
    public bool? QueueJobs { get; set; }
    [Description("Job timeout in minutes; 0 disables the timeout.")]
    public int? TimeoutMinutes { get; set; }
    [Description("Replace the job's failure notifications with these notification IDs.")]
    public List<string>? ErrorNotificationIds { get; set; }
    [Description("Add these notification IDs to the job's failure notifications.")]
    public List<string>? AddErrorNotificationIds { get; set; }
    [Description("Remove these notification IDs from the job's failure notifications.")]
    public List<string>? RemoveErrorNotificationIds { get; set; }
    [Description("Set the retry policy: number of retries after a task failure (0-10).")]
    public int? RetryTimes { get; set; }
    [Description("Minutes between retries (default 5).")]
    public int? RetryDelayMinutes { get; set; }
    [Description("Which tasks get the retry policy: AdfTasks (default) or AllTasks.")]
    public string? RetryScope { get; set; }
    [Description("HTTP-pattern ADF jobs: true = poll the pipeline status until it finishes and fail the job if the pipeline fails (instead of one check after a fixed wait).")]
    public bool? AdfPollUntilComplete { get; set; }
    [Description("Minutes between status polls (default from environment, usually 1).")]
    public int? AdfPollMinutes { get; set; }
    [Description("Maximum pipeline run time in minutes before the job fails (default from environment, usually 180).")]
    public int? AdfMaxRunMinutes { get; set; }
    [Description("HTTP-pattern ADF jobs: change the pipeline that is triggered.")]
    public string? AdfPipelineName { get; set; }
    [Description("HTTP-pattern ADF jobs: true = point the createRun/status URLs at this environment's configured ADF factory (AdfSubscriptionId/AdfResourceGroup/AdfFactoryName).")]
    public bool? AdfRetargetToEnvironmentFactory { get; set; }
}

/// <summary>New ADF job created from the environment's template job (team's HTTP-task pattern).</summary>
public sealed class CreateAdfJobInput
{
    [Description("Environment, e.g. DEV.")]
    public string Environment { get; set; } = "";
    [Description("New job name, e.g. 'Customers - Trigger ADF'.")]
    public string Name { get; set; } = "";
    [Description("ADF pipeline to trigger, e.g. s_D365_Customers_Blob_DataFlow.")]
    public string PipelineName { get; set; } = "";
    [Description("Pipeline parameters for the createRun body (values may use VisualCron variables). Never secrets.")]
    public Dictionary<string, string>? Parameters { get; set; }
    [Description("Schedules; default = copy the template's schedule.")]
    public List<ScheduleInput>? Schedules { get; set; }
    [Description("Run after these jobs complete (same environment).")]
    public List<DependencyInput>? DependsOn { get; set; }
    [Description("Failure notification IDs (see visualcron_list_notifications).")]
    public List<string>? ErrorNotificationIds { get; set; }
    [Description("Job group; default = the template's group.")]
    public string? Group { get; set; }
    [Description("Template job (ID or name); default = the environment's AdfTemplateJob.")]
    public string? TemplateJob { get; set; }
    [Description("Poll until the pipeline finishes and fail on pipeline failure (default true).")]
    public bool PollUntilComplete { get; set; } = true;
    public int? PollMinutes { get; set; }
    public int? MaxRunMinutes { get; set; }
    [Description("Create active (default false: review first, then visualcron_enable_job).")]
    public bool Active { get; set; }
}

public sealed class TaskChangesInput
{
    public string? Name { get; set; }
    public bool? Active { get; set; }
    [Description("New 1-based position of the task.")]
    public int? Position { get; set; }
    [Description("ADF: change the pipeline name.")]
    public string? PipelineName { get; set; }
    [Description("ADF: add or change these pipeline parameters.")]
    public Dictionary<string, string>? SetParameters { get; set; }
    [Description("ADF: remove these pipeline parameters.")]
    public List<string>? RemoveParameters { get; set; }
    [Description("ADF: timeout in minutes.")]
    public int? TimeoutMinutes { get; set; }
    public string? CmdLine { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? CredentialId { get; set; }
    public string? ScriptFile { get; set; }
    public string? ScriptText { get; set; }
    public int? RetryTimes { get; set; }
    public int? RetryDelayMinutes { get; set; }
    public bool? ContinueOnError { get; set; }
}

public sealed class ReplacementInput
{
    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
}

public sealed class CloneInput
{
    public string SourceEnvironment { get; set; } = "";
    public string SourceJob { get; set; } = "";
    public string TargetEnvironment { get; set; } = "";
    public string? NewName { get; set; }
    public string? Group { get; set; }
    public List<ReplacementInput>? Replacements { get; set; }
    public bool AutoReplaceEnvironmentTokens { get; set; } = true;
    public bool Activate { get; set; }
}
