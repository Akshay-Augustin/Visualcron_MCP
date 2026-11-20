using VisualCronAI.Contracts;
using VisualCronAI.Core.Adf;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Tests;

/// <summary>
/// One VisualCron server hosting DEV, UAT and PROD as job groups ("DEV: ADF-D365 Loads", "UAT: ...", "PROD: ..."),
/// as on the real server. Environments are scoped by JobGroups and fenced by WritableJobGroups.
/// </summary>
public class SharedServerTests
{
    private static TestHost Host()
    {
        var h = new TestHost(configure: o =>
        {
            foreach (var (env, prefix) in new[] { ("DEV", "DEV"), ("UAT", "UAT"), ("PROD", "PROD") })
            {
                var e = o.Environments[env];
                e.Address = "vc-shared";
                e.JobGroups = [prefix + ":*"];
                e.WritableJobGroups = [prefix + ":*"];
                e.DefaultJobGroup = prefix + ": ADF-D365 Loads";
                e.EnvironmentTokens = [prefix + ":", "_" + prefix];
            }
        });
        h.Gateway.ShareServer("DEV", "UAT", "PROD");
        return h;
    }

    private static JobDto AdfJob(string env, string name, string pipeline) => new()
    {
        Name = name, Group = env + ": ADF-D365 Loads", Active = true,
        Tasks = [AdfTaskFactory.CreateTask(env, new EnvironmentOptions { AdfRunnerPath = @"C:\VisualCronAI\adf-runner\VisualCronAI.AdfRunner.exe", AdfEnvironment = env }, "", pipeline, null, 0, 0, 1, 5)],
        Triggers = [new TriggerDto { Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Daily, Times = ["01:30"] } }],
    };

    [Theory]
    [InlineData("DEV:*", "DEV: ADF-D365 Loads", true)]
    [InlineData("DEV:*", "dev: ADF-D365 Loads", true)]
    [InlineData("DEV:*", "UAT: ADF-D365 Loads", false)]
    [InlineData("DEV:*", "Default group", false)]
    [InlineData("DEV: ADF-D365 Loads", "DEV: ADF-D365 Loads", true)]
    [InlineData("*D365*", "PROD: ADF-D365 Loads", true)]
    public void Group_patterns(string pattern, string group, bool expected) => Assert.Equal(expected, GroupPattern.Matches(pattern, group));

    [Fact]
    public async Task Each_environment_only_sees_its_groups()
    {
        var h = Host();
        h.Gateway.Add("DEV", AdfJob("DEV", "Products - Trigger ADF", "p_D365_Product_Load"));
        h.Gateway.Add("DEV", AdfJob("UAT", "Products - Trigger ADF", "p_D365_Product_Load"));
        var prod = h.Gateway.Add("DEV", AdfJob("PROD", "Products - Trigger ADF", "p_D365_Product_Load"));

        Assert.Single(await h.Service.ScopedJobsAsync("DEV", default), j => j.Group.StartsWith("DEV:"));
        var dev = await h.Service.ResolveJobAsync("DEV", "Products - Trigger ADF", default);   // same name in 3 groups: no ambiguity
        Assert.StartsWith("DEV:", dev.Group);
        var ex = await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ResolveJobAsync("DEV", prod.Id, default));
        Assert.Contains("not part of environment DEV", ex.Message);
    }

    [Fact]
    public async Task Dev_cannot_move_a_job_into_prod_group()
    {
        var h = Host();
        h.Gateway.Add("DEV", AdfJob("DEV", "Devices - Trigger ADF", "p_D365_Devices"));
        var p = await h.Service.ProposeJobChangesAsync("DEV", "Devices - Trigger ADF", new JobChangesInput { Group = "PROD: ADF-D365 Loads" }, default);
        Assert.Equal(ProposalStatus.Blocked, p.Status);
        Assert.Contains(p.Validation.Errors, e => e.Path == "job.group");
    }

    [Fact]
    public async Task Same_name_is_allowed_in_another_group_but_not_the_same_group()
    {
        var h = Host();
        h.Gateway.Add("DEV", AdfJob("UAT", "D365_Product_Daily_Load", "p_D365_Product_Load"));
        var ok = await h.Service.DraftJobAsync(TestHost.D365Draft("DEV"), default);
        Assert.True(ok.Validation.IsValid, string.Join("; ", ok.Validation.Errors));
        Assert.Equal("DEV: ADF-D365 Loads", ok.Job.Group);

        h.Gateway.Add("DEV", AdfJob("DEV", "D365_Product_Daily_Load", "p_D365_Product_Load"));
        var dup = await h.Service.DraftJobAsync(TestHost.D365Draft("DEV"), default);
        Assert.Contains(dup.Validation.Errors, e => e.Path == "job.name");
    }

    [Fact]
    public async Task Clone_dev_to_uat_on_the_same_server()
    {
        var h = Host();
        h.Gateway.Notifications["DEV"].Add(new NamedRefDto { Id = "11111111-1111-1111-1111-111111111111", Name = "D365 failures" });
        var devUpstream = h.Gateway.Add("DEV", AdfJob("DEV", "Products - Trigger ADF", "p_D365_Product_Load"));
        var uatUpstream = h.Gateway.Add("DEV", AdfJob("UAT", "Products - Trigger ADF", "p_D365_Product_Load"));
        var rental = AdfJob("DEV", "RentalItems - Trigger ADF", "p_D365_RentalItems");
        rental.ErrorNotificationIds = ["11111111-1111-1111-1111-111111111111"];
        rental.Triggers.Add(new TriggerDto { Kind = TriggerKinds.JobEvent, JobEvent = new JobEventDto { UpstreamJobId = devUpstream.Id, UpstreamJobName = devUpstream.Name, Condition = JobEventConditions.CompletedSuccessfully } });
        h.Gateway.Add("DEV", rental);

        var p = await h.Service.ProposeCloneAsync(new CloneInput { SourceEnvironment = "DEV", SourceJob = "RentalItems - Trigger ADF", TargetEnvironment = "UAT" }, default);

        Assert.Equal(ProposalStatus.PendingApproval, p.Status);
        Assert.Equal("RentalItems - Trigger ADF", p.After.Name);                 // same name, different group
        Assert.Equal("UAT: ADF-D365 Loads", p.After.Group);
        Assert.Equal("UAT", AdfTaskFactory.TryRead(p.After.Tasks[0])!.Environment);
        Assert.Equal(uatUpstream.Id, p.After.Triggers.Single(t => t.Kind == TriggerKinds.JobEvent).JobEvent!.UpstreamJobId);
        Assert.Contains("11111111-1111-1111-1111-111111111111", p.After.ErrorNotificationIds); // same server: notification still valid
        Assert.False(p.After.Active);
        Assert.Empty(h.Gateway.Writes);
    }

    [Fact]
    public async Task Uat_writes_stay_disabled_by_default_config()
    {
        var h = Host();
        h.Options.Environments["UAT"].AllowWrites = false;
        h.Gateway.Add("DEV", AdfJob("DEV", "Products - Trigger ADF", "p_D365_Product_Load"));
        var p = await h.Service.ProposeCloneAsync(new CloneInput { SourceEnvironment = "DEV", SourceJob = "Products - Trigger ADF", TargetEnvironment = "UAT" }, default);
        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true)], default));
        Assert.Empty(h.Gateway.Writes);
    }
}
