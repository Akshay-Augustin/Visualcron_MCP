using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using VisualCronAI.Core.Diff;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Models;
using VisualCronAI.Core.Validation;

namespace VisualCronAI.Mcp.Tools;

/// <summary>
/// Every tool returns JSON produced here. The serialized text passes through <see cref="SecretScanner.Redact"/> as a
/// last line of defence so credential-like material can never reach the AI model, even if it exists on the server.
/// Domain errors become MCP tool errors with redacted messages.
/// </summary>
internal static class ToolResult
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(object value) => SecretScanner.Redact(JsonSerializer.Serialize(value, Json));

    public static async Task<string> Run(Func<Task<object>> action)
    {
        try
        {
            return Serialize(await action());
        }
        catch (VisualCronAIException ex)
        {
            throw new McpException(SecretScanner.Redact(ex.Message));
        }
        catch (GatewayException ex)
        {
            throw new McpException($"VisualCron error ({ex.Code}): {SecretScanner.Redact(ex.Message)}");
        }
    }

    /// <summary>Compact proposal view for the model; full detail via visualcron_preview_changes.</summary>
    public static object ProposalView(ChangeProposal p, bool full = false) => new
    {
        proposalId = p.Id,
        status = p.Status,
        operation = p.Operation,
        environment = p.Environment,
        isProduction = p.IsProduction,
        jobName = p.JobName,
        summary = p.Summary,
        changes = p.Changes,
        validation = new
        {
            valid = p.Validation.IsValid,
            errors = p.Validation.Errors.Select(e => e.ToString()).ToList(),
            warnings = p.Validation.Warnings.Select(e => e.ToString()).ToList(),
        },
        nextRuns = p.SchedulePreview == null ? null : new
        {
            serverTimeZone = p.SchedulePreview.ServerTimeZone,
            times = p.SchedulePreview.NextExecutionsServerTime.Select(d => d.ToString("ddd yyyy-MM-dd HH:mm")).ToList(),
            triggers = p.SchedulePreview.Triggers.Select(t => t.Description).ToList(),
        },
        notes = p.Warnings,
        approval = p.Approval == null ? null : new { p.Approval.Approved, p.Approval.Channel, p.Approval.DecidedBy, p.Approval.DecidedAt, p.Approval.Reason },
        result = p.Result,
        expiresAt = p.ExpiresAt,
        before = full ? p.Before : null,
        after = full ? p.After : null,
        nextStep = NextStep(p),
    };

    private static string NextStep(ChangeProposal p) => p.Status switch
    {
        ProposalStatus.Blocked => "Fix the validation errors and prepare a new proposal. This one cannot be approved.",
        ProposalStatus.PendingApproval => "Show the user the summary, changes, warnings and next runs. If they want to proceed, call visualcron_request_approval — the server will ask the human directly.",
        ProposalStatus.Approved => "Approved by the user. Call visualcron_apply_approved_changes to write it to VisualCron.",
        ProposalStatus.Rejected => "The user rejected this change. Do not apply it; ask what they want to change.",
        ProposalStatus.Applied => "Done. The job was written and verified.",
        ProposalStatus.Failed => "Apply failed; see result.message. Nothing was left half-applied (rollback attempted).",
        ProposalStatus.Expired => "Expired. Prepare the change again.",
        _ => "",
    };

    public static object DiffView(IEnumerable<DiffEntry> diffs) => diffs.GroupBy(d => d.Area).ToDictionary(g => g.Key, g => g.Select(d => d.ToString()).ToList());
}
