using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Adf;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Diff;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Models;
using VisualCronAI.Core.Validation;

namespace VisualCronAI.Core.Services;

/// <summary>
/// Two-phase change workflow, enforced server-side:
///   prepare (draft/propose) → preview + validate → human approval (MCP elicitation or Windows dialog) → apply.
/// The AI model can prepare and preview, but cannot approve: approvals come only from <see cref="IApprovalPrompt"/>
/// channels that ask the human directly. Apply re-checks everything (status, expiry, content hash, environment write
/// switch, production approver, optimistic concurrency) and writes an audit record for every step.
/// </summary>
public sealed class ChangeService(
    IVisualCronGateway gateway,
    EnvironmentRegistry environments,
    ProposalStore proposals,
    DraftStore drafts,
    IAuditLog audit,
    ICurrentUser user,
    IClock clock,
    ILogger<ChangeService> logger)
{
    private static readonly SemaphoreSlim ApplyLock = new(1, 1);
    private static readonly JsonSerializerOptions HashJson = new() { WriteIndented = false };

    // ============================================================ drafts / create

    public sealed class DraftResult
    {
        public string DraftId { get; init; } = "";
        public string Environment { get; init; } = "";
        public JobDto Job { get; init; } = new();
        public List<string> Description { get; init; } = [];
        public ValidationReport Validation { get; init; } = new();
        public SchedulePreviewDto? SchedulePreview { get; init; }
        public List<string> Warnings { get; init; } = [];
    }

    public async Task<DraftResult> DraftJobAsync(JobDraftInput input, CancellationToken ct)
    {
        string env = environments.Resolve(input.Environment);
        var envOptions = environments.Get(env);
        var server = await gateway.ConnectAsync(env, ct);
        var warnings = new List<string>();

        if (input.Tasks.Count == 0) throw new VisualCronAIException("Describe at least one task.");
        var job = new JobDto
        {
            Name = (input.Name ?? "").Trim(),
            Group = string.IsNullOrWhiteSpace(input.Group) ? envOptions.DefaultJobGroup : input.Group.Trim(),
            Description = input.Description?.Trim() ?? "",
            Active = input.Active,
            RunTasksInOrder = true,
            NotStartIfRunning = true,
            ErrorNotificationIds = input.ErrorNotificationIds?.Select(x => x.Trim()).ToList() ?? [],
            Timeout = input.TimeoutMinutes is > 0 ? new TimeoutDto { Enabled = true, Hours = input.TimeoutMinutes.Value / 60, Minutes = input.TimeoutMinutes.Value % 60, ConsiderError = true } : null,
        };
        for (int i = 0; i < input.Tasks.Count; i++)
        {
            var t = JobSpecBuilder.BuildTask(input.Tasks[i], env, envOptions);
            t.Id = $"draft-task-{i + 1}";
            job.Tasks.Add(t);
        }
        JobSpecBuilder.Renumber(job.Tasks);

        if (input.Schedules is { Count: > 0 })
        {
            var (triggers, w) = JobSpecBuilder.BuildSchedules(input.Schedules, server, clock.UtcNow.UtcDateTime);
            job.Triggers.AddRange(triggers);
            warnings.AddRange(w);
        }
        if (input.DependsOn is { Count: > 0 })
            job.Triggers.AddRange(await BuildDependenciesAsync(env, input.DependsOn, ct));

        var (validation, preview) = await ValidateAndPreviewAsync(env, envOptions, job, before: null, isNew: true, ct);
        var draft = new JobDraft { Id = ProposalStore.NewId("drf"), Environment = env, Job = job, CreatedAt = clock.UtcNow };
        drafts.Add(draft);
        return new DraftResult
        {
            DraftId = draft.Id,
            Environment = env,
            Job = job,
            Description = JobComparer.DescribeJob(job),
            Validation = validation,
            SchedulePreview = preview,
            Warnings = warnings,
        };
    }

    public async Task<ChangeProposal> ProposeCreateAsync(string draftId, CancellationToken ct)
    {
        var draft = drafts.Get(draftId) ?? throw new VisualCronAIException($"Draft '{draftId}' not found (drafts are kept in memory for this session only).");
        var envOptions = environments.Get(draft.Environment);
        var existing = await gateway.ListJobsAsync(draft.Environment, draft.Job.Name, null, ct);
        if (existing.Any(j => SameNameAndGroup(j.Name, j.Group, draft.Job.Name, draft.Job.Group)))
            throw new VisualCronAIException($"A job named '{draft.Job.Name}' already exists in group '{draft.Job.Group}'. Choose another name or modify the existing job.");

        var after = Copy(draft.Job);
        return await CreateProposalAsync(ProposalOperation.Create, draft.Environment, envOptions, before: null, after,
            $"Create job '{after.Name}' in {draft.Environment}", JobComparer.DescribeJob(after), ct);
    }

    // ============================================================ updates

    /// <summary>Generic update: load current job, let <paramref name="mutate"/> change a copy, then build a proposal.</summary>
    public async Task<ChangeProposal> ProposeUpdateAsync(string environment, string jobRef, string summaryVerb,
        Func<JobDto, ServerInfoDto, EnvironmentOptions, Task<List<string>>> mutate, CancellationToken ct)
    {
        string env = environments.Resolve(environment);
        var envOptions = environments.Get(env);
        var server = await gateway.ConnectAsync(env, ct);
        var before = await ResolveJobAsync(env, jobRef, ct);
        var after = Copy(before);
        var notes = await mutate(after, server, envOptions);
        JobSpecBuilder.Renumber(after.Tasks = after.Tasks.OrderBy(t => t.Order).ToList());

        var diff = JobComparer.Compare(before, after);
        if (diff.Count == 0) throw new VisualCronAIException("The requested change does not modify the job.");
        var proposal = await CreateProposalAsync(ProposalOperation.Update, env, envOptions, before, after,
            $"{summaryVerb} job '{before.Name}' in {env}", diff.Select(d => d.ToString()).ToList(), ct, notes);
        return proposal;
    }

    public Task<ChangeProposal> ProposeJobChangesAsync(string environment, string jobRef, JobChangesInput c, CancellationToken ct) =>
        ProposeUpdateAsync(environment, jobRef, "Update", (job, _, _) =>
        {
            var notes = new List<string>();
            if (c.Name != null) job.Name = c.Name.Trim();
            if (c.Group != null) job.Group = c.Group.Trim();
            if (c.Description != null) job.Description = c.Description.Trim();
            if (c.RunTasksInOrder != null) job.RunTasksInOrder = c.RunTasksInOrder.Value;
            if (c.NotStartIfRunning != null) job.NotStartIfRunning = c.NotStartIfRunning.Value;
            if (c.QueueJobs != null) job.QueueJobs = c.QueueJobs.Value;
            if (c.TimeoutMinutes != null)
            {
                int m = c.TimeoutMinutes.Value;
                if (m is < 0 or > 10080) throw new VisualCronAIException("TimeoutMinutes must be 0..10080.");
                job.Timeout = m == 0 ? new TimeoutDto { Enabled = false } : new TimeoutDto { Enabled = true, Hours = m / 60, Minutes = m % 60, ConsiderError = true };
            }
            if (c.ErrorNotificationIds != null) job.ErrorNotificationIds = c.ErrorNotificationIds.Select(x => x.Trim()).Distinct().ToList();
            foreach (var id in c.AddErrorNotificationIds ?? []) if (!job.ErrorNotificationIds.Contains(id.Trim())) job.ErrorNotificationIds.Add(id.Trim());
            foreach (var id in c.RemoveErrorNotificationIds ?? []) job.ErrorNotificationIds.Remove(id.Trim());
            if (c.AdfPipelineName != null) AdfHttpPattern.SetPipeline(job, c.AdfPipelineName.Trim());
            if (c.AdfRetargetToEnvironmentFactory == true)
            {
                var te = environments.Get(environment);
                if (string.IsNullOrWhiteSpace(te.AdfResourceGroup) || string.IsNullOrWhiteSpace(te.AdfFactoryName))
                    throw new VisualCronAIException($"ADF factory for {environment} is not configured (AdfResourceGroup/AdfFactoryName).");
                if (AdfHttpPattern.TryRead(job) == null) throw new VisualCronAIException($"Job '{job.Name}' does not call ADF through HTTP tasks.");
                int n = AdfHttpPattern.Retarget(job, te.AdfSubscriptionId, te.AdfResourceGroup, te.AdfFactoryName);
                notes.Add(n == 0 ? $"ADF URLs already point at {te.AdfResourceGroup}/{te.AdfFactoryName}."
                                 : $"{n} ADF URL(s) retargeted to {te.AdfResourceGroup}/{te.AdfFactoryName}.");
            }
            if (c.AdfPollUntilComplete == true)
            {
                notes.AddRange(AdfHttpPattern.RepairReferences(job));
                var eo = environments.Get(environment);
                AdfHttpPattern.ApplyPolling(job, c.AdfPollMinutes ?? eo.AdfPollMinutes, c.AdfMaxRunMinutes ?? eo.AdfMaxRunMinutes);
                notes.Add("The status check now loops until the pipeline finishes and raises an error for Failed/Cancelled. " +
                          "This uses VisualCron flow conditions — verify on DEV with one run before rolling out.");
            }
            if (c.RetryTimes != null)
            {
                bool all = string.Equals(c.RetryScope, "AllTasks", StringComparison.OrdinalIgnoreCase);
                var targets = job.Tasks.Where(t => all || JobSpecBuilder.IsAdfTask(t)).ToList();
                if (!all && targets.Count == 0 && AdfHttpPattern.TryRead(job) is { } hv) targets = [hv.CreateRunTask];
                if (targets.Count == 0)
                    throw new VisualCronAIException(all ? "The job has no tasks." : "The job has no ADF tasks; use RetryScope=AllTasks to apply the retry policy to every task.");
                foreach (var t in targets)
                    AdfTaskFactory.SetErrorPolicy(t, c.RetryTimes.Value, c.RetryDelayMinutes ?? JobSpecBuilder.DefaultRetryDelayMinutes, continueOnError: false);
                notes.Add($"Retry policy applied to {targets.Count} task(s). After the last retry fails the job stops and the job's failure notifications are sent.");
            }
            return Task.FromResult(notes);
        }, ct);

    public Task<ChangeProposal> ProposeAddTaskAsync(string environment, string jobRef, TaskInput input, int? position, CancellationToken ct) =>
        ProposeUpdateAsync(environment, jobRef, "Add task to", (job, _, envOptions) =>
        {
            var task = JobSpecBuilder.BuildTask(input, environments.Resolve(environment), envOptions);
            if (job.Tasks.Any(t => string.Equals(t.Name, task.Name, StringComparison.OrdinalIgnoreCase)))
                throw new VisualCronAIException($"The job already has a task named '{task.Name}'.");
            var ordered = job.Tasks.OrderBy(t => t.Order).ToList();
            int index = position is > 0 ? Math.Min(position.Value - 1, ordered.Count) : ordered.Count;
            ordered.Insert(index, task);
            JobSpecBuilder.Renumber(ordered);
            job.Tasks = ordered;
            return Task.FromResult(new List<string>());
        }, ct);

    public Task<ChangeProposal> ProposeUpdateTaskAsync(string environment, string jobRef, string taskRef, TaskChangesInput c, CancellationToken ct) =>
        ProposeUpdateAsync(environment, jobRef, "Update task in", (job, _, _) =>
        {
            var task = FindTask(job, taskRef);
            bool typeSpecific = c.PipelineName != null || c.SetParameters != null || c.RemoveParameters != null || c.TimeoutMinutes != null
                                || c.CmdLine != null || c.Arguments != null || c.WorkingDirectory != null || c.CredentialId != null
                                || c.ScriptFile != null || c.ScriptText != null;
            if (typeSpecific && !task.Editable)
                throw new VisualCronAIException($"Task '{task.Name}' is a {task.TaskType} task; only its name, position, active state and retry policy can be changed here. Edit other settings in the VisualCron client.");

            if (c.Name != null) task.Name = c.Name.Trim();
            if (c.Active != null) task.Active = c.Active.Value;
            bool isAdf = JobSpecBuilder.IsAdfTask(task);
            var httpAdf = AdfHttpPattern.TryRead(job);
            if (!isAdf && httpAdf != null && httpAdf.CreateRunTask.Id == task.Id && (c.PipelineName != null || c.SetParameters != null || c.RemoveParameters != null))
            {
                if (c.PipelineName != null) AdfHttpPattern.SetPipeline(job, c.PipelineName.Trim());
                if (c.SetParameters != null || c.RemoveParameters != null)
                    AdfHttpPattern.SetParameters(job, c.SetParameters ?? new Dictionary<string, string>(), c.RemoveParameters);
            }
            else if (c.PipelineName != null || c.SetParameters != null || c.RemoveParameters != null || c.TimeoutMinutes != null)
            {
                if (!isAdf) throw new VisualCronAIException($"Task '{task.Name}' is not an ADF pipeline task.");
                AdfTaskFactory.Modify(task, a =>
                {
                    if (c.PipelineName != null) a.PipelineName = c.PipelineName.Trim();
                    foreach (var (k, v) in c.SetParameters ?? []) a.Parameters[k.Trim()] = v;
                    foreach (var k in c.RemoveParameters ?? [])
                    {
                        var key = a.Parameters.Keys.FirstOrDefault(x => VisualCronAI.Adf.Models.AdfRunnerArguments.ParameterName(x) == k.Trim() || x == k.Trim());
                        if (key == null) throw new VisualCronAIException($"Parameter '{k}' is not set on task '{task.Name}'.");
                        a.Parameters.Remove(key);
                    }
                    if (c.TimeoutMinutes != null) a.TimeoutMinutes = c.TimeoutMinutes.Value;
                });
            }
            if (task.TaskType == TaskTypes.Execute && task.Execute != null)
            {
                if (c.CmdLine != null) task.Execute.CmdLine = c.CmdLine.Trim();
                if (c.Arguments != null) task.Execute.Arguments = c.Arguments;
                if (c.WorkingDirectory != null) task.Execute.WorkingDirectory = c.WorkingDirectory;
                if (c.CredentialId != null) task.Execute.CredentialId = c.CredentialId.Trim();
            }
            else if (task.TaskType == TaskTypes.PowerShell && task.PowerShell != null)
            {
                if (c.ScriptFile != null) { task.PowerShell.ScriptSource = "File"; task.PowerShell.File = c.ScriptFile; }
                if (c.ScriptText != null) { task.PowerShell.ScriptSource = "Text"; task.PowerShell.Text = c.ScriptText; }
                if (c.WorkingDirectory != null) task.PowerShell.WorkingDirectory = c.WorkingDirectory;
                if (c.CredentialId != null) task.PowerShell.CredentialId = c.CredentialId.Trim();
            }
            if (c.RetryTimes != null || c.ContinueOnError != null)
                AdfTaskFactory.SetErrorPolicy(task, c.RetryTimes ?? 0, c.RetryDelayMinutes ?? JobSpecBuilder.DefaultRetryDelayMinutes, c.ContinueOnError ?? false);
            if (c.Position != null)
            {
                var ordered = job.Tasks.OrderBy(t => t.Order).ToList();
                ordered.Remove(task);
                ordered.Insert(Math.Clamp(c.Position.Value - 1, 0, ordered.Count), task);
                JobSpecBuilder.Renumber(ordered);
                job.Tasks = ordered;
            }
            return Task.FromResult(new List<string>());
        }, ct);

    public Task<ChangeProposal> ProposeRemoveTaskAsync(string environment, string jobRef, string taskRef, CancellationToken ct) =>
        ProposeUpdateAsync(environment, jobRef, "Remove task from", (job, _, _) =>
        {
            var task = FindTask(job, taskRef);
            job.Tasks.Remove(task);
            var dangling = job.Tasks.SelectMany(t => t.Flows).Where(f => f.ActionType == FlowActions.GotoTask && f.TaskId == task.Id).ToList();
            var notes = new List<string>();
            if (dangling.Count > 0) notes.Add($"{dangling.Count} 'go to task' flow(s) pointed at the removed task and will fail validation; adjust them first.");
            return Task.FromResult(notes);
        }, ct);

    public Task<ChangeProposal> ProposeScheduleAsync(string environment, string jobRef, List<ScheduleInput>? schedules,
        List<DependencyInput>? dependsOn, bool replaceExisting, CancellationToken ct) =>
        ProposeUpdateAsync(environment, jobRef, "Reschedule", async (job, server, _) =>
        {
            var notes = new List<string>();
            if (schedules == null && dependsOn == null) throw new VisualCronAIException("Provide schedules and/or dependsOn.");
            string env = environments.Resolve(environment);
            if (schedules != null)
            {
                if (replaceExisting)
                {
                    int removed = job.Triggers.RemoveAll(t => t.Kind == TriggerKinds.Schedule);
                    if (removed > 0) notes.Add($"{removed} existing schedule trigger(s) replaced.");
                }
                var (triggers, w) = JobSpecBuilder.BuildSchedules(schedules, server, clock.UtcNow.UtcDateTime);
                job.Triggers.AddRange(triggers);
                notes.AddRange(w);
            }
            if (dependsOn != null)
            {
                if (replaceExisting) job.Triggers.RemoveAll(t => t.Kind == TriggerKinds.JobEvent);
                var deps = await BuildDependenciesAsync(env, dependsOn, ct);
                if (deps.Any(d => d.JobEvent!.UpstreamJobId == job.Id)) throw new VisualCronAIException("A job cannot depend on itself.");
                job.Triggers.AddRange(deps);
            }
            return notes;
        }, ct);

    public async Task<ChangeProposal> ProposeSetActiveAsync(string environment, string jobRef, bool active, CancellationToken ct)
    {
        string env = environments.Resolve(environment);
        var envOptions = environments.Get(env);
        var before = await ResolveJobAsync(env, jobRef, ct);
        if (before.Active == active) throw new VisualCronAIException($"Job '{before.Name}' is already {(active ? "enabled" : "disabled")}.");
        var after = Copy(before);
        after.Active = active;
        var op = active ? ProposalOperation.Enable : ProposalOperation.Disable;
        return await CreateProposalAsync(op, env, envOptions, before, after,
            $"{(active ? "Enable" : "Disable")} job '{before.Name}' in {env}", [$"[Job] ~ active: {before.Active} → {active}"], ct);
    }

    // ============================================================ clone

    public async Task<ChangeProposal> ProposeCloneAsync(CloneInput input, CancellationToken ct)
    {
        string srcEnv = environments.Resolve(input.SourceEnvironment);
        string dstEnv = environments.Resolve(input.TargetEnvironment);
        var src = environments.Get(srcEnv);
        var dst = environments.Get(dstEnv);
        await gateway.ConnectAsync(dstEnv, ct);
        var source = await ResolveJobAsync(srcEnv, input.SourceJob, ct);
        var after = Copy(source);
        var notes = new List<string>();
        bool sameServer = string.Equals(src.Address, dst.Address, StringComparison.OrdinalIgnoreCase) && src.Port == dst.Port;
        if (sameServer && srcEnv != dstEnv && dst.JobGroups.Count == 0)
            notes.Add($"{srcEnv} and {dstEnv} share a VisualCron server; configure JobGroups for each environment so they are kept apart.");

        // 1. Text replacements: explicit ones first, then positional environment tokens (DEV.Tokens[i] -> UAT.Tokens[i]).
        var replacements = new List<(string Find, string Replace)>();
        foreach (var r in input.Replacements ?? [])
        {
            if (string.IsNullOrEmpty(r.Find)) throw new VisualCronAIException("Replacement 'Find' must not be empty.");
            replacements.Add((r.Find, r.Replace ?? ""));
        }
        if (input.AutoReplaceEnvironmentTokens && srcEnv != dstEnv)
        {
            for (int i = 0; i < Math.Min(src.EnvironmentTokens.Count, dst.EnvironmentTokens.Count); i++)
                if (!string.IsNullOrEmpty(src.EnvironmentTokens[i])) replacements.Add((src.EnvironmentTokens[i], dst.EnvironmentTokens[i]));
            if (src.EnvironmentTokens.Count == 0) notes.Add($"No EnvironmentTokens configured for {srcEnv}; only explicit replacements were applied.");
        }
        string R(string s) { foreach (var (f, r) in replacements) s = s.Replace(f, r, StringComparison.Ordinal); return s; }

        after.Name = string.IsNullOrWhiteSpace(input.NewName) ? R(source.Name) : input.NewName.Trim();
        // Group: explicit > source group with tokens replaced (e.g. "DEV: ADF-D365 Loads" -> "UAT: ADF-D365 Loads")
        // when that lands in the target environment's groups > target default group.
        string mappedGroup = R(source.Group);
        if (!string.IsNullOrWhiteSpace(input.Group)) after.Group = input.Group.Trim();
        else if (srcEnv == dstEnv) after.Group = source.Group;
        else if (GroupPattern.MatchesAny(dst.JobGroups, mappedGroup)) after.Group = mappedGroup;
        else after.Group = string.IsNullOrWhiteSpace(dst.DefaultJobGroup) ? mappedGroup : dst.DefaultJobGroup;
        if (sameServer && SameNameAndGroup(after.Name, after.Group, source.Name, source.Group))
            throw new VisualCronAIException($"The clone would have the same name and group as the source ('{after.Name}' in '{after.Group}'); pass NewName or Group.");
        after.Description = R(source.Description);
        after.Active = input.Activate;
        foreach (var t in after.Tasks)
        {
            t.Name = R(t.Name);
            if (!t.Editable) continue;
            if (AdfTaskFactory.Retarget(t, dstEnv, dst))
            {
                AdfTaskFactory.Modify(t, a =>
                {
                    foreach (var key in a.Parameters.Keys.ToList()) a.Parameters[key] = R(a.Parameters[key]);
                });
            }
            else if (t.Execute != null)
            {
                t.Execute.CmdLine = R(t.Execute.CmdLine);
                t.Execute.Arguments = R(t.Execute.Arguments);
                t.Execute.WorkingDirectory = R(t.Execute.WorkingDirectory);
            }
            if (t.Http != null)
            {
                t.Http.Url = R(t.Http.Url);
                if (!t.Http.BodyRawMasked) t.Http.BodyRaw = R(t.Http.BodyRaw);
                foreach (var kv in t.Http.BodyFields.Concat(t.Http.Headers).Where(kv => !kv.Masked)) kv.Value = R(kv.Value);
            }
            if (t.PowerShell != null)
            {
                t.PowerShell.File = R(t.PowerShell.File);
                t.PowerShell.Text = R(t.PowerShell.Text);
                t.PowerShell.WorkingDirectory = R(t.PowerShell.WorkingDirectory);
            }
        }

        // 1b. HTTP-pattern ADF jobs: point createRun / pipelineruns URLs at the target environment's factory.
        if (srcEnv != dstEnv && AdfHttpPattern.TryRead(after) is { } adfView)
        {
            if (string.IsNullOrWhiteSpace(dst.AdfResourceGroup) && string.IsNullOrWhiteSpace(dst.AdfFactoryName))
                notes.Add($"ADF target for {dstEnv} is not configured (AdfSubscriptionId/AdfResourceGroup/AdfFactoryName); the clone still calls {adfView.ResourceGroup}/{adfView.FactoryName}.");
            else
            {
                int n = AdfHttpPattern.Retarget(after, dst.AdfSubscriptionId, dst.AdfResourceGroup, dst.AdfFactoryName);
                notes.Add($"{n} ADF URL(s) retargeted to {dst.AdfResourceGroup}/{dst.AdfFactoryName}.");
            }
        }

        // 2. Server-specific references (notifications, credentials, conditions, upstream jobs) do not exist on another server.
        if (!sameServer)
        {
            var notifications = (await SafeList(() => gateway.ListNotificationsAsync(dstEnv, ct)))?.Select(n => n.Id).ToHashSet();
            var credentials = (await SafeList(() => gateway.ListCredentialsAsync(dstEnv, ct)))?.Select(n => n.Id).ToHashSet();
            void Prune(List<string> ids, HashSet<string>? known, string what)
            {
                var missing = ids.Where(id => known == null || !known.Contains(id)).ToList();
                foreach (var m in missing) ids.Remove(m);
                if (missing.Count > 0) notes.Add($"{missing.Count} {what} reference(s) do not exist in {dstEnv} and were removed; re-attach them with visualcron_update_job.");
            }
            Prune(after.ErrorNotificationIds, notifications, "failure notification");
            Prune(after.CompleteNotificationIds, notifications, "completion notification");
            Prune(after.StartNotificationIds, notifications, "start notification");
            if (after.ConditionSetIds.Count > 0) { notes.Add($"{after.ConditionSetIds.Count} condition set(s) removed (server-specific)."); after.ConditionSetIds.Clear(); }
            foreach (var t in after.Tasks)
            {
                Prune(t.ErrorNotificationIds, notifications, $"task '{t.Name}' notification");
                if (t.Execute is { CredentialId.Length: > 0 } && credentials?.Contains(t.Execute.CredentialId) != true)
                {
                    notes.Add($"Task '{t.Name}' run-as credential does not exist in {dstEnv}; cleared (set one with visualcron_update_task).");
                    t.Execute.CredentialId = "";
                }
                if (t.PowerShell is { CredentialId.Length: > 0 } && credentials?.Contains(t.PowerShell.CredentialId) != true)
                {
                    notes.Add($"Task '{t.Name}' PowerShell credential does not exist in {dstEnv}; cleared.");
                    t.PowerShell.CredentialId = "";
                }
                t.Flows.RemoveAll(f => f.ActionType == FlowActions.RunNotification && notifications?.Contains(f.NotificationId) != true);
            }

        }

        // Map dependency triggers to the corresponding jobs of the target environment (also on a shared server, where the
        // upstream DEV job ID would otherwise still point at DEV).
        if (srcEnv != dstEnv)
        {
            var targetJobs = await ScopedJobsAsync(dstEnv, ct);
            foreach (var tr in after.Triggers.Where(t => t.Kind == TriggerKinds.JobEvent).ToList())
            {
                string wanted = R(tr.JobEvent!.UpstreamJobName);
                var match = targetJobs.Where(j => string.Equals(j.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Count == 1)
                {
                    notes.Add($"Dependency on '{tr.JobEvent.UpstreamJobName}' mapped to '{match[0].Name}' ({match[0].Group}) in {dstEnv}.");
                    tr.JobEvent.UpstreamJobId = match[0].Id;
                    tr.JobEvent.UpstreamJobName = match[0].Name;
                }
                else
                {
                    after.Triggers.Remove(tr);
                    notes.Add($"Dependency on '{tr.JobEvent.UpstreamJobName}' removed: no unique job named '{wanted}' in {dstEnv}.");
                }
            }
        }
        if (after.Tasks.Any(t => !t.Editable))
            notes.Add($"{after.Tasks.Count(t => !t.Editable)} task(s) are of types this server does not model and are copied verbatim — review their environment-specific settings in the VisualCron client.");
        if (!after.Active) notes.Add("The clone will be created INACTIVE. Enable it with visualcron_enable_job after review.");

        var diff = JobComparer.Compare(source, after);
        var changes = new List<string> { $"Clone of '{source.Name}' ({srcEnv}) → '{after.Name}' ({dstEnv}). Differences from the source:" };
        changes.AddRange(diff.Count == 0 ? ["(identical apart from IDs)"] : diff.Select(d => d.ToString()));
        changes.Add("Resulting job:");
        changes.AddRange(JobComparer.DescribeJob(after).Select(l => "  " + l));

        var proposal = await CreateProposalAsync(ProposalOperation.Clone, dstEnv, dst, before: null, after,
            $"Clone job '{source.Name}' from {srcEnv} to {dstEnv} as '{after.Name}'", changes, ct, notes,
            sourceEnvironment: srcEnv, source: source);
        return proposal;
    }

    /// <summary>
    /// New ADF job in the team's HTTP-task pattern: VisualCron clones the template job (token request, job variables
    /// holding tenant/client id/secret, task chain) and the clone gets its own name, pipeline, schedule and polling.
    /// </summary>
    public async Task<ChangeProposal> ProposeCreateAdfJobAsync(CreateAdfJobInput input, CancellationToken ct)
    {
        string env = environments.Resolve(input.Environment);
        var eo = environments.Get(env);
        var server = await gateway.ConnectAsync(env, ct);
        string templateRef = string.IsNullOrWhiteSpace(input.TemplateJob) ? eo.AdfTemplateJob : input.TemplateJob;
        if (string.IsNullOrWhiteSpace(templateRef))
            throw new VisualCronAIException($"No template job: pass TemplateJob or configure AdfTemplateJob for {env}.");
        var template = await ResolveJobAsync(env, templateRef, ct);
        if (AdfHttpPattern.TryRead(template) == null)
            throw new VisualCronAIException($"Template '{template.Name}' does not trigger ADF through HTTP tasks.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new VisualCronAIException("Name is required.");

        var notes = new List<string> { $"Built from template '{template.Name.Trim()}' (VisualCron copies its token request and job variables; secrets never leave the server)." };
        var after = Copy(template);
        after.Name = input.Name.Trim();
        after.Description = $"Triggers ADF pipeline {input.PipelineName}.";
        if (!string.IsNullOrWhiteSpace(input.Group)) after.Group = input.Group.Trim();
        after.Active = input.Active;
        AdfHttpPattern.SetPipeline(after, input.PipelineName.Trim());
        if (input.Parameters is { Count: > 0 }) AdfHttpPattern.SetParameters(after, input.Parameters, null);
        if (!string.IsNullOrWhiteSpace(eo.AdfResourceGroup) || !string.IsNullOrWhiteSpace(eo.AdfFactoryName))
            AdfHttpPattern.Retarget(after, eo.AdfSubscriptionId, eo.AdfResourceGroup, eo.AdfFactoryName);
        if (input.PollUntilComplete)
        {
            AdfHttpPattern.ApplyPolling(after, input.PollMinutes ?? eo.AdfPollMinutes, input.MaxRunMinutes ?? eo.AdfMaxRunMinutes);
            notes.Add("Status is polled until the pipeline finishes; Failed/Cancelled raise an error (verify with one DEV run).");
        }
        if (input.Schedules is { Count: > 0 })
        {
            after.Triggers.RemoveAll(t => t.Kind == TriggerKinds.Schedule);
            var (triggers, w) = JobSpecBuilder.BuildSchedules(input.Schedules, server, clock.UtcNow.UtcDateTime);
            after.Triggers.AddRange(triggers);
            notes.AddRange(w);
        }
        if (input.DependsOn != null)
        {
            after.Triggers.RemoveAll(t => t.Kind == TriggerKinds.JobEvent);
            after.Triggers.AddRange(await BuildDependenciesAsync(env, input.DependsOn, ct));
        }
        if (input.ErrorNotificationIds != null) after.ErrorNotificationIds = input.ErrorNotificationIds.Select(x => x.Trim()).ToList();
        if (!after.Active) notes.Add("The job will be created INACTIVE; enable it with visualcron_enable_job after review.");

        var changes = new List<string> { $"New job from template '{template.Name.Trim()}'. Differences from the template:" };
        changes.AddRange(JobComparer.Compare(template, after).Select(d => d.ToString()));
        changes.Add("Resulting job:");
        changes.AddRange(JobComparer.DescribeJob(after).Select(l => "  " + l));
        return await CreateProposalAsync(ProposalOperation.Clone, env, eo, before: null, after,
            $"Create ADF job '{after.Name}' (pipeline {input.PipelineName}) in {env}", changes, ct, notes,
            sourceEnvironment: env, source: template);
    }

    // ============================================================ approval & apply

    public ChangeProposal GetProposal(string proposalId)
    {
        var p = proposals.Get(proposalId) ?? throw new VisualCronAIException($"Proposal '{proposalId}' not found (proposals are kept in memory for this session only).");
        if (p.Status is ProposalStatus.PendingApproval or ProposalStatus.Approved or ProposalStatus.Blocked && clock.UtcNow > p.ExpiresAt)
            p.Status = ProposalStatus.Expired;
        return p;
    }

    public async Task<ChangeProposal> RequestApprovalAsync(string proposalId, IReadOnlyList<IApprovalPrompt> channels, CancellationToken ct)
    {
        var p = GetProposal(proposalId);
        switch (p.Status)
        {
            case ProposalStatus.Blocked:
                throw new VisualCronAIException("Proposal has validation errors and cannot be approved: " + string.Join("; ", p.Validation.Errors.Select(e => e.ToString())));
            case ProposalStatus.Expired:
                throw new VisualCronAIException("Proposal expired; prepare the change again.");
            case ProposalStatus.PendingApproval:
                break;
            default:
                throw new VisualCronAIException($"Proposal is {p.Status}; approval can only be requested once.");
        }
        var envOptions = environments.Get(p.Environment);
        EnsureWritable(p, envOptions);

        var channel = SelectChannel(channels) ?? throw new VisualCronAIException(
            "No human approval channel is available. Use an MCP client that supports elicitation (Claude Code, VS Code Copilot) or enable Approval:Mode=WindowsDialog on an interactive Windows session.");

        var request = new ApprovalRequest
        {
            ProposalId = p.Id,
            Environment = p.Environment,
            IsProduction = p.IsProduction,
            JobName = p.JobName,
            Operation = p.Operation,
            Summary = p.Summary,
            Details = RenderApprovalDetails(p),
            RequiredConfirmationText = p.IsProduction ? $"PROD {p.JobName}" : null,
        };
        await Audit("ApprovalRequested", p, "Pending", $"channel={channel.Channel}", ct);

        ApprovalDecision decision;
        try { decision = await channel.RequestAsync(request, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Approval channel {Channel} failed", channel.Channel);
            decision = new ApprovalDecision { Approved = false, Channel = channel.Channel, Reason = "Approval prompt failed: " + ex.Message };
        }

        bool prodConfirmed = false;
        if (decision.Approved && p.IsProduction)
        {
            prodConfirmed = string.Equals(decision.ConfirmationText?.Trim(), request.RequiredConfirmationText, StringComparison.Ordinal);
            if (!prodConfirmed)
                decision = new ApprovalDecision { Approved = false, Channel = decision.Channel, Reason = $"Production confirmation text did not match '{request.RequiredConfirmationText}'." };
        }

        p.Approval = new ApprovalRecord
        {
            Approved = decision.Approved,
            Channel = decision.Channel,
            DecidedBy = user.Name,
            DecidedAt = clock.UtcNow,
            ContentHash = p.ContentHash,
            ProductionConfirmed = prodConfirmed,
            Reason = decision.Reason,
        };
        p.Status = decision.Approved ? ProposalStatus.Approved : ProposalStatus.Rejected;
        await Audit(decision.Approved ? "ApprovalGranted" : "ApprovalDenied", p, p.Status.ToString(), decision.Reason, ct);
        return p;
    }

    public async Task<ChangeProposal> ApplyAsync(string proposalId, CancellationToken ct)
    {
        await ApplyLock.WaitAsync(ct);
        try
        {
            var p = GetProposal(proposalId);
            var envOptions = environments.Get(p.Environment);

            // ---- server-side enforcement; none of this depends on what the AI says
            if (p.Status != ProposalStatus.Approved || p.Approval is not { Approved: true })
                throw new VisualCronAIException($"Proposal is {p.Status}. Changes can only be applied after a human approves them via visualcron_request_approval.");
            if (clock.UtcNow > p.Approval.DecidedAt.AddMinutes(environments.Approval.ApprovalTtlMinutes))
            {
                p.Status = ProposalStatus.Expired;
                throw new VisualCronAIException("The approval expired; prepare and approve the change again.");
            }
            if (!string.Equals(p.Approval.ContentHash, p.ContentHash, StringComparison.Ordinal) || !string.Equals(ComputeHash(p), p.ContentHash, StringComparison.Ordinal))
                throw new VisualCronAIException("Proposal content does not match what was approved.");
            if (!p.Validation.IsValid) throw new VisualCronAIException("Proposal has validation errors.");
            EnsureWritable(p, envOptions);
            if (p.IsProduction && !p.Approval.ProductionConfirmed)
                throw new VisualCronAIException("Production changes need the explicit production confirmation.");

            await Audit("ApplyStarted", p, "Started", null, ct);
            WriteResultDto result;
            try
            {
                result = p.Operation switch
                {
                    ProposalOperation.Create => await gateway.CreateJobAsync(p.Environment, ForWrite(p.After), ct),
                    ProposalOperation.Update => await gateway.UpdateJobAsync(p.Environment, ForWrite(p.After), p.BaseVersionToken!, ct),
                    ProposalOperation.Enable or ProposalOperation.Disable =>
                        await gateway.SetJobActiveAsync(p.Environment, p.TargetJobId!, p.Operation == ProposalOperation.Enable, p.BaseVersionToken!, ct),
                    ProposalOperation.Clone => await gateway.CloneJobAsync(new CloneJobParams
                    {
                        SourceEnvironment = p.SourceEnvironment!,
                        SourceJobId = p.SourceJobId!,
                        ExpectedSourceVersionToken = p.SourceVersionToken!,
                        TargetEnvironment = p.Environment,
                        Overrides = ForWrite(p.After),
                    }, ct),
                    _ => throw new VisualCronAIException("Unsupported operation."),
                };
            }
            catch (GatewayException ex)
            {
                p.Status = ProposalStatus.Failed;
                p.Result = new ApplyRecord { Success = false, AppliedAt = clock.UtcNow, AppliedBy = user.Name, Message = ex.Message };
                await Audit("ApplyFailed", p, ex.Code, ex.Message, ct);
                string hint = ex.Code == BridgeErrorCodes.ConcurrencyConflict ? " The job was changed by someone else; prepare the change again." : "";
                throw new VisualCronAIException($"Apply failed ({ex.Code}): {ex.Message}{hint}");
            }

            p.Status = ProposalStatus.Applied;
            p.Result = new ApplyRecord
            {
                Success = true,
                AppliedAt = clock.UtcNow,
                AppliedBy = user.Name,
                JobId = result.Job.Id,
                Verified = result.Verified,
                Message = result.Verified ? "Applied and verified by reading the job back from VisualCron." : "Applied (not verified).",
                Notes = result.Notes,
            };
            await Audit("ApplySucceeded", p, "Applied", $"jobId={result.Job.Id}; verified={result.Verified}", ct, jobId: result.Job.Id);
            return p;
        }
        finally
        {
            ApplyLock.Release();
        }
    }

    // ============================================================ queries used by tools

    public async Task<JobDto> ResolveJobAsync(string environment, string jobRef, CancellationToken ct)
    {
        string env = environments.Resolve(environment);
        if (string.IsNullOrWhiteSpace(jobRef)) throw new VisualCronAIException("A job ID or name is required.");
        jobRef = jobRef.Trim();
        if (jobRef.Length > 200) throw new VisualCronAIException("Job reference is too long.");
        var scope = environments.Get(env).JobGroups;
        if (JobValidator.IsGuid(jobRef))
        {
            var byId = await gateway.GetJobAsync(env, jobRef, ct);
            if (!GroupPattern.MatchesAny(scope, byId.Group))
                throw new VisualCronAIException($"Job '{byId.Name}' is in group '{byId.Group}', which is not part of environment {env} ({string.Join(", ", scope)}).");
            return byId;
        }

        var jobs = await ScopedJobsAsync(env, ct);
        // Real job names often carry stray leading/trailing spaces ("Products - Trigger ADF ").
        var exact = jobs.Where(j => string.Equals(j.Name?.Trim(), jobRef, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return await gateway.GetJobAsync(env, exact[0].Id, ct);
        if (exact.Count > 1)
            throw new VisualCronAIException($"{exact.Count} jobs are named '{jobRef}' in {env}; use the job ID: " + string.Join(", ", exact.Select(j => $"{j.Id} ({j.Group})")));
        var similar = jobs.Where(j => j.Name.Contains(jobRef, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        throw new VisualCronAIException($"No job named '{jobRef}' in {env}." +
            (similar.Count > 0 ? " Did you mean: " + string.Join(", ", similar.Select(j => $"'{j.Name}' ({j.Id})")) : ""));
    }

    public async Task<(ValidationReport Report, SchedulePreviewDto? Preview)> ValidateExistingAsync(string environment, string jobRef, CancellationToken ct)
    {
        string env = environments.Resolve(environment);
        var job = await ResolveJobAsync(env, jobRef, ct);
        return await ValidateAndPreviewAsync(env, environments.Get(env), job, job, isNew: false, ct);
    }

    public DraftResult GetDraft(string draftId)
    {
        var d = drafts.Get(draftId) ?? throw new VisualCronAIException($"Draft '{draftId}' not found.");
        return new DraftResult { DraftId = d.Id, Environment = d.Environment, Job = d.Job, Description = JobComparer.DescribeJob(d.Job) };
    }

    public async Task<(ValidationReport Report, SchedulePreviewDto? Preview)> ValidateDraftAsync(string draftId, CancellationToken ct)
    {
        var d = drafts.Get(draftId) ?? throw new VisualCronAIException($"Draft '{draftId}' not found.");
        return await ValidateAndPreviewAsync(d.Environment, environments.Get(d.Environment), d.Job, null, isNew: true, ct);
    }

    // ============================================================ internals

    private async Task<ChangeProposal> CreateProposalAsync(ProposalOperation op, string env, EnvironmentOptions envOptions,
        JobDto? before, JobDto after, string summary, List<string> changes, CancellationToken ct,
        List<string>? notes = null, string? sourceEnvironment = null, JobDto? source = null)
    {
        var (validation, preview) = await ValidateAndPreviewAsync(env, envOptions, after, before, isNew: before == null, ct);
        var warnings = new List<string>(notes ?? []);
        if (!envOptions.AllowWrites) warnings.Add($"Writes are disabled for {env} (AllowWrites=false); this proposal can be reviewed but not applied.");
        if (envOptions.IsProduction) warnings.Add("PRODUCTION environment: approval requires typing a confirmation and an authorized production approver.");

        var now = clock.UtcNow;
        var p = new ChangeProposal
        {
            Id = ProposalStore.NewId("prp"),
            Operation = op,
            Environment = env,
            IsProduction = envOptions.IsProduction,
            JobName = after.Name,
            TargetJobId = before?.Id,
            BaseVersionToken = before?.VersionToken,
            SourceEnvironment = sourceEnvironment,
            SourceJobId = source?.Id,
            SourceVersionToken = source?.VersionToken,
            Before = before,
            After = after,
            Summary = summary,
            Changes = changes,
            Validation = validation,
            SchedulePreview = preview,
            Warnings = warnings,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(environments.Approval.ProposalTtlMinutes),
            CreatedBy = user.Name,
            Status = validation.IsValid ? ProposalStatus.PendingApproval : ProposalStatus.Blocked,
        };
        p = WithHash(p);
        proposals.Add(p);
        await Audit("ProposalCreated", p, p.Status.ToString(), summary, ct);
        return p;
    }

    private async Task<(ValidationReport, SchedulePreviewDto?)> ValidateAndPreviewAsync(string env, EnvironmentOptions envOptions, JobDto job, JobDto? before, bool isNew, CancellationToken ct)
    {
        var notifications = await SafeList(() => gateway.ListNotificationsAsync(env, ct));
        var credentials = await SafeList(() => gateway.ListCredentialsAsync(env, ct));
        var jobs = await SafeList(() => gateway.ListJobsAsync(env, null, null, ct));
        var ctx = new ValidationContext
        {
            Environment = env,
            EnvironmentOptions = envOptions,
            IsNewJob = isNew,
            Before = before,
            KnownNotificationIds = notifications?.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            KnownCredentialIds = credentials?.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            KnownJobs = jobs?.GroupBy(j => j.Id).ToDictionary(g => g.Key, g => g.First().Name),
        };
        var report = JobValidator.Validate(job, ctx);
        // Names must be unique within a job group (the same name may exist in DEV:, UAT: and PROD: groups).
        if (isNew && jobs != null && jobs.Any(j => SameNameAndGroup(j.Name, j.Group, job.Name, job.Group)))
            report.Error("job.name", $"A job named '{job.Name}' already exists in group '{job.Group}'.");
        if (before != null && jobs != null && jobs.Any(j => j.Id != before.Id && SameNameAndGroup(j.Name, j.Group, job.Name, job.Group)))
            report.Error("job.name", $"Another job is already named '{job.Name}' in group '{job.Group}'.");
        if (before != null && !GroupPattern.MatchesAny(envOptions.WritableJobGroups, before.Group))
            report.Error("job.group", $"Job is in group '{before.Group}', which is not writable in {env}.");
        if (before != null && !GroupPattern.MatchesAny(envOptions.JobGroups, before.Group))
            report.Error("job.group", $"Job is in group '{before.Group}', which is not part of environment {env}.");
        if (notifications == null) report.Warn("references", "Could not list notifications/credentials to verify references (insufficient VisualCron permissions?).");

        SchedulePreviewDto? preview = null;
        try { preview = await gateway.PreviewScheduleAsync(env, ForWrite(job), 5, ct); }
        catch (GatewayException ex) { report.Warn("schedule", "Schedule preview unavailable: " + ex.Message); }
        if (preview != null) foreach (var w in preview.Warnings) report.Warn("schedule", w);
        return (report, preview);
    }

    private async Task<List<TriggerDto>> BuildDependenciesAsync(string env, List<DependencyInput> deps, CancellationToken ct)
    {
        var result = new List<TriggerDto>();
        foreach (var d in deps)
        {
            var upstream = await ResolveJobAsync(env, d.Job, ct);
            result.Add(JobSpecBuilder.BuildDependency(upstream.Id, upstream.Name, d.Condition));
        }
        return result;
    }

    /// <summary>Jobs that belong to the environment (its JobGroups on a shared server; the whole server otherwise).</summary>
    public async Task<IReadOnlyList<JobSummaryDto>> ScopedJobsAsync(string env, CancellationToken ct)
    {
        var scope = environments.Get(env).JobGroups;
        var all = await gateway.ListJobsAsync(env, null, null, ct);
        return all.Where(j => GroupPattern.MatchesAny(scope, j.Group)).ToList();
    }

    private static bool SameNameAndGroup(string n1, string g1, string n2, string g2) =>
        string.Equals(n1?.Trim(), n2?.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(g1?.Trim(), g2?.Trim(), StringComparison.OrdinalIgnoreCase);

    private IApprovalPrompt? SelectChannel(IReadOnlyList<IApprovalPrompt> channels)
    {
        string mode = environments.Approval.Mode ?? "Auto";
        IEnumerable<IApprovalPrompt> candidates = mode.ToLowerInvariant() switch
        {
            "elicitation" => channels.Where(c => c.Channel == "Elicitation"),
            "windowsdialog" => channels.Where(c => c.Channel == "WindowsDialog"),
            _ => channels.OrderBy(c => c.Channel == "Elicitation" ? 0 : 1),
        };
        return candidates.FirstOrDefault(c => c.IsAvailable);
    }

    private void EnsureWritable(ChangeProposal p, EnvironmentOptions envOptions)
    {
        if (!envOptions.AllowWrites)
            throw new VisualCronAIException($"Writes are disabled for environment {p.Environment} (AllowWrites=false in configuration).");
        if (envOptions.IsProduction)
        {
            var approvers = environments.Approval.ProductionApprovers;
            if (!approvers.Contains(user.Name, StringComparer.OrdinalIgnoreCase))
                throw new VisualCronAIException($"Windows account '{user.Name}' is not listed in Approval:ProductionApprovers.");
        }
    }

    private static string RenderApprovalDetails(ChangeProposal p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{p.Summary}");
        sb.AppendLine($"Environment: {p.Environment}{(p.IsProduction ? "  *** PRODUCTION ***" : "")}   Proposal: {p.Id}");
        sb.AppendLine();
        foreach (var c in p.Changes.Take(60)) sb.AppendLine(c);
        if (p.Changes.Count > 60) sb.AppendLine($"... {p.Changes.Count - 60} more line(s) — see visualcron_preview_changes.");
        if (p.SchedulePreview?.NextExecutionsServerTime.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Next runs ({p.SchedulePreview.ServerTimeZone}): " + string.Join(", ", p.SchedulePreview.NextExecutionsServerTime.Take(5).Select(d => d.ToString("ddd yyyy-MM-dd HH:mm"))));
        }
        var warnings = p.Validation.Warnings.Select(w => w.Message).Concat(p.Warnings).Distinct().ToList();
        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var w in warnings.Take(15)) sb.AppendLine("  - " + w);
        }
        return SecretScanner.Redact(sb.ToString());
    }

    private static ChangeProposal WithHash(ChangeProposal p)
    {
        string hash = ComputeHash(p);
        return new ChangeProposal
        {
            Id = p.Id, Operation = p.Operation, Environment = p.Environment, IsProduction = p.IsProduction, JobName = p.JobName,
            TargetJobId = p.TargetJobId, BaseVersionToken = p.BaseVersionToken, SourceEnvironment = p.SourceEnvironment,
            SourceJobId = p.SourceJobId, SourceVersionToken = p.SourceVersionToken, Before = p.Before, After = p.After,
            Summary = p.Summary, Changes = p.Changes, Validation = p.Validation, SchedulePreview = p.SchedulePreview,
            Warnings = p.Warnings, CreatedAt = p.CreatedAt, ExpiresAt = p.ExpiresAt, CreatedBy = p.CreatedBy,
            Status = p.Status, ContentHash = hash,
        };
    }

    /// <summary>Hash over everything that determines what will be written.</summary>
    public static string ComputeHash(ChangeProposal p)
    {
        var payload = new
        {
            p.Operation, p.Environment, p.TargetJobId, p.BaseVersionToken, p.SourceEnvironment, p.SourceJobId, p.SourceVersionToken,
            after = ForWrite(p.After),
        };
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, HashJson)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Strips read-only fields before sending to the bridge.</summary>
    private static JobDto ForWrite(JobDto j)
    {
        var c = Copy(j);
        c.Stats = null;
        c.VersionToken = null;
        return c;
    }

    private static JobDto Copy(JobDto j) => JsonSerializer.Deserialize<JobDto>(JsonSerializer.Serialize(j))!;

    private static TaskDto FindTask(JobDto job, string taskRef)
    {
        if (string.IsNullOrWhiteSpace(taskRef)) throw new VisualCronAIException("A task ID, name or 1-based position is required.");
        taskRef = taskRef.Trim();
        var byId = job.Tasks.FirstOrDefault(t => t.Id == taskRef);
        if (byId != null) return byId;
        var byName = job.Tasks.Where(t => string.Equals(t.Name, taskRef, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (int.TryParse(taskRef, out int pos) && pos >= 1 && pos <= job.Tasks.Count) return job.Tasks.OrderBy(t => t.Order).ElementAt(pos - 1);
        var adf = job.Tasks.Where(t => AdfTaskFactory.TryRead(t)?.PipelineName.Equals(taskRef, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (adf.Count == 1) return adf[0];
        throw new VisualCronAIException($"Task '{taskRef}' not found in job '{job.Name}'. Tasks: " + string.Join(", ", job.Tasks.OrderBy(t => t.Order).Select(t => $"'{t.Name}'")));
    }

    private async Task<IReadOnlyList<T>?> SafeList<T>(Func<Task<IReadOnlyList<T>>> f)
    {
        try { return await f(); }
        catch (GatewayException ex) when (ex.Code is BridgeErrorCodes.PermissionDenied or BridgeErrorCodes.Internal)
        {
            logger.LogWarning("Reference listing failed: {Message}", ex.Message);
            return null;
        }
    }

    private Task Audit(string type, ChangeProposal p, string outcome, string? details, CancellationToken ct, string? jobId = null) =>
        audit.WriteAsync(new AuditEvent
        {
            Timestamp = clock.UtcNow,
            EventType = type,
            User = user.Name,
            Environment = p.Environment,
            ProposalId = p.Id,
            JobId = jobId ?? p.TargetJobId,
            JobName = p.JobName,
            Operation = p.Operation.ToString(),
            Outcome = outcome,
            ContentHash = p.ContentHash,
            Details = details == null ? null : SecretScanner.Redact(details),
        }, ct);
}
