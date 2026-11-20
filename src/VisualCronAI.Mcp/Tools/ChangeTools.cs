using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualCronAI.Core.Models;
using VisualCronAI.Core.Services;

namespace VisualCronAI.Mcp.Tools;

/// <summary>
/// Tools that PREPARE changes. None of them writes to VisualCron: each returns a proposal that must be approved by the
/// human (visualcron_request_approval) and then applied (visualcron_apply_approved_changes).
/// </summary>
[McpServerToolType]
public sealed class ChangeTools(ChangeService service)
{
    private const string Prepares = " Does NOT save anything: returns a proposal to review, approve and apply.";

    [McpServerTool(Name = "visualcron_draft_job")]
    [Description("Builds and validates a new job definition (tasks, schedules, dependencies, retries, failure notifications) and returns a draft with a description and the next run times. Nothing is saved. Then call visualcron_create_job with the draftId.")]
    public Task<string> DraftJob([Description("The job to draft.")] JobDraftInput job, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            var d = await service.DraftJobAsync(job, ct);
            return new
            {
                d.DraftId,
                d.Environment,
                description = d.Description,
                validation = new { valid = d.Validation.IsValid, errors = d.Validation.Errors.Select(e => e.ToString()), warnings = d.Validation.Warnings.Select(e => e.ToString()) },
                nextRuns = d.SchedulePreview == null ? null : new { d.SchedulePreview.ServerTimeZone, times = d.SchedulePreview.NextExecutionsServerTime.Select(x => x.ToString("ddd yyyy-MM-dd HH:mm")) },
                notes = d.Warnings,
                job = d.Job,
                nextStep = d.Validation.IsValid ? "Show the draft to the user; if it is what they want, call visualcron_create_job with this draftId." : "Fix the errors and draft again.",
            };
        });

    [McpServerTool(Name = "visualcron_create_job")]
    [Description("Prepares creation of a drafted job (from visualcron_draft_job)." + Prepares)]
    public Task<string> CreateJob(string draftId, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeCreateAsync(draftId, ct)));

    [McpServerTool(Name = "visualcron_create_adf_job")]
    [Description("Prepares a new ADF job in the team's standard HTTP pattern (token → createRun → runId → wait → status), built from the environment's template job (e.g. 'Products - Trigger ADF'): sets the pipeline, schedule, dependencies, notifications and, by default, polls until the pipeline finishes and fails the job if the pipeline fails. Use this for 'create a job that runs ADF pipeline X'." + Prepares)]
    public Task<string> CreateAdfJob([Description("The ADF job to create.")] CreateAdfJobInput job, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeCreateAdfJobAsync(job, ct)));

    [McpServerTool(Name = "visualcron_clone_job")]
    [Description("Prepares a copy of a job, optionally into another environment (e.g. DEV to UAT). Replaces environment tokens (configured per environment) and explicit find/replace pairs in names, arguments and ADF parameters, retargets ADF tasks to the target environment, removes references that do not exist on the target server, and shows the differences from the source. Clones are created inactive unless activate=true." + Prepares)]
    public Task<string> CloneJob(
        [Description("Source job ID or exact name.")] string sourceJob,
        [Description("Source environment, e.g. DEV.")] string sourceEnvironment,
        [Description("Target environment, e.g. UAT.")] string targetEnvironment,
        [Description("Name of the clone. Default: source name with environment tokens replaced.")] string? newName = null,
        [Description("Job group of the clone.")] string? group = null,
        [Description("Additional find/replace pairs applied to names, descriptions, arguments and ADF parameter values.")] List<ReplacementInput>? replacements = null,
        bool autoReplaceEnvironmentTokens = true,
        bool activate = false,
        CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeCloneAsync(new CloneInput
        {
            SourceEnvironment = sourceEnvironment, SourceJob = sourceJob, TargetEnvironment = targetEnvironment,
            NewName = newName, Group = group, Replacements = replacements, AutoReplaceEnvironmentTokens = autoReplaceEnvironmentTokens, Activate = activate,
        }, ct)));

    [McpServerTool(Name = "visualcron_update_job")]
    [Description("Prepares job-level changes: name, group, description, timeout, failure notifications, and retry policy (e.g. 'retry twice after failures' means retryTimes=2) applied to the job's ADF tasks or all tasks." + Prepares)]
    public Task<string> UpdateJob([Description("Job ID or exact name.")] string job, JobChangesInput changes, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeJobChangesAsync(environment ?? "", job, changes, ct)));

    [McpServerTool(Name = "visualcron_add_task")]
    [Description("Prepares adding a task (ADF pipeline, Execute or PowerShell) to a job at a position (default: last)." + Prepares)]
    public Task<string> AddTask(string job, TaskInput task, [Description("1-based position; default last.")] int? position = null, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeAddTaskAsync(environment ?? "", job, task, position, ct)));

    [McpServerTool(Name = "visualcron_update_task")]
    [Description("Prepares changes to one task: name, active, position, ADF pipeline/parameters/timeout, command, script, credential, retry policy." + Prepares)]
    public Task<string> UpdateTask(string job, [Description("Task ID, name, 1-based position or ADF pipeline name.")] string task, TaskChangesInput changes, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeUpdateTaskAsync(environment ?? "", job, task, changes, ct)));

    [McpServerTool(Name = "visualcron_remove_task", Destructive = true)]
    [Description("Prepares removal of a task from a job." + Prepares)]
    public Task<string> RemoveTask(string job, [Description("Task ID, name, 1-based position or ADF pipeline name.")] string task, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeRemoveTaskAsync(environment ?? "", job, task, ct)));

    [McpServerTool(Name = "visualcron_update_schedule")]
    [Description("Prepares new schedules and/or dependencies for a job. By default replaces existing schedule triggers (when schedules are given) and dependency triggers (when dependsOn is given). Use timeZone for times like '3 AM Central'." + Prepares)]
    public Task<string> UpdateSchedule(string job,
        [Description("New time schedules.")] List<ScheduleInput>? schedules = null,
        [Description("Run when these jobs complete (dependency triggers). Empty list removes dependencies.")] List<DependencyInput>? dependsOn = null,
        bool replaceExisting = true, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeScheduleAsync(environment ?? "", job, schedules, dependsOn, replaceExisting, ct)));

    [McpServerTool(Name = "visualcron_enable_job")]
    [Description("Prepares enabling (activating) a job." + Prepares)]
    public Task<string> EnableJob(string job, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeSetActiveAsync(environment ?? "", job, true, ct)));

    [McpServerTool(Name = "visualcron_disable_job")]
    [Description("Prepares disabling (deactivating) a job." + Prepares)]
    public Task<string> DisableJob(string job, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await service.ProposeSetActiveAsync(environment ?? "", job, false, ct)));
}
