using VisualCronAI.Contracts;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Tests;

/// <summary>Server-side enforcement of the two-phase workflow: nothing is written without a human approval.</summary>
public class ApprovalWorkflowTests
{
    private static async Task<(TestHost H, ChangeProposal P)> PreparedCreate(TestHost? host = null, string env = "DEV")
    {
        var h = host ?? new TestHost();
        var draft = await h.Service.DraftJobAsync(TestHost.D365Draft(env), default);
        var p = await h.Service.ProposeCreateAsync(draft.DraftId, default);
        return (h, p);
    }

    [Fact]
    public async Task Preparing_a_change_writes_nothing()
    {
        var (h, p) = await PreparedCreate();
        Assert.Equal(ProposalStatus.PendingApproval, p.Status);
        Assert.Empty(h.Gateway.Writes);
        Assert.Contains(h.Audit.Events, e => e.EventType == "ProposalCreated");
    }

    [Fact]
    public async Task Apply_without_approval_is_refused()
    {
        var (h, p) = await PreparedCreate();
        var ex = await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ApplyAsync(p.Id, default));
        Assert.Contains("approve", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Gateway.Writes);
    }

    [Fact]
    public async Task Approved_change_is_applied_once_and_audited()
    {
        var (h, p) = await PreparedCreate();
        var approval = new FakeApproval(approve: true);
        await h.Service.RequestApprovalAsync(p.Id, [approval], default);
        var applied = await h.Service.ApplyAsync(p.Id, default);

        Assert.Equal(ProposalStatus.Applied, applied.Status);
        Assert.Single(h.Gateway.Writes);
        Assert.Equal(1, approval.Calls);
        Assert.Contains("D365_Product_Daily_Load", approval.LastRequest!.Details);
        Assert.Equal(["ProposalCreated", "ApprovalRequested", "ApprovalGranted", "ApplyStarted", "ApplySucceeded"], h.Audit.Events.Select(e => e.EventType));

        // Single use: a second apply is refused.
        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ApplyAsync(p.Id, default));
        Assert.Single(h.Gateway.Writes);
    }

    [Fact]
    public async Task Rejected_change_cannot_be_applied()
    {
        var (h, p) = await PreparedCreate();
        var result = await h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(approve: false)], default);
        Assert.Equal(ProposalStatus.Rejected, result.Status);
        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ApplyAsync(p.Id, default));
        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true)], default));
        Assert.Empty(h.Gateway.Writes);
        Assert.Contains(h.Audit.Events, e => e.EventType == "ApprovalDenied");
    }

    [Fact]
    public async Task No_human_channel_means_no_approval()
    {
        var (h, p) = await PreparedCreate();
        var ex = await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true, available: false)], default));
        Assert.Contains("No human approval channel", ex.Message);
        Assert.Equal(ProposalStatus.PendingApproval, p.Status);
    }

    [Fact]
    public async Task Writes_disabled_environment_cannot_be_approved()
    {
        var h = new TestHost(configure: o => o.Environments["UAT"].AllowWrites = false);
        var (_, p) = await PreparedCreate(h, "UAT");
        Assert.Contains(p.Warnings, w => w.Contains("AllowWrites=false"));
        var approval = new FakeApproval(true);
        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.RequestApprovalAsync(p.Id, [approval], default));
        Assert.Equal(0, approval.Calls);
        Assert.Empty(h.Gateway.Writes);
    }

    [Fact]
    public async Task Production_requires_listed_approver()
    {
        var (h, p) = await PreparedCreate(new TestHost(user: @"CORP\alice"), "PROD");
        Assert.True(p.IsProduction);
        var ex = await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true, "PROD D365_Product_Daily_Load")], default));
        Assert.Contains("ProductionApprovers", ex.Message);
        Assert.Empty(h.Gateway.Writes);
    }

    [Fact]
    public async Task Production_requires_exact_typed_confirmation()
    {
        var (h, p) = await PreparedCreate(new TestHost(user: @"CORP\prodlead"), "PROD");
        var result = await h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true, confirmation: "yes")], default);
        Assert.Equal(ProposalStatus.Rejected, result.Status);
        Assert.Contains("confirmation", result.Approval!.Reason);
        Assert.Empty(h.Gateway.Writes);

        var (h2, p2) = await PreparedCreate(new TestHost(user: @"CORP\prodlead"), "PROD");
        var approval = new FakeApproval(true, confirmation: "PROD D365_Product_Daily_Load");
        await h2.Service.RequestApprovalAsync(p2.Id, [approval], default);
        Assert.Equal("PROD D365_Product_Daily_Load", approval.LastRequest!.RequiredConfirmationText);
        var applied = await h2.Service.ApplyAsync(p2.Id, default);
        Assert.Equal(ProposalStatus.Applied, applied.Status);
        Assert.True(applied.Approval!.ProductionConfirmed);
    }

    [Fact]
    public async Task Expired_approval_is_refused()
    {
        var (h, p) = await PreparedCreate();
        await h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true)], default);
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(h.Options.Approval.ApprovalTtlMinutes + 1);
        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ApplyAsync(p.Id, default));
        Assert.Empty(h.Gateway.Writes);
    }

    [Fact]
    public async Task Tampering_after_approval_is_detected()
    {
        var (h, p) = await PreparedCreate();
        await h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true)], default);
        p.After.Tasks[0].Execute!.Arguments += " --param injected=1";
        var ex = await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ApplyAsync(p.Id, default));
        Assert.Contains("does not match", ex.Message);
        Assert.Empty(h.Gateway.Writes);
    }

    [Fact]
    public async Task Concurrent_modification_fails_safely()
    {
        var h = new TestHost();
        var job = h.Gateway.Add("DEV", new JobDto { Name = "Salesforce_Account_Load", Group = "ADF", Tasks = [new TaskDto { Name = "x", TaskType = TaskTypes.Execute, Execute = new ExecuteSettingsDto { CmdLine = "a.exe" } }] });
        var p = await h.Service.ProposeJobChangesAsync("DEV", "Salesforce_Account_Load", new JobChangesInput { Description = "new" }, default);
        await h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true)], default);

        h.Gateway.Jobs["DEV"][job.Id].VersionToken = "someone-else-edited";
        var ex = await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ApplyAsync(p.Id, default));
        Assert.Contains("concurrency_conflict", ex.Message);
        Assert.Equal(ProposalStatus.Failed, p.Status);
        Assert.Empty(h.Gateway.Writes);
        Assert.Contains(h.Audit.Events, e => e.EventType == "ApplyFailed");
    }

    [Fact]
    public async Task Bridge_verification_failure_is_reported_as_failed()
    {
        var (h, p) = await PreparedCreate();
        await h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true)], default);
        h.Gateway.FailNextWriteWith = BridgeErrorCodes.VerificationFailed;
        var ex = await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.ApplyAsync(p.Id, default));
        Assert.Contains("verification_failed", ex.Message);
        Assert.Equal(ProposalStatus.Failed, p.Status);
    }

    [Fact]
    public async Task Blocked_proposal_cannot_be_approved()
    {
        var h = new TestHost();
        var input = TestHost.D365Draft();
        input.Tasks[0].Parameters = new() { ["conn"] = "Server=x;Password=Sup3rS3cret" };
        var d = await h.Service.DraftJobAsync(input, default);
        Assert.False(d.Validation.IsValid);
        var p = await h.Service.ProposeCreateAsync(d.DraftId, default);
        Assert.Equal(ProposalStatus.Blocked, p.Status);
        await Assert.ThrowsAsync<VisualCronAIException>(() => h.Service.RequestApprovalAsync(p.Id, [new FakeApproval(true)], default));
    }

    [Fact]
    public async Task Writable_job_groups_are_enforced()
    {
        var h = new TestHost(configure: o => o.Environments["DEV"].WritableJobGroups = ["ADF"]);
        var input = TestHost.D365Draft();
        input.Group = "Finance";
        var d = await h.Service.DraftJobAsync(input, default);
        Assert.Contains(d.Validation.Errors, e => e.Path == "job.group");
    }
}
