using System.Diagnostics;
using VisualCronAI.Adf.Models;

namespace VisualCronAI.Adf.Services;

/// <summary>
/// Starts an ADF pipeline, prints its run ID, polls until it reaches a terminal state and returns a process exit
/// code. VisualCron captures stdout/stderr as task output and treats a non-zero exit code as task failure, which
/// then drives the job's retry flows and error notifications.
/// </summary>
public sealed class AdfPipelineRunner(IAdfClient client, TextWriter output, TextWriter error,
    Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? time = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private const int MaxTransientRetries = 5;

    public async Task<int> RunAsync(AdfRunnerArguments args, CancellationToken ct)
    {
        var validation = args.Validate();
        if (validation.Count > 0)
        {
            foreach (var v in validation) Log(error, "CONFIG ERROR: " + v);
            return RunnerExitCodes.ConfigurationError;
        }

        Log(output, $"Starting pipeline '{args.PipelineName}' in environment {args.Environment} with {args.Parameters.Count} parameter(s).");
        string runId;
        try
        {
            // createRun is NOT retried on ambiguous network failures (it could start a duplicate run);
            // only on explicit throttling / service-unavailable responses.
            runId = await WithRetry(() => client.CreateRunAsync(args.PipelineName, args.Parameters, ct), retryNetwork: false, ct);
        }
        catch (AdfApiException ex)
        {
            Log(error, "Failed to start pipeline: " + ex.Message);
            return RunnerExitCodes.AzureApiError;
        }

        Log(output, $"RunId={runId}");
        var started = DateTimeOffset.UtcNow;
        long startTs = _time.GetTimestamp();
        TimeSpan Elapsed() => _time.GetElapsedTime(startTs);
        var timeout = TimeSpan.FromMinutes(args.TimeoutMinutes);
        string lastStatus = "";
        PipelineRunInfo run;

        while (true)
        {
            try
            {
                run = await WithRetry(() => client.GetRunAsync(runId, ct), retryNetwork: true, ct);
            }
            catch (AdfApiException ex)
            {
                Log(error, $"Lost track of run {runId}: {ex.Message}");
                return RunnerExitCodes.AzureApiError;
            }

            if (run.Status != lastStatus)
            {
                Log(output, $"Status={run.Status} (elapsed {Elapsed():hh\\:mm\\:ss})");
                lastStatus = run.Status;
            }
            if (PipelineRunStatus.IsTerminal(run.Status)) break;

            if (Elapsed() >= timeout)
            {
                Log(error, $"Timed out after {args.TimeoutMinutes} minute(s) waiting for run {runId} (status {run.Status}).");
                if (args.CancelOnTimeout)
                {
                    try { await client.CancelRunAsync(runId, ct); Log(error, $"Cancellation requested for run {runId}."); }
                    catch (AdfApiException ex) { Log(error, $"Cancel request failed: {ex.Message}"); }
                }
                return RunnerExitCodes.TimedOut;
            }
            await _delay(TimeSpan.FromSeconds(args.PollSeconds), ct);
        }

        string duration = run.DurationInMs is long ms ? TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss") : Elapsed().ToString(@"hh\:mm\:ss");
        switch (run.Status)
        {
            case PipelineRunStatus.Succeeded:
                Log(output, $"Pipeline '{args.PipelineName}' succeeded. RunId={runId} Duration={duration}");
                return RunnerExitCodes.Succeeded;
            case PipelineRunStatus.Cancelled:
                Log(error, $"Pipeline '{args.PipelineName}' was cancelled. RunId={runId}");
                return RunnerExitCodes.PipelineCancelled;
            default:
                Log(error, $"Pipeline '{args.PipelineName}' FAILED. RunId={runId} Duration={duration} Message={run.Message}");
                await ReportFailedActivities(runId, started, ct);
                return RunnerExitCodes.PipelineFailed;
        }
    }

    private async Task ReportFailedActivities(string runId, DateTimeOffset started, CancellationToken ct)
    {
        try
        {
            var activities = await client.QueryActivityRunsAsync(runId, started.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5), ct);
            foreach (var a in activities.Where(a => a.Status == PipelineRunStatus.Failed))
                Log(error, $"  Activity '{a.ActivityName}' ({a.ActivityType}) failed: [{a.ErrorCode}] {a.ErrorMessage}");
        }
        catch (AdfApiException ex)
        {
            Log(error, "Could not query activity runs: " + ex.Message);
        }
    }

    private async Task<T> WithRetry<T>(Func<Task<T>> action, bool retryNetwork, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (AdfApiException ex) when (ex.Transient && attempt <= MaxTransientRetries && (retryNetwork || ex.Status != null))
            {
                var wait = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
                Log(error, $"Transient ADF error (attempt {attempt}/{MaxTransientRetries}): {ex.Message}. Retrying in {wait.TotalSeconds:0}s.");
                await _delay(wait, ct);
            }
        }
    }

    private static void Log(TextWriter w, string message) => w.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [adf-runner] {message}");
}
