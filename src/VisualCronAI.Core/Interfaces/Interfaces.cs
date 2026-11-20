using VisualCronAI.Contracts;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Core.Interfaces;

/// <summary>
/// Access to real VisualCron servers. The production implementation talks to the .NET Framework bridge process,
/// which uses the official VisualCronAPI.dll. Environments are connected on first use from configuration.
/// </summary>
public interface IVisualCronGateway
{
    Task<HelloResult> GetBridgeInfoAsync(CancellationToken ct);
    Task<ServerInfoDto> ConnectAsync(string environment, CancellationToken ct);
    Task<ServerInfoDto> TestConnectionAsync(string environment, CancellationToken ct);
    Task<IReadOnlyList<JobSummaryDto>> ListJobsAsync(string environment, string? nameContains, string? group, CancellationToken ct);
    Task<JobDto> GetJobAsync(string environment, string jobId, CancellationToken ct);
    Task<IReadOnlyList<JobHistoryEntryDto>> GetJobHistoryAsync(JobHistoryParams p, CancellationToken ct);
    Task<IReadOnlyList<TaskLogEntryDto>> GetJobLogsAsync(JobLogsParams p, CancellationToken ct);
    Task<IReadOnlyList<NamedRefDto>> ListNotificationsAsync(string environment, CancellationToken ct);
    Task<IReadOnlyList<NamedRefDto>> ListCredentialsAsync(string environment, CancellationToken ct);
    Task<SchedulePreviewDto> PreviewScheduleAsync(string environment, JobDto job, int count, CancellationToken ct);
    Task<WriteResultDto> CreateJobAsync(string environment, JobDto job, CancellationToken ct);
    Task<WriteResultDto> UpdateJobAsync(string environment, JobDto job, string expectedVersionToken, CancellationToken ct);
    Task<WriteResultDto> CloneJobAsync(CloneJobParams p, CancellationToken ct);
    Task<WriteResultDto> SetJobActiveAsync(string environment, string jobId, bool active, string expectedVersionToken, CancellationToken ct);
}

/// <summary>Error from the gateway / bridge with a stable code from <see cref="BridgeErrorCodes"/>.</summary>
public sealed class GatewayException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A channel that asks the *human* to approve a change (MCP elicitation, Windows dialog...).
/// The AI model cannot answer these prompts.</summary>
public interface IApprovalPrompt
{
    string Channel { get; }
    bool IsAvailable { get; }
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

public interface IAuditLog
{
    Task WriteAsync(AuditEvent e, CancellationToken ct);
}

public interface ICurrentUser
{
    /// <summary>DOMAIN\user of the Windows account running the MCP server.</summary>
    string Name { get; }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
