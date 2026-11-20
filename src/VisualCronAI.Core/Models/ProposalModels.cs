using System.Text.Json.Serialization;
using VisualCronAI.Contracts;

namespace VisualCronAI.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProposalOperation { Create, Update, Clone, Enable, Disable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProposalStatus
{
    /// <summary>Validation errors; cannot be approved.</summary>
    Blocked,
    PendingApproval,
    Approved,
    Rejected,
    Applied,
    Failed,
    Expired,
}

/// <summary>A prepared, immutable change. Nothing is written to VisualCron until it is approved by a human and applied.</summary>
public sealed class ChangeProposal
{
    public string Id { get; init; } = "";
    public ProposalOperation Operation { get; init; }
    public string Environment { get; init; } = "";
    public bool IsProduction { get; init; }
    public string JobName { get; init; } = "";
    /// <summary>Target job for Update/Enable/Disable.</summary>
    public string? TargetJobId { get; init; }
    /// <summary>Version token of the target job when the proposal was prepared (optimistic concurrency).</summary>
    public string? BaseVersionToken { get; init; }
    public string? SourceEnvironment { get; init; }
    public string? SourceJobId { get; init; }
    public string? SourceVersionToken { get; init; }
    public JobDto? Before { get; init; }
    public JobDto After { get; init; } = new();
    public string Summary { get; init; } = "";
    public List<string> Changes { get; init; } = [];
    public ValidationReport Validation { get; init; } = new();
    public SchedulePreviewDto? SchedulePreview { get; init; }
    public List<string> Warnings { get; init; } = [];
    public string ContentHash { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string CreatedBy { get; init; } = "";

    public ProposalStatus Status { get; set; }
    public ApprovalRecord? Approval { get; set; }
    public ApplyRecord? Result { get; set; }
}

public sealed class ApprovalRecord
{
    public bool Approved { get; init; }
    public string Channel { get; init; } = "";
    public string DecidedBy { get; init; } = "";
    public DateTimeOffset DecidedAt { get; init; }
    public string ContentHash { get; init; } = "";
    public bool ProductionConfirmed { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class ApplyRecord
{
    public bool Success { get; init; }
    public DateTimeOffset AppliedAt { get; init; }
    public string AppliedBy { get; init; } = "";
    public string JobId { get; init; } = "";
    public bool Verified { get; init; }
    public string Message { get; init; } = "";
    public List<string> Notes { get; init; } = [];
}

public sealed class ApprovalRequest
{
    public string ProposalId { get; init; } = "";
    public string Environment { get; init; } = "";
    public bool IsProduction { get; init; }
    public string JobName { get; init; } = "";
    public ProposalOperation Operation { get; init; }
    public string Summary { get; init; } = "";
    /// <summary>Plain-text details shown to the human (changes, validation warnings, next runs).</summary>
    public string Details { get; init; } = "";
    /// <summary>For production: text the human must type to confirm, e.g. "PROD D365_Product_Daily_Load".</summary>
    public string? RequiredConfirmationText { get; init; }
}

public sealed class ApprovalDecision
{
    public bool Approved { get; init; }
    public string Channel { get; init; } = "";
    public string? ConfirmationText { get; init; }
    public string Reason { get; init; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IssueSeverity { Error, Warning }

public sealed class ValidationIssue
{
    public IssueSeverity Severity { get; init; }
    public string Path { get; init; } = "";
    public string Message { get; init; } = "";
    public override string ToString() => $"{Severity} {Path}: {Message}";
}

public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; init; } = [];
    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);

    public void Error(string path, string message) => Issues.Add(new ValidationIssue { Severity = IssueSeverity.Error, Path = path, Message = message });
    public void Warn(string path, string message) => Issues.Add(new ValidationIssue { Severity = IssueSeverity.Warning, Path = path, Message = message });
}

public sealed class AuditEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public string EventType { get; init; } = "";
    public string User { get; init; } = "";
    public string Environment { get; init; } = "";
    public string? ProposalId { get; init; }
    public string? JobId { get; init; }
    public string? JobName { get; init; }
    public string? Operation { get; init; }
    public string Outcome { get; init; } = "";
    public string? ContentHash { get; init; }
    public string? Details { get; init; }
}

/// <summary>Domain error surfaced to the MCP client as a tool error. Messages must never contain secrets.</summary>
public sealed class VisualCronAIException(string message) : Exception(message);
