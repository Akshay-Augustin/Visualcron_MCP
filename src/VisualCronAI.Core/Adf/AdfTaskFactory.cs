using VisualCronAI.Adf.Models;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Core.Adf;

/// <summary>ADF-specific view of an Execute task that runs VisualCronAI.AdfRunner.exe.</summary>
public sealed class AdfTaskView
{
    public string Environment { get; set; } = "";
    public string PipelineName { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
    public int TimeoutMinutes { get; set; }
    public int PollSeconds { get; set; }
}

/// <summary>Builds and recognizes VisualCron Execute tasks that run an ADF pipeline via the runner.</summary>
public static class AdfTaskFactory
{
    public static TaskDto CreateTask(string environment, EnvironmentOptions env, string taskName, string pipelineName,
        IDictionary<string, string>? parameters, int timeoutMinutes, int pollSeconds, int retryTimes, int retryDelayMinutes)
    {
        if (string.IsNullOrWhiteSpace(env.AdfRunnerPath))
            throw new VisualCronAIException($"AdfRunnerPath is not configured for environment {environment}; set VisualCronAI:Environments:{environment}:AdfRunnerPath.");

        var args = new AdfRunnerArguments
        {
            Environment = string.IsNullOrWhiteSpace(env.AdfEnvironment) ? environment : env.AdfEnvironment,
            PipelineName = pipelineName,
            Parameters = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            TimeoutMinutes = timeoutMinutes <= 0 ? AdfRunnerArguments.DefaultTimeoutMinutes : timeoutMinutes,
            PollSeconds = pollSeconds <= 0 ? AdfRunnerArguments.DefaultPollSeconds : pollSeconds,
        };
        var errors = args.Validate();
        if (errors.Count > 0) throw new VisualCronAIException("Invalid ADF task: " + string.Join(" ", errors));

        var task = new TaskDto
        {
            Name = string.IsNullOrWhiteSpace(taskName) ? $"Run ADF pipeline {pipelineName}" : taskName,
            Description = $"Runs Azure Data Factory pipeline '{pipelineName}', waits for completion and fails the task if the pipeline fails.",
            TaskType = TaskTypes.Execute,
            Execute = new ExecuteSettingsDto
            {
                CmdLine = env.AdfRunnerPath,
                Arguments = args.ToCommandLine(),
                WorkingDirectory = Path.GetDirectoryName(env.AdfRunnerPath) ?? "",
                RunHidden = true,
            },
            Flows = [new FlowDto { EventType = FlowEvents.OnSuccess, ActionType = FlowActions.ContinueNextTask }],
        };
        SetErrorPolicy(task, retryTimes, retryDelayMinutes, continueOnError: false);
        return task;
    }

    /// <summary>Replaces the task's OnError flow with "retry N times, then stop job" (or continue / stop without retry).</summary>
    public static void SetErrorPolicy(TaskDto task, int retryTimes, int retryDelayMinutes, bool continueOnError)
    {
        if (retryTimes is < 0 or > 10) throw new VisualCronAIException("Retry count must be between 0 and 10.");
        if (retryDelayMinutes is < 0 or > 1440) throw new VisualCronAIException("Retry delay must be between 0 and 1440 minutes.");
        var existing = task.Flows.FirstOrDefault(f => f.EventType == FlowEvents.OnError);
        task.Flows.RemoveAll(f => f.EventType == FlowEvents.OnError);
        task.Flows.Add(new FlowDto
        {
            Id = existing?.Id ?? "",
            EventType = FlowEvents.OnError,
            ActionType = retryTimes > 0 ? FlowActions.RetryTask : (continueOnError ? FlowActions.ContinueNextTask : FlowActions.StopJob),
            RetryTimes = retryTimes > 0 ? retryTimes : 0,
            RetryDelayMinutes = retryTimes > 0 ? retryDelayMinutes : 0,
            RetryDelaySeconds = retryTimes > 0 && retryDelayMinutes == 0 ? 30 : 0,
            RetryFailAction = "StopJob",
        });
    }

    public static AdfTaskView? TryRead(TaskDto task)
    {
        if (task.TaskType != TaskTypes.Execute || task.Execute == null || !AdfRunnerArguments.IsRunnerCommand(task.Execute.CmdLine)) return null;
        try
        {
            var a = AdfRunnerArguments.Parse(task.Execute.Arguments);
            return new AdfTaskView
            {
                Environment = a.Environment,
                PipelineName = a.PipelineName,
                Parameters = a.Parameters,
                TimeoutMinutes = a.TimeoutMinutes,
                PollSeconds = a.PollSeconds,
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Rewrites an ADF task's arguments after <paramref name="mutate"/> changes the parsed view.</summary>
    public static void Modify(TaskDto task, Action<AdfRunnerArguments> mutate)
    {
        if (task.Execute == null || !AdfRunnerArguments.IsRunnerCommand(task.Execute.CmdLine))
            throw new VisualCronAIException($"Task '{task.Name}' is not an ADF pipeline task.");
        var args = AdfRunnerArguments.Parse(task.Execute.Arguments);
        mutate(args);
        var errors = args.Validate();
        if (errors.Count > 0) throw new VisualCronAIException("Invalid ADF task: " + string.Join(" ", errors));
        task.Execute.Arguments = args.ToCommandLine();
    }

    /// <summary>Points an ADF task at another environment's runner and ADF configuration (used by clone).</summary>
    public static bool Retarget(TaskDto task, string targetEnvironment, EnvironmentOptions target)
    {
        if (TryRead(task) == null) return false;
        Modify(task, a => a.Environment = string.IsNullOrWhiteSpace(target.AdfEnvironment) ? targetEnvironment : target.AdfEnvironment);
        if (!string.IsNullOrWhiteSpace(target.AdfRunnerPath))
        {
            task.Execute!.CmdLine = target.AdfRunnerPath;
            task.Execute.WorkingDirectory = Path.GetDirectoryName(target.AdfRunnerPath) ?? "";
        }
        return true;
    }
}
