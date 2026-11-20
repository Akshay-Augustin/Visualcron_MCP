using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Adf;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Diff;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Services;
using VisualCronAI.Core.Validation;

namespace VisualCronAI.Mcp.Tools;

[McpServerToolType]
public sealed class ReadTools(IVisualCronGateway gateway, ChangeService changes, EnvironmentRegistry environments, IOptions<VisualCronAIOptions> options)
{
    [McpServerTool(Name = "visualcron_list_environments", ReadOnly = true, Idempotent = true)]
    [Description("Lists configured VisualCron environments (DEV/UAT/PROD...) with their write/production flags.")]
    public Task<string> ListEnvironments() => ToolResult.Run(() => Task.FromResult<object>(new
    {
        defaultEnvironment = options.Value.DefaultEnvironment,
        environments = options.Value.Environments.Select(e => new
        {
            name = e.Key,
            server = $"{e.Value.Address}:{e.Value.Port}",
            authMode = e.Value.AuthMode,
            isProduction = e.Value.IsProduction,
            allowWrites = e.Value.AllowWrites,
            jobGroups = e.Value.JobGroups,
            writableJobGroups = e.Value.WritableJobGroups,
            adfRunnerConfigured = !string.IsNullOrWhiteSpace(e.Value.AdfRunnerPath),
        }),
        approvalMode = options.Value.Approval.Mode,
    }));

    [McpServerTool(Name = "visualcron_test_connection", ReadOnly = true)]
    [Description("Connects to the VisualCron server of an environment (Windows/AD logon or Credential Manager, handled entirely by the server — never ask the user for credentials) and reports server version, time zone and job count.")]
    public Task<string> TestConnection([Description("Environment, e.g. DEV. Default: configured default.")] string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            var bridge = await gateway.GetBridgeInfoAsync(ct);
            var info = await gateway.TestConnectionAsync(env, ct);
            return new { environment = env, info.Connected, server = $"{info.Address}:{info.Port}", info.ServerVersion, info.TimeZoneName, info.UtcOffsetMinutes, info.ServerTime, info.JobCount, info.AuthMode, bridge.VisualCronApiVersion };
        });

    [McpServerTool(Name = "visualcron_list_jobs", ReadOnly = true, Idempotent = true)]
    [Description("Lists VisualCron jobs (id, name, group, active, status, last/next run, last result).")]
    public Task<string> ListJobs(string? environment = null, [Description("Optional job group filter.")] string? group = null,
        [Description("Max jobs to return (default 200).")] int limit = 200, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            var jobs = (await changes.ScopedJobsAsync(env, ct))
                .Where(j => string.IsNullOrWhiteSpace(group) || GroupPattern.Matches(group.Contains('*') ? group : group + "*", j.Group)).ToList();
            return new { environment = env, jobGroups = environments.Get(env).JobGroups, total = jobs.Count, jobs = jobs.Take(Math.Clamp(limit, 1, 2000)) };
        });

    [McpServerTool(Name = "visualcron_search_jobs", ReadOnly = true, Idempotent = true)]
    [Description("Searches jobs whose name contains the text (case-insensitive).")]
    public Task<string> SearchJobs([Description("Text contained in the job name, e.g. 'D365 Product' or 'Salesforce_Account'.")] string query,
        string? environment = null, string? group = null, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new Core.Models.VisualCronAIException("Query must be 1-200 characters.");
            var all = (await changes.ScopedJobsAsync(env, ct))
                .Where(j => string.IsNullOrWhiteSpace(group) || GroupPattern.Matches(group.Contains('*') ? group : group + "*", j.Group)).ToList();
            // Match all words in any order, so "D365 Product Load" finds "D365_Product_Daily_Load".
            var words = query.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
            var hits = all.Where(j => words.All(w => j.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            return new { environment = env, query, count = hits.Count, jobs = hits };
        });

    [McpServerTool(Name = "visualcron_get_job", ReadOnly = true, Idempotent = true)]
    [Description("Full details of a job: settings, tasks (with ADF pipeline/parameters), triggers, error handling, notifications, stats and version token.")]
    public Task<string> GetJob([Description("Job ID or exact job name.")] string job, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            var j = await changes.ResolveJobAsync(env, job, ct);
            var http = AdfHttpPattern.TryRead(j);
            return new
            {
                environment = env,
                overview = JobComparer.DescribeJob(j),
                nextRunsServerTime = j.NextRunsServerTime.Select(d => d.ToString("ddd yyyy-MM-dd HH:mm")),
                adfHttp = http == null ? null : new
                {
                    http.SubscriptionId, http.ResourceGroup, http.FactoryName, http.PipelineName, http.Parameters,
                    waitMinutes = http.WaitMinutes, http.PollsUntilComplete,
                    statusCheck = http.PollsUntilComplete ? "polls until finished; fails on pipeline failure" : "single check after a fixed wait — a failed or still-running pipeline is reported as Success",
                },
                job = j,
                adfTasks = AdfViews(j),
            };
        });

    [McpServerTool(Name = "visualcron_get_job_tasks", ReadOnly = true, Idempotent = true)]
    [Description("Tasks of a job in execution order, including ADF pipeline name/parameters and retry policy.")]
    public Task<string> GetJobTasks(string job, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            var j = await changes.ResolveJobAsync(env, job, ct);
            return new
            {
                environment = env,
                job = new { j.Id, j.Name },
                tasks = j.Tasks.OrderBy(t => t.Order).Select(t => new
                {
                    position = t.Order, t.Id, t.Name, t.TaskType, t.Active, t.Editable,
                    description = JobComparer.DescribeTask(t),
                    onError = JobComparer.ErrorPolicy(t),
                    adf = AdfTaskFactory.TryRead(t),
                    t.Execute, t.PowerShell, t.Summary,
                }),
            };
        });

    [McpServerTool(Name = "visualcron_get_job_triggers", ReadOnly = true, Idempotent = true)]
    [Description("Triggers of a job (schedules and job dependencies) plus the next run times computed by VisualCron's scheduling engine.")]
    public Task<string> GetJobTriggers(string job, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            var j = await changes.ResolveJobAsync(env, job, ct);
            var server = await gateway.ConnectAsync(env, ct);
            return new
            {
                environment = env,
                job = new { j.Id, j.Name },
                triggers = j.Triggers.Select(t => new { t.Id, t.Kind, t.Active, description = JobComparer.DescribeTrigger(t), t.Schedule, t.JobEvent, editable = t.Editable }),
                // Computed by the bridge from the job's real triggers and cross-checked with the server's next execution.
                nextRuns = new { serverTimeZone = server.TimeZoneName, times = j.NextRunsServerTime.Select(d => d.ToString("ddd yyyy-MM-dd HH:mm")) },
            };
        });

    [McpServerTool(Name = "visualcron_get_job_dependencies", ReadOnly = true)]
    [Description("Upstream jobs this job waits for, and downstream jobs triggered by it (dependency triggers and 'run job' flows).")]
    public Task<string> GetJobDependencies(string job, string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            var j = await changes.ResolveJobAsync(env, job, ct);
            var upstream = j.Triggers.Where(t => t.Kind == TriggerKinds.JobEvent).Select(t => new { t.JobEvent!.UpstreamJobId, t.JobEvent.UpstreamJobName, t.JobEvent.Condition, t.Active }).ToList();
            var runsJobs = j.Tasks.SelectMany(t => t.Flows.Where(f => f.ActionType == FlowActions.RunJob).Select(f => new { task = t.Name, on = f.EventType, jobId = f.JobId })).ToList();

            var all = await gateway.ListJobsAsync(env, null, null, ct);
            var downstream = new List<object>();
            int scanned = 0;
            foreach (var other in all.Where(o => o.Id != j.Id).Take(500))
            {
                scanned++;
                var d = await gateway.GetJobAsync(env, other.Id, ct);
                foreach (var t in d.Triggers.Where(t => t.Kind == TriggerKinds.JobEvent && t.JobEvent!.UpstreamJobId == j.Id))
                    downstream.Add(new { jobId = d.Id, jobName = d.Name, condition = t.JobEvent!.Condition, t.Active });
                foreach (var f in d.Tasks.SelectMany(t => t.Flows).Where(f => f.ActionType == FlowActions.RunJob && f.JobId == j.Id))
                    downstream.Add(new { jobId = d.Id, jobName = d.Name, condition = "RunJob flow on " + f.EventType, active = true });
            }
            return new
            {
                environment = env,
                job = new { j.Id, j.Name },
                upstream,
                runsJobs,
                downstream,
                note = all.Count > 501 ? $"Only the first 500 of {all.Count} jobs were scanned for downstream dependencies." : null,
                taskOrder = j.RunTasksInOrder ? "Tasks run sequentially; each waits for the previous one to succeed (unless its error flow says otherwise)." : "Tasks run in parallel.",
            };
        });

    [McpServerTool(Name = "visualcron_get_job_history", ReadOnly = true, Idempotent = true)]
    [Description("Recent executions of a job (start time, duration, exit code, result).")]
    public Task<string> GetJobHistory(string job, string? environment = null, [Description("Days back (1-90, default 7).")] int days = 7,
        bool failedOnly = false, int maxEntries = 50, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            var j = await changes.ResolveJobAsync(env, job, ct);
            var now = DateTime.UtcNow;
            var history = await gateway.GetJobHistoryAsync(new JobHistoryParams
            {
                Environment = env, JobId = j.Id, FromUtc = now.AddDays(-Math.Clamp(days, 1, 90)), ToUtc = now.AddMinutes(5),
                FailedOnly = failedOnly, MaxEntries = Math.Clamp(maxEntries, 1, 500),
            }, ct);
            return new { environment = env, job = new { j.Id, j.Name }, count = history.Count, executions = history };
        });

    [McpServerTool(Name = "visualcron_get_job_logs", ReadOnly = true, Idempotent = true)]
    [Description("Task output (stdout/stderr, exit codes) of a job execution — the latest one by default. For ADF tasks this includes the pipeline run ID and failed activity errors.")]
    public Task<string> GetJobLogs(string job, string? environment = null, [Description("Execution ID from visualcron_get_job_history; empty = latest.")] string? executionId = null,
        int maxCharsPerStream = 4000, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            var j = await changes.ResolveJobAsync(env, job, ct);
            if (!string.IsNullOrEmpty(executionId) && !Guid.TryParse(executionId, out _)) throw new Core.Models.VisualCronAIException("executionId must be a GUID.");
            // Fetch the full output and redact BEFORE truncating: truncating first can cut off the context that
            // identifies a secret (e.g. the "eyJ" start of a JWT), leaving an unrecognizable fragment.
            var logs = await gateway.GetJobLogsAsync(new JobLogsParams { Environment = env, JobId = j.Id, ExecutionId = executionId ?? "", MaxCharsPerStream = 1_000_000 }, ct);
            int max = Math.Clamp(maxCharsPerStream, 200, 50000);
            foreach (var l in logs)
            {
                l.StandardOutput = TailOf(SecretScanner.Redact(l.StandardOutput), max, out bool t1);
                l.StandardError = TailOf(SecretScanner.Redact(l.StandardError), max, out bool t2);
                l.Truncated |= t1 || t2;
            }
            return new { environment = env, job = new { j.Id, j.Name }, tasks = logs };
        });

    [McpServerTool(Name = "visualcron_list_notifications", ReadOnly = true, Idempotent = true)]
    [Description("Existing VisualCron notifications (e-mail etc.) that can be attached to jobs as failure notifications.")]
    public Task<string> ListNotifications(string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            return new { environment = env, notifications = await gateway.ListNotificationsAsync(env, ct) };
        });

    [McpServerTool(Name = "visualcron_list_credentials", ReadOnly = true, Idempotent = true)]
    [Description("IDs and descriptions of VisualCron network credentials that tasks can run as. Never returns user names or passwords.")]
    public Task<string> ListCredentials(string? environment = null, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string env = environments.Resolve(environment);
            return new { environment = env, credentials = await gateway.ListCredentialsAsync(env, ct) };
        });

    private static string TailOf(string s, int max, out bool truncated)
    {
        truncated = s.Length > max;
        return truncated ? "…" + s[^max..] : s;
    }

    private static object AdfViews(JobDto j) => j.Tasks.Select(t => (t, adf: AdfTaskFactory.TryRead(t))).Where(x => x.adf != null)
        .Select(x => new { task = x.t.Name, x.adf!.PipelineName, x.adf.Environment, x.adf.Parameters, x.adf.TimeoutMinutes, onError = JobComparer.ErrorPolicy(x.t) }).ToList();
}
