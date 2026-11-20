using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Models;
using VisualCronAI.Core.Services;
using VisualCronAI.Infrastructure.Api;
using VisualCronAI.Infrastructure.Authentication;

namespace VisualCronAI.Tests;

/// <summary>
/// Runs only when explicitly enabled against a DEV VisualCron server:
///   $env:VISUALCRONAI_IT_DEV_ADDRESS = "vc-dev-server"; $env:VISUALCRONAI_IT_DEV_PORT = "16444"  (optional)
///   dotnet test --filter Category=DevIntegration
/// Uses Windows/AD logon of the current user. Creates only VCAI_IT_* jobs (inactive) and deletes them afterwards.
/// Refuses to run against this machine (its VisualCron service is UAT/PROD) or any loopback address.
/// </summary>
public sealed class DevIntegrationFactAttribute : FactAttribute
{
    public DevIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VISUALCRONAI_IT_DEV_ADDRESS")))
            Skip = "Set VISUALCRONAI_IT_DEV_ADDRESS to a DEV VisualCron server to run integration tests.";
    }
}

[Trait("Category", "DevIntegration")]
public sealed class DevIntegrationTests : IAsyncLifetime
{
    private BridgeProcessGateway _gateway = null!;
    private ChangeService _service = null!;
    private readonly List<string> _created = [];
    private const string Env = "DEV";

    public async Task InitializeAsync()
    {
        string? address = Environment.GetEnvironmentVariable("VISUALCRONAI_IT_DEV_ADDRESS");
        if (string.IsNullOrWhiteSpace(address)) return;
        await GuardNotLocal(address);

        var options = new VisualCronAIOptions
        {
            BridgePath = BridgeProtocolTests.BridgeExe,
            DefaultEnvironment = Env,
            Environments =
            {
                [Env] = new EnvironmentOptions
                {
                    Address = address,
                    Port = int.TryParse(Environment.GetEnvironmentVariable("VISUALCRONAI_IT_DEV_PORT"), out var p) ? p : 16444,
                    AuthMode = "AD",
                    AllowWrites = true,
                    IsProduction = false,
                    AdfRunnerPath = @"C:\VisualCronAI\adf-runner\VisualCronAI.AdfRunner.exe",
                    AdfEnvironment = Env,
                    DefaultJobGroup = "VCAI_IT",
                },
            },
        };
        var opts = Options.Create(options);
        var envs = new EnvironmentRegistry(opts);
        _gateway = new BridgeProcessGateway(opts, envs, NullLogger<BridgeProcessGateway>.Instance);
        _service = new ChangeService(_gateway, envs, new ProposalStore(), new DraftStore(), new MemoryAudit(), new WindowsCurrentUser(), new SystemClock(), NullLogger<ChangeService>.Instance);
    }

    private static async Task GuardNotLocal(string address)
    {
        if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase) || address.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to run integration tests against this machine's VisualCron (UAT/PROD).");
        var target = await Dns.GetHostAddressesAsync(address);
        var local = (await Dns.GetHostAddressesAsync(Dns.GetHostName())).Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback).ToHashSet();
        if (target.Any(a => IPAddress.IsLoopback(a) || local.Contains(a)))
            throw new InvalidOperationException($"Refusing to run integration tests: {address} resolves to this machine.");
    }

    private async Task<ChangeProposal> ApproveAndApply(ChangeProposal p)
    {
        Assert.True(p.Validation.IsValid, string.Join("; ", p.Validation.Errors));
        // Opting in via VISUALCRONAI_IT_DEV_ADDRESS is the human approval for these DEV-only test writes.
        await _service.RequestApprovalAsync(p.Id, [new FakeApproval(true)], default);
        var applied = await _service.ApplyAsync(p.Id, default);
        Assert.Equal(ProposalStatus.Applied, applied.Status);
        Assert.True(applied.Result!.Verified);
        if (p.Operation is ProposalOperation.Create or ProposalOperation.Clone) _created.Add(applied.Result.JobId);
        return applied;
    }

    [DevIntegrationFact]
    public async Task Connect_and_read_jobs()
    {
        var info = await _gateway.TestConnectionAsync(Env, default);
        Assert.True(info.Connected);
        var jobs = await _gateway.ListJobsAsync(Env, null, null, default);
        if (jobs.Count > 0)
        {
            var job = await _gateway.GetJobAsync(Env, jobs[0].Id, default);
            Assert.Equal(jobs[0].Id, job.Id);
            Assert.False(string.IsNullOrEmpty(job.VersionToken));
            await _gateway.GetJobHistoryAsync(new JobHistoryParams { Environment = Env, JobId = job.Id, FromUtc = DateTime.UtcNow.AddDays(-2), ToUtc = DateTime.UtcNow }, default);
        }
    }

    [DevIntegrationFact]
    public async Task Create_modify_clone_disable_roundtrip()
    {
        string name = BridgeMethods.TestJobPrefix + Guid.NewGuid().ToString("N")[..8];
        var draft = await _service.DraftJobAsync(new JobDraftInput
        {
            Environment = Env, Name = name, Active = false,
            Tasks = [new TaskInput { Type = "Execute", Name = "noop", CmdLine = @"C:\Windows\System32\cmd.exe", Arguments = "/c exit 0", RetryTimes = 1, RetryDelayMinutes = 1 }],
            Schedules = [new ScheduleInput { Mode = "Weekly", Times = ["03:30"], DaysOfWeek = ["Sunday"] }],
        }, default);
        var created = await ApproveAndApply(await _service.ProposeCreateAsync(draft.DraftId, default));

        var job = await _gateway.GetJobAsync(Env, created.Result!.JobId, default);
        Assert.Equal(name, job.Name);
        Assert.False(job.Active);
        Assert.Equal(["03:30"], job.Triggers.Single().Schedule!.Times);
        var onError = job.Tasks.Single().Flows.Single(f => f.EventType == FlowEvents.OnError);
        Assert.Equal(FlowActions.RetryTask, onError.ActionType);
        Assert.Equal(1, onError.RetryTimes);

        await ApproveAndApply(await _service.ProposeScheduleAsync(Env, job.Id, [new ScheduleInput { Mode = "Daily", Times = ["04:00"] }], null, true, default));
        await ApproveAndApply(await _service.ProposeAddTaskAsync(Env, job.Id, new TaskInput { Type = "Execute", Name = "second", CmdLine = @"C:\Windows\System32\cmd.exe", Arguments = "/c exit 0" }, null, default));
        job = await _gateway.GetJobAsync(Env, job.Id, default);
        Assert.Equal(2, job.Tasks.Count);
        Assert.Equal(["04:00"], job.Triggers.Single().Schedule!.Times);

        var clone = await ApproveAndApply(await _service.ProposeCloneAsync(new CloneInput { SourceEnvironment = Env, SourceJob = job.Id, TargetEnvironment = Env, NewName = name + "_clone" }, default));
        var cloned = await _gateway.GetJobAsync(Env, clone.Result!.JobId, default);
        Assert.NotEqual(job.Id, cloned.Id);
        Assert.Equal(2, cloned.Tasks.Count);
    }

    [DevIntegrationFact]
    public async Task Concurrent_change_is_detected_on_the_real_server()
    {
        string name = BridgeMethods.TestJobPrefix + Guid.NewGuid().ToString("N")[..8];
        var draft = await _service.DraftJobAsync(new JobDraftInput
        {
            Environment = Env, Name = name, Active = false,
            Tasks = [new TaskInput { Type = "Execute", Name = "noop", CmdLine = @"C:\Windows\System32\cmd.exe", Arguments = "/c exit 0" }],
        }, default);
        var created = await ApproveAndApply(await _service.ProposeCreateAsync(draft.DraftId, default));
        string id = created.Result!.JobId;

        var stale = await _service.ProposeJobChangesAsync(Env, id, new JobChangesInput { Description = "first" }, default);
        await ApproveAndApply(await _service.ProposeJobChangesAsync(Env, id, new JobChangesInput { Description = "second" }, default));

        await _service.RequestApprovalAsync(stale.Id, [new FakeApproval(true)], default);
        var ex = await Assert.ThrowsAsync<VisualCronAIException>(() => _service.ApplyAsync(stale.Id, default));
        Assert.Contains(BridgeErrorCodes.ConcurrencyConflict, ex.Message);
        Assert.Equal("second", (await _gateway.GetJobAsync(Env, id, default)).Description);
    }

    public async Task DisposeAsync()
    {
        if (_gateway == null) return;
        foreach (var id in _created)
        {
            try { await _gateway.DeleteTestJobAsync(Env, id, default); } catch { /* best effort */ }
        }
        await _gateway.DisposeAsync();
    }
}
