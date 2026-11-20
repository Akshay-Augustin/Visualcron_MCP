using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Models;
using VisualCronAI.Core.Services;

namespace VisualCronAI.Tests;

/// <summary>In-memory VisualCron "server" per environment. Records every write so tests can assert nothing was written.</summary>
public sealed class FakeGateway : IVisualCronGateway
{
    public Dictionary<string, Dictionary<string, JobDto>> Jobs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<NamedRefDto>> Notifications { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TimeZones { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes { get; } = [];
    public string? FailNextWriteWith { get; set; }

    public FakeGateway(params string[] environments)
    {
        foreach (var e in environments)
        {
            Jobs[e] = new Dictionary<string, JobDto>();
            Notifications[e] = [];
            TimeZones[e] = "Central Standard Time";
        }
    }

    /// <summary>Makes the given environments one VisualCron server (shared job and notification stores).</summary>
    public void ShareServer(params string[] environments)
    {
        foreach (var e in environments.Skip(1))
        {
            Jobs[e] = Jobs[environments[0]];
            Notifications[e] = Notifications[environments[0]];
        }
    }

    public JobDto Add(string env, JobDto job)
    {
        if (string.IsNullOrEmpty(job.Id)) job.Id = Guid.NewGuid().ToString();
        foreach (var t in job.Tasks) if (string.IsNullOrEmpty(t.Id) || t.Id.StartsWith("draft")) t.Id = Guid.NewGuid().ToString();
        foreach (var t in job.Triggers) if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString();
        job.Stats = new JobStatsDto { DateModified = DateTime.UtcNow };
        job.VersionToken = "v-" + Guid.NewGuid().ToString("N")[..8];
        Jobs[env][job.Id] = job;
        return job;
    }

    private static JobDto Copy(JobDto j) => JsonSerializer.Deserialize<JobDto>(JsonSerializer.Serialize(j))!;

    public Task<HelloResult> GetBridgeInfoAsync(CancellationToken ct) => Task.FromResult(new HelloResult { VisualCronApiVersion = "12.1.5.0" });

    public Task<ServerInfoDto> ConnectAsync(string environment, CancellationToken ct) => Task.FromResult(new ServerInfoDto
    {
        Environment = environment, Connected = true, TimeZoneName = TimeZones[environment], UtcOffsetMinutes = -360, ServerTime = new DateTime(2026, 11, 19, 10, 0, 0),
    });

    public Task<ServerInfoDto> TestConnectionAsync(string environment, CancellationToken ct) => ConnectAsync(environment, ct);

    public Task<IReadOnlyList<JobSummaryDto>> ListJobsAsync(string environment, string? nameContains, string? group, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JobSummaryDto>>(Jobs[environment].Values
            .Where(j => nameContains == null || j.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .Select(j => new JobSummaryDto { Id = j.Id, Name = j.Name, Group = j.Group, Active = j.Active }).ToList());

    public Task<JobDto> GetJobAsync(string environment, string jobId, CancellationToken ct) =>
        Jobs[environment].TryGetValue(jobId, out var j) ? Task.FromResult(Copy(j)) : throw new GatewayException(BridgeErrorCodes.NotFound, "not found");

    public Task<IReadOnlyList<JobHistoryEntryDto>> GetJobHistoryAsync(JobHistoryParams p, CancellationToken ct) => Task.FromResult<IReadOnlyList<JobHistoryEntryDto>>([]);

    public Task<IReadOnlyList<TaskLogEntryDto>> GetJobLogsAsync(JobLogsParams p, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskLogEntryDto>>(
    [
        new TaskLogEntryDto { TaskName = "t", StandardOutput = "connecting with password=Hunter2!! and Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop" },
    ]);

    public Task<IReadOnlyList<NamedRefDto>> ListNotificationsAsync(string environment, CancellationToken ct) => Task.FromResult<IReadOnlyList<NamedRefDto>>(Notifications[environment]);
    public Task<IReadOnlyList<NamedRefDto>> ListCredentialsAsync(string environment, CancellationToken ct) => Task.FromResult<IReadOnlyList<NamedRefDto>>([]);

    public Task<SchedulePreviewDto> PreviewScheduleAsync(string environment, JobDto job, int count, CancellationToken ct) =>
        Task.FromResult(new SchedulePreviewDto { ServerTimeZone = TimeZones[environment], NextExecutionsServerTime = [new DateTime(2026, 11, 20, 2, 0, 0)] });

    private void MaybeFail()
    {
        if (FailNextWriteWith is { } code) { FailNextWriteWith = null; throw new GatewayException(code, "simulated " + code); }
    }

    public Task<WriteResultDto> CreateJobAsync(string environment, JobDto job, CancellationToken ct)
    {
        MaybeFail();
        Writes.Add($"create:{environment}:{job.Name}");
        return Task.FromResult(new WriteResultDto { Job = Add(environment, Copy(job)), Verified = true });
    }

    public Task<WriteResultDto> UpdateJobAsync(string environment, JobDto job, string expectedVersionToken, CancellationToken ct)
    {
        MaybeFail();
        var current = Jobs[environment][job.Id];
        if (current.VersionToken != expectedVersionToken) throw new GatewayException(BridgeErrorCodes.ConcurrencyConflict, "changed");
        Writes.Add($"update:{environment}:{job.Name}");
        return Task.FromResult(new WriteResultDto { Job = Add(environment, Copy(job)), Verified = true });
    }

    public Task<WriteResultDto> CloneJobAsync(CloneJobParams p, CancellationToken ct)
    {
        MaybeFail();
        Writes.Add($"clone:{p.SourceEnvironment}->{p.TargetEnvironment}:{p.Overrides.Name}");
        var j = Copy(p.Overrides); j.Id = "";
        return Task.FromResult(new WriteResultDto { Job = Add(p.TargetEnvironment, j), Verified = true });
    }

    public Task<WriteResultDto> SetJobActiveAsync(string environment, string jobId, bool active, string expectedVersionToken, CancellationToken ct)
    {
        MaybeFail();
        Writes.Add($"active:{environment}:{jobId}:{active}");
        var j = Jobs[environment][jobId]; j.Active = active;
        return Task.FromResult(new WriteResultDto { Job = j, Verified = true });
    }
}

public sealed class FakeApproval(bool approve, string? confirmation = null, bool available = true, string channel = "Elicitation") : IApprovalPrompt
{
    public int Calls { get; private set; }
    public ApprovalRequest? LastRequest { get; private set; }
    public string Channel => channel;
    public bool IsAvailable => available;
    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Calls++;
        LastRequest = request;
        return Task.FromResult(new ApprovalDecision { Approved = approve, Channel = Channel, ConfirmationText = confirmation, Reason = approve ? "ok" : "no" });
    }
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 19, 16, 0, 0, TimeSpan.Zero);
}

public sealed class MemoryAudit : IAuditLog
{
    public List<AuditEvent> Events { get; } = [];
    public Task WriteAsync(AuditEvent e, CancellationToken ct) { Events.Add(e); return Task.CompletedTask; }
}

public sealed class FixedUser(string name) : ICurrentUser
{
    public string Name { get; } = name;
}

public sealed class TestHost
{
    public FakeGateway Gateway { get; }
    public VisualCronAIOptions Options { get; }
    public FakeClock Clock { get; } = new();
    public MemoryAudit Audit { get; } = new();
    public ChangeService Service { get; }
    public EnvironmentRegistry Environments { get; }

    public TestHost(string user = @"CORP\alice", Action<VisualCronAIOptions>? configure = null)
    {
        Options = new VisualCronAIOptions
        {
            DefaultEnvironment = "DEV",
            Environments =
            {
                ["DEV"] = new EnvironmentOptions { Address = "vc-dev", AllowWrites = true, AdfRunnerPath = @"C:\VisualCronAI\adf-runner\VisualCronAI.AdfRunner.exe", AdfEnvironment = "DEV", DefaultJobGroup = "ADF", EnvironmentTokens = ["DEV", "_dev"] },
                ["UAT"] = new EnvironmentOptions { Address = "vc-uat", AllowWrites = true, AdfRunnerPath = @"D:\Tools\VisualCronAI.AdfRunner.exe", AdfEnvironment = "UAT", DefaultJobGroup = "ADF", EnvironmentTokens = ["UAT", "_uat"] },
                ["PROD"] = new EnvironmentOptions { Address = "vc-prod", AllowWrites = true, IsProduction = true, AdfRunnerPath = @"D:\Tools\VisualCronAI.AdfRunner.exe", AdfEnvironment = "PROD", DefaultJobGroup = "ADF", EnvironmentTokens = ["PROD", "_prod"] },
            },
            Approval = new ApprovalOptions { ProductionApprovers = [@"CORP\prodlead"] },
        };
        configure?.Invoke(Options);
        Gateway = new FakeGateway("DEV", "UAT", "PROD");
        Environments = new EnvironmentRegistry(Microsoft.Extensions.Options.Options.Create(Options));
        Service = new ChangeService(Gateway, Environments, new ProposalStore(), new DraftStore(), Audit, new FixedUser(user), Clock, NullLogger<ChangeService>.Instance);
    }

    public static JobDraftInput D365Draft(string env = "DEV") => new()
    {
        Environment = env,
        Name = "D365_Product_Daily_Load",
        Tasks = [new TaskInput { Type = "AdfPipeline", PipelineName = "p_D365_Product_Load" }],
        Schedules = [new ScheduleInput { Mode = "Daily", Times = ["02:00"], TimeZone = "Central Standard Time" }],
    };
}
