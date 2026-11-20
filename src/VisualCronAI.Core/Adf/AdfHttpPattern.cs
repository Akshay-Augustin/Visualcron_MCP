using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Core.Adf;

/// <summary>What an HTTP-pattern ADF job looks like (token → createRun → runId → wait → status → filter).</summary>
public sealed class AdfHttpView
{
    public string SubscriptionId { get; init; } = "";
    public string ResourceGroup { get; init; } = "";
    public string FactoryName { get; init; } = "";
    public string PipelineName { get; init; } = "";
    public Dictionary<string, string> Parameters { get; init; } = [];
    public bool ParametersMasked { get; init; }
    public TaskDto CreateRunTask { get; init; } = null!;
    public TaskDto? WaitTask { get; init; }
    public TaskDto? StatusTask { get; init; }
    public TaskDto? StatusFilterTask { get; init; }
    public int WaitMinutes => WaitTask?.Wait is { } w ? w.Hours * 60 + w.Minutes : 0;
    /// <summary>True when the status filter loops back to Wait while running and raises an error on failure.</summary>
    public bool PollsUntilComplete { get; init; }
}

/// <summary>
/// Support for the team's existing VisualCron pattern that calls the ADF REST API with native HTTP / JSON filter /
/// Wait tasks. Secrets are not handled here: the token request uses job variables ({JOB(Active|Variable|clientsecret)})
/// that stay on the server and are copied by VisualCron's own clone.
/// </summary>
public static partial class AdfHttpPattern
{
    public const string RunningStatesRegex = "^(Queued|InProgress|Canceling)$";
    public const string OkOrRunningStatesRegex = "^(Succeeded|Queued|InProgress|Canceling)$";

    [GeneratedRegex(@"/subscriptions/(?<sub>[^/]+)/resourceGroups/(?<rg>[^/]+)/providers/Microsoft\.DataFactory/factories/(?<f>[^/]+)/pipelines/(?<p>[^/?]+)/createRun", RegexOptions.IgnoreCase)]
    private static partial Regex CreateRunUrl();
    [GeneratedRegex(@"/subscriptions/(?<sub>[^/]+)/resourceGroups/(?<rg>[^/]+)/providers/Microsoft\.DataFactory/factories/(?<f>[^/?]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FactoryPath();
    [GeneratedRegex(@"\{TASK\((?<id>[0-9a-fA-F\-]{36})\|")]
    private static partial Regex TaskRef();
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_\-]{0,259}$")]
    private static partial Regex PipelineName();

    public static AdfHttpView? TryRead(JobDto job)
    {
        var tasks = job.Tasks.OrderBy(t => t.Order).ToList();
        var create = tasks.FirstOrDefault(t => t.Http != null && CreateRunUrl().IsMatch(t.Http.Url));
        if (create == null) return null;
        var m = CreateRunUrl().Match(create.Http!.Url);

        var status = tasks.FirstOrDefault(t => t.Http != null && t.Http.Url.Contains("/pipelineruns/", StringComparison.OrdinalIgnoreCase));
        // Identify the status filter by role (JSON path "status" after the status call), not by its reference, so that
        // a wrongly wired filter (reading another job's task) is still found and can be repaired.
        var statusFilter = status == null ? null : tasks.FirstOrDefault(t => t.JsonFilter != null && t.Order > status.Order && IsPath(t, "status"));
        var wait = status == null ? null : tasks.Where(t => t.Wait != null && t.Order < status.Order).OrderByDescending(t => t.Order).FirstOrDefault();

        bool polls = statusFilter != null && wait != null && status != null && statusFilter.JsonFilter!.Variable.Trim() == Ref(status.Id)
                     && statusFilter.Flows.Any(f => f.ActionType == FlowActions.GotoTask && f.TaskId == wait.Id && f.Condition == "IfOutput")
                     && statusFilter.OutputErrorRule is { Enabled: true };

        var (parameters, masked) = ReadParameters(create.Http);
        return new AdfHttpView
        {
            SubscriptionId = m.Groups["sub"].Value,
            ResourceGroup = m.Groups["rg"].Value,
            FactoryName = m.Groups["f"].Value,
            PipelineName = Uri.UnescapeDataString(m.Groups["p"].Value),
            Parameters = parameters,
            ParametersMasked = masked,
            CreateRunTask = create,
            WaitTask = wait,
            StatusTask = status,
            StatusFilterTask = statusFilter,
            PollsUntilComplete = polls,
        };
    }

    private static bool IsPath(TaskDto t, string path) =>
        t.JsonFilter != null && t.JsonFilter.JsonPath.Trim().TrimStart('$', '.').Equals(path, StringComparison.OrdinalIgnoreCase);

    private static string Ref(string taskId) => "{TASK(" + taskId + "|StdOut)}";

    /// <summary>{TASK(id|...)} references in the job that point at tasks which are not part of the job.</summary>
    public static List<(TaskDto Task, string Where, string ForeignId)> ForeignReferences(JobDto job)
    {
        var ids = new HashSet<string>(job.Tasks.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var result = new List<(TaskDto, string, string)>();
        foreach (var t in job.Tasks)
        {
            var places = new List<(string Where, string? Text)>
            {
                ("url", t.Http?.Url), ("body", t.Http?.BodyRawMasked == true ? null : t.Http?.BodyRaw), ("json input", t.JsonFilter?.Variable),
                ("arguments", t.Execute?.Arguments),
            };
            places.AddRange((t.Http?.Headers ?? []).Select(h => ("header " + h.Key, (string?)h.Value)));
            foreach (var (where, text) in places)
                foreach (Match m in TaskRef().Matches(text ?? ""))
                    if (!ids.Contains(m.Groups["id"].Value)) result.Add((t, where, m.Groups["id"].Value));
        }
        return result;
    }

    /// <summary>
    /// Re-wires the ADF chain to the job's OWN tasks: token filter ← token request, createRun/status Authorization ←
    /// token filter, RunId filter ← createRun, status URL ← RunId filter, status filter ← status call.
    /// Fixes jobs copied from another job whose references still point at the source job's tasks.
    /// </summary>
    public static List<string> RepairReferences(JobDto job)
    {
        var notes = new List<string>();
        var tasks = job.Tasks.OrderBy(t => t.Order).ToList();
        var token = tasks.FirstOrDefault(t => t.Http != null && t.Http.Url.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase));
        var tokenFilter = tasks.FirstOrDefault(t => IsPath(t, "access_token"));
        var create = tasks.FirstOrDefault(t => t.Http != null && CreateRunUrl().IsMatch(t.Http.Url));
        var runIdFilter = tasks.FirstOrDefault(t => IsPath(t, "runId"));
        var status = tasks.FirstOrDefault(t => t.Http != null && t.Http.Url.Contains("/pipelineruns/", StringComparison.OrdinalIgnoreCase));
        var statusFilter = status == null ? null : tasks.FirstOrDefault(t => t.Order > status.Order && IsPath(t, "status"));

        void SetInput(TaskDto? filter, TaskDto? source)
        {
            if (filter?.JsonFilter == null || source == null) return;
            string want = Ref(source.Id);
            if (filter.JsonFilter.Variable.Trim() == want) return;
            notes.Add($"Task '{filter.Name}' (#{filter.Order}) now reads its own task '{source.Name}' (#{source.Order}) instead of {Describe(filter.JsonFilter.Variable)}.");
            filter.JsonFilter.Variable = want;
        }
        void SetAuth(TaskDto? call)
        {
            if (call?.Http == null || tokenFilter == null) return;
            var auth = call.Http.Headers.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
            if (auth == null || auth.Masked) return;
            string want = "Bearer " + Ref(tokenFilter.Id);
            if (auth.Value.Trim() == want) return;
            notes.Add($"Task '{call.Name}' (#{call.Order}) now sends this job's own token (task #{tokenFilter.Order}) instead of {Describe(auth.Value)}.");
            auth.Value = want;
        }

        SetInput(tokenFilter, token);
        SetAuth(create);
        SetInput(runIdFilter, create);
        SetAuth(status);
        if (status?.Http != null && runIdFilter != null)
        {
            string fixedUrl = Regex.Replace(status.Http.Url, @"/pipelineruns/\{TASK\([0-9a-fA-F\-]{36}\|StdOut\)\}", "/pipelineruns/" + Ref(runIdFilter.Id));
            if (fixedUrl != status.Http.Url)
            {
                notes.Add($"Task '{status.Name}' (#{status.Order}) now checks this job's own run ID (task #{runIdFilter.Order}).");
                status.Http.Url = fixedUrl;
            }
        }
        SetInput(statusFilter, status);
        return notes;

        string Describe(string text)
        {
            var m = TaskRef().Match(text ?? "");
            if (!m.Success) return "'" + text + "'";
            var t = job.Tasks.FirstOrDefault(x => x.Id.Equals(m.Groups["id"].Value, StringComparison.OrdinalIgnoreCase));
            return t == null ? $"a task of ANOTHER job ({m.Groups["id"].Value[..8]}…)" : $"task #{t.Order} '{t.Name}'";
        }
    }

    private static (Dictionary<string, string>, bool) ReadParameters(HttpSettingsDto h)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (h.BodyRawMasked) return (result, true);
        if (string.IsNullOrWhiteSpace(h.BodyRaw)) return (result, false);
        try
        {
            if (JsonNode.Parse(h.BodyRaw) is JsonObject o)
                foreach (var (k, v) in o) result[k] = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "null";
        }
        catch (JsonException) { /* body is not JSON (e.g. uses VisualCron variables); leave parameters empty */ }
        return (result, false);
    }

    public static void SetPipeline(JobDto job, string pipelineName)
    {
        if (pipelineName == null || !PipelineName().IsMatch(pipelineName)) throw new VisualCronAIException($"Invalid pipeline name '{pipelineName}'.");
        var view = TryRead(job) ?? throw new VisualCronAIException($"Job '{job.Name}' does not call ADF createRun through an HTTP task.");
        var http = view.CreateRunTask.Http!;
        http.Url = CreateRunUrl().Replace(http.Url, m => m.Value.Replace("/pipelines/" + m.Groups["p"].Value + "/", "/pipelines/" + Uri.EscapeDataString(pipelineName) + "/"), 1);
    }

    /// <summary>Replaces the createRun JSON body with the given parameters (values may use VisualCron variables).</summary>
    public static void SetParameters(JobDto job, IDictionary<string, string> set, IEnumerable<string>? remove)
    {
        var view = TryRead(job) ?? throw new VisualCronAIException($"Job '{job.Name}' does not call ADF createRun through an HTTP task.");
        if (view.ParametersMasked) throw new VisualCronAIException("The createRun body is withheld because it looks like it contains a secret; edit it in the VisualCron client.");
        var http = view.CreateRunTask.Http!;
        if (!string.IsNullOrWhiteSpace(http.BodyRaw) && http.BodyRaw.Trim() != "{}" && view.Parameters.Count == 0)
            throw new VisualCronAIException("The createRun body is not a plain JSON object; edit it in the VisualCron client.");
        var p = new Dictionary<string, string>(view.Parameters, StringComparer.Ordinal);
        foreach (var k in remove ?? []) if (!p.Remove(k)) throw new VisualCronAIException($"Parameter '{k}' is not set.");
        foreach (var (k, v) in set) p[k] = v;
        var body = new JsonObject();
        foreach (var (k, v) in p) body[k] = v;
        http.BodyRaw = p.Count == 0 ? "{}" : body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        http.BodyType = "raw";
        if (string.IsNullOrWhiteSpace(http.ContentType)) http.ContentType = "application/json";
    }

    /// <summary>Points every ADF REST URL of the job at another subscription / resource group / factory.</summary>
    public static int Retarget(JobDto job, string? subscriptionId, string? resourceGroup, string? factoryName)
    {
        int changed = 0;
        foreach (var h in job.Tasks.Select(t => t.Http).Where(h => h != null))
        {
            string before = h!.Url;
            h.Url = FactoryPath().Replace(h.Url, m =>
                $"/subscriptions/{(string.IsNullOrWhiteSpace(subscriptionId) ? m.Groups["sub"].Value : subscriptionId)}" +
                $"/resourceGroups/{(string.IsNullOrWhiteSpace(resourceGroup) ? m.Groups["rg"].Value : resourceGroup)}" +
                $"/providers/Microsoft.DataFactory/factories/{(string.IsNullOrWhiteSpace(factoryName) ? m.Groups["f"].Value : factoryName)}");
            if (h.Url != before) changed++;
        }
        return changed;
    }

    /// <summary>
    /// Makes the job wait for the real pipeline result: the Wait task becomes the poll interval, the status filter
    /// loops back to Wait while the run is Queued/InProgress/Canceling, raises a task error for any other status
    /// (Failed/Cancelled), and a job timeout stops runaway loops (counted as an error).
    /// </summary>
    public static void ApplyPolling(JobDto job, int pollMinutes, int maxRunMinutes)
    {
        if (pollMinutes is < 1 or > 60) throw new VisualCronAIException("Poll interval must be 1-60 minutes.");
        if (maxRunMinutes is < 5 or > 1440) throw new VisualCronAIException("Maximum run time must be 5-1440 minutes.");
        RepairReferences(job);
        var view = TryRead(job) ?? throw new VisualCronAIException($"Job '{job.Name}' does not call ADF through HTTP tasks.");
        if (view.WaitTask == null || view.StatusTask == null || view.StatusFilterTask == null)
            throw new VisualCronAIException("Could not identify the Wait → status HTTP → status JSON filter tasks in this job.");

        view.WaitTask.Wait = new WaitSettingsDto { Minutes = pollMinutes };
        var filter = view.StatusFilterTask;
        var onError = filter.Flows.FirstOrDefault(f => f.EventType == FlowEvents.OnError)
                      ?? new FlowDto { EventType = FlowEvents.OnError, ActionType = FlowActions.StopJob };
        filter.Flows =
        [
            new FlowDto
            {
                EventType = FlowEvents.OnSuccess, ActionType = FlowActions.GotoTask, TaskId = view.WaitTask.Id,
                Condition = "IfOutput", ConditionType = "RegExMatch", ConditionValue = RunningStatesRegex,
            },
            new FlowDto { EventType = FlowEvents.OnSuccess, ActionType = FlowActions.ContinueNextTask },
            onError,
        ];
        filter.OutputErrorRule = new OutputErrorRuleDto { Enabled = true, ConditionType = "RegExNoMatch", Value = OkOrRunningStatesRegex };
        job.Timeout = new TimeoutDto { Enabled = true, Hours = maxRunMinutes / 60, Minutes = maxRunMinutes % 60, ConsiderError = true };
    }

    /// <summary>Normalizes {TASK(&lt;guid&gt;|...)} references so jobs created from the same template compare equal.</summary>
    public static string NormalizeTaskRefs(string? s) => TaskRef().Replace(s ?? "", "{TASK(<task>|");
}
