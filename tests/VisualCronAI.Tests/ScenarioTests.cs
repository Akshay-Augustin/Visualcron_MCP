using VisualCronAI.Contracts;
using VisualCronAI.Core.Adf;
using VisualCronAI.Core.Diff;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Tests;

/// <summary>The six natural-language examples from the requirements, exercised at the service layer.</summary>
public class ScenarioTests
{
    [Fact]
    public async Task Example1_create_daily_adf_job_at_2am_central()
    {
        var h = new TestHost();
        var d = await h.Service.DraftJobAsync(TestHost.D365Draft(), default);

        Assert.True(d.Validation.IsValid, string.Join("; ", d.Validation.Errors));
        var task = Assert.Single(d.Job.Tasks);
        var adf = AdfTaskFactory.TryRead(task)!;
        Assert.Equal("p_D365_Product_Load", adf.PipelineName);
        Assert.Equal("DEV", adf.Environment);
        Assert.Equal(@"C:\VisualCronAI\adf-runner\VisualCronAI.AdfRunner.exe", task.Execute!.CmdLine);
        var trigger = Assert.Single(d.Job.Triggers);
        Assert.Equal(ScheduleModes.Daily, trigger.Schedule!.Mode);
        Assert.Equal(["02:00"], trigger.Schedule.Times);           // server is Central too → no conversion
        Assert.Equal("", trigger.Schedule.TimeZone);              // normalized to server-local
        Assert.Equal("ADF", d.Job.Group);
    }

    [Fact]
    public async Task Example1_times_are_converted_when_server_is_in_another_zone()
    {
        var h = new TestHost();
        h.Gateway.TimeZones["DEV"] = "Eastern Standard Time";
        var d = await h.Service.DraftJobAsync(TestHost.D365Draft(), default);
        Assert.Equal(["03:00"], d.Job.Triggers[0].Schedule!.Times);
        Assert.Contains(d.Warnings, w => w.Contains("converted"));
    }

    [Fact]
    public async Task Example2_clone_dev_job_to_uat_replaces_environment_specifics()
    {
        var h = new TestHost();
        h.Gateway.Notifications["DEV"].Add(new NamedRefDto { Id = "11111111-1111-1111-1111-111111111111", Name = "DEV failures" });
        var d = await h.Service.DraftJobAsync(new JobDraftInput
        {
            Environment = "DEV", Name = "D365_Product_Load_DEV",
            Tasks = [new TaskInput { PipelineName = "p_D365_Product_Load", Parameters = new() { ["SourceDb"] = "d365_dev" }, RetryTimes = 2 }],
            Schedules = [new ScheduleInput { Times = ["02:00"] }],
            ErrorNotificationIds = ["11111111-1111-1111-1111-111111111111"],
        }, default);
        var create = await h.Service.ProposeCreateAsync(d.DraftId, default);
        await h.Service.RequestApprovalAsync(create.Id, [new FakeApproval(true)], default);
        await h.Service.ApplyAsync(create.Id, default);

        var clone = await h.Service.ProposeCloneAsync(new CloneInput { SourceEnvironment = "DEV", SourceJob = "D365_Product_Load_DEV", TargetEnvironment = "UAT" }, default);

        Assert.Equal(ProposalStatus.PendingApproval, clone.Status);
        Assert.Equal("D365_Product_Load_UAT", clone.After.Name);
        Assert.False(clone.After.Active);
        var adf = AdfTaskFactory.TryRead(clone.After.Tasks[0])!;
        Assert.Equal("UAT", adf.Environment);
        Assert.Equal("d365_uat", adf.Parameters["SourceDb"]);
        Assert.Equal(@"D:\Tools\VisualCronAI.AdfRunner.exe", clone.After.Tasks[0].Execute!.CmdLine);
        Assert.Empty(clone.After.ErrorNotificationIds);   // DEV notification does not exist on the UAT server
        Assert.Contains(clone.Warnings, w => w.Contains("do not exist in UAT"));
        Assert.Contains(clone.Changes, c => c.Contains("adf.param[SourceDb]") && c.Contains("d365_uat"));
        Assert.Equal(1, h.Gateway.Writes.Count); // only the original create

        await h.Service.RequestApprovalAsync(clone.Id, [new FakeApproval(true)], default);
        await h.Service.ApplyAsync(clone.Id, default);
        Assert.Contains("clone:DEV->UAT:D365_Product_Load_UAT", h.Gateway.Writes);
    }

    [Fact]
    public async Task Example3_change_schedule_to_3am()
    {
        var h = new TestHost();
        h.Gateway.Add("DEV", new JobDto
        {
            Name = "Salesforce_Account_Load", Group = "ADF",
            Tasks = [new TaskDto { Name = "run", TaskType = TaskTypes.Execute, Execute = new ExecuteSettingsDto { CmdLine = "x.exe" } }],
            Triggers = [new TriggerDto { Id = "t1", Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Daily, Times = ["02:00"] } }],
        });
        var p = await h.Service.ProposeScheduleAsync("DEV", "salesforce_account_load", [new ScheduleInput { Mode = "Daily", Times = ["3:00"] }], null, true, default);

        var t = Assert.Single(p.After.Triggers);
        Assert.Equal(["03:00"], t.Schedule!.Times);
        Assert.Contains(p.Changes, c => c.Contains("daily at 02:00") && c.StartsWith("[Schedule] -"));
        Assert.Contains(p.Changes, c => c.Contains("daily at 03:00") && c.StartsWith("[Schedule] +"));
    }

    [Fact]
    public async Task Example4_sequential_pipelines_and_cross_job_dependency()
    {
        var h = new TestHost();
        // a) One job, two tasks: Contact runs only after Account succeeds (task order + stop-on-error).
        var d = await h.Service.DraftJobAsync(new JobDraftInput
        {
            Environment = "DEV", Name = "Salesforce_Account_Contact",
            Tasks = [new TaskInput { PipelineName = "p_SF_Account" }, new TaskInput { PipelineName = "p_SF_Contact" }],
            Schedules = [new ScheduleInput { Times = ["01:00"] }],
        }, default);
        Assert.True(d.Job.RunTasksInOrder);
        Assert.Equal(["p_SF_Account", "p_SF_Contact"], d.Job.Tasks.OrderBy(t => t.Order).Select(t => AdfTaskFactory.TryRead(t)!.PipelineName));
        Assert.All(d.Job.Tasks, t => Assert.Equal(FlowActions.StopJob, t.Flows.Single(f => f.EventType == FlowEvents.OnError).ActionType));

        // b) Separate job triggered when the Account job completes successfully.
        var account = h.Gateway.Add("DEV", new JobDto { Name = "SF_Account", Group = "ADF", Tasks = [new TaskDto { Name = "a", TaskType = TaskTypes.Execute, Execute = new ExecuteSettingsDto { CmdLine = "a.exe" } }] });
        var d2 = await h.Service.DraftJobAsync(new JobDraftInput
        {
            Environment = "DEV", Name = "SF_Contact",
            Tasks = [new TaskInput { PipelineName = "p_SF_Contact" }],
            DependsOn = [new DependencyInput { Job = "SF_Account" }],
        }, default);
        var dep = Assert.Single(d2.Job.Triggers);
        Assert.Equal(TriggerKinds.JobEvent, dep.Kind);
        Assert.Equal(account.Id, dep.JobEvent!.UpstreamJobId);
        Assert.Equal(JobEventConditions.CompletedSuccessfully, dep.JobEvent.Condition);
        Assert.True(d2.Validation.IsValid, string.Join("; ", d2.Validation.Errors));
    }

    [Fact]
    public async Task Example5_retry_twice_and_notify_on_final_failure()
    {
        var h = new TestHost();
        h.Gateway.Notifications["DEV"].Add(new NamedRefDto { Id = "22222222-2222-2222-2222-222222222222", Name = "Email data team", Type = "Email" });
        var d = await h.Service.DraftJobAsync(TestHost.D365Draft(), default);
        var c = await h.Service.ProposeCreateAsync(d.DraftId, default);
        await h.Service.RequestApprovalAsync(c.Id, [new FakeApproval(true)], default);
        await h.Service.ApplyAsync(c.Id, default);

        var p = await h.Service.ProposeJobChangesAsync("DEV", "D365_Product_Daily_Load", new JobChangesInput
        {
            RetryTimes = 2, RetryDelayMinutes = 10, AddErrorNotificationIds = ["22222222-2222-2222-2222-222222222222"],
        }, default);

        Assert.True(p.Validation.IsValid, string.Join("; ", p.Validation.Errors));
        var onError = p.After.Tasks[0].Flows.Single(f => f.EventType == FlowEvents.OnError);
        Assert.Equal(FlowActions.RetryTask, onError.ActionType);
        Assert.Equal(2, onError.RetryTimes);
        Assert.Equal(10, onError.RetryDelayMinutes);
        Assert.Equal("StopJob", onError.RetryFailAction);
        Assert.Contains("22222222-2222-2222-2222-222222222222", p.After.ErrorNotificationIds);
        Assert.Contains(p.Changes, x => x.StartsWith("[ErrorHandling]") && x.Contains("retry 2x"));
    }

    [Fact]
    public async Task Example5_unknown_notification_is_rejected()
    {
        var h = new TestHost();
        h.Gateway.Add("DEV", new JobDto { Name = "J", Group = "ADF", Tasks = [new TaskDto { Name = "x", TaskType = TaskTypes.Execute, Execute = new ExecuteSettingsDto { CmdLine = "a.exe" } }] });
        var p = await h.Service.ProposeJobChangesAsync("DEV", "J", new JobChangesInput { ErrorNotificationIds = ["33333333-3333-3333-3333-333333333333"] }, default);
        Assert.Equal(ProposalStatus.Blocked, p.Status);
    }

    [Fact]
    public void Example6_compare_dev_and_uat_versions()
    {
        JobDto Make(string env, string param, string time, int retries) => new()
        {
            Name = $"D365_Product_Load_{env}", Group = "ADF", Active = true,
            Tasks = [new TaskDto
            {
                Name = "Run ADF pipeline p_D365_Product_Load", TaskType = TaskTypes.Execute,
                Execute = new ExecuteSettingsDto { CmdLine = @"C:\r\VisualCronAI.AdfRunner.exe", Arguments = $"run --env {env} --pipeline p_D365_Product_Load --param LoadType={param}" },
                Flows = [new FlowDto { EventType = FlowEvents.OnError, ActionType = FlowActions.RetryTask, RetryTimes = retries, RetryDelayMinutes = 5 }],
            }],
            Triggers = [new TriggerDto { Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Daily, Times = [time] } }],
        };
        var diffs = JobComparer.Compare(Make("DEV", "Full", "02:00", 2), Make("UAT", "Delta", "03:00", 1),
            new CompareOptions { NormalizeEnvironmentTokens = true, LeftTokens = ["DEV"], RightTokens = ["UAT"] });

        Assert.DoesNotContain(diffs, d => d.Path == "name");                 // only differs by env token
        Assert.DoesNotContain(diffs, d => d.Path.EndsWith("adf.environment"));
        Assert.Contains(diffs, d => d.Area == "Parameters" && d.Path.Contains("LoadType") && d.Left == "Full" && d.Right == "Delta");
        Assert.Contains(diffs, d => d.Area == "Schedule" && d.Kind == DiffKind.Removed && d.Left!.Contains("02:00"));
        Assert.Contains(diffs, d => d.Area == "Schedule" && d.Kind == DiffKind.Added && d.Right!.Contains("03:00"));
        Assert.Contains(diffs, d => d.Area == "ErrorHandling" && d.Left!.Contains("retry 2x") && d.Right!.Contains("retry 1x"));
    }

    [Fact]
    public async Task Update_task_parameters_and_remove_task()
    {
        var h = new TestHost();
        var d = await h.Service.DraftJobAsync(new JobDraftInput
        {
            Environment = "DEV", Name = "Two_Step",
            Tasks = [new TaskInput { PipelineName = "p_A", Parameters = new() { ["x"] = "1" } }, new TaskInput { PipelineName = "p_B" }],
        }, default);
        var c = await h.Service.ProposeCreateAsync(d.DraftId, default);
        await h.Service.RequestApprovalAsync(c.Id, [new FakeApproval(true)], default);
        await h.Service.ApplyAsync(c.Id, default);

        var upd = await h.Service.ProposeUpdateTaskAsync("DEV", "Two_Step", "p_A", new TaskChangesInput { SetParameters = new() { ["LoadDate"] = "{DATEFORMAT(yyyy-MM-dd)}" }, RemoveParameters = ["x"] }, default);
        var adf = AdfTaskFactory.TryRead(upd.After.Tasks.OrderBy(t => t.Order).First())!;
        Assert.Equal("{DATEFORMAT(yyyy-MM-dd)}", adf.Parameters["LoadDate"]);
        Assert.False(adf.Parameters.ContainsKey("x"));

        var rm = await h.Service.ProposeRemoveTaskAsync("DEV", "Two_Step", "2", default);
        Assert.Single(rm.After.Tasks);
        Assert.Contains(rm.Changes, x => x.StartsWith("[Task] -"));
    }

    [Fact]
    public async Task Enable_disable_and_noop_detection()
    {
        var h = new TestHost();
        h.Gateway.Add("DEV", new JobDto { Name = "J", Group = "ADF", Active = true, Tasks = [new TaskDto { Name = "x", TaskType = TaskTypes.Execute, Execute = new ExecuteSettingsDto { CmdLine = "a.exe" } }] });
        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ProposeSetActiveAsync("DEV", "J", true, default));
        var p = await h.Service.ProposeSetActiveAsync("DEV", "J", false, default);
        Assert.Equal(ProposalOperation.Disable, p.Operation);
        await h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true)], default);
        await h.Service.ApplyAsync(p.Id, default);
        Assert.Single(h.Gateway.Writes, w => w.StartsWith("active:DEV:") && w.EndsWith(":False"));

        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ProposeJobChangesAsync("DEV", "J", new JobChangesInput { Group = "ADF" }, default));
    }

    [Fact]
    public async Task Ambiguous_and_unknown_job_references_are_explained()
    {
        var h = new TestHost();
        h.Gateway.Add("DEV", new JobDto { Name = "D365_Product_Load", Group = "A" });
        h.Gateway.Add("DEV", new JobDto { Name = "D365_Product_Load", Group = "B" });
        var ex = await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ResolveJobAsync("DEV", "D365_Product_Load", default));
        Assert.Contains("use the job ID", ex.Message);
        var ex2 = await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ResolveJobAsync("DEV", "D365", default));
        Assert.Contains("Did you mean", ex2.Message);
        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ResolveJobAsync("QA", "x", default));
    }
}
