using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Mcp.Approval;

/// <summary>
/// Human approval through MCP elicitation: the server sends an elicitation/create request and the MCP client
/// (Claude Code, VS Code) renders a form to the *user*. The model never sees or fills this form.
/// </summary>
public sealed class ElicitationApprovalPrompt(McpServer server) : IApprovalPrompt
{
    public string Channel => "Elicitation";

    public bool IsAvailable => server.ClientCapabilities?.Elicitation != null;

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
        {
            ["decision"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Title = "Decision",
                Description = "Approve to allow this exact change to be applied to VisualCron.",
                OneOf =
                [
                    new ElicitRequestParams.EnumSchemaOption { Const = "approve", Title = "Approve" },
                    new ElicitRequestParams.EnumSchemaOption { Const = "reject", Title = "Reject" },
                ],
            },
        };
        var required = new List<string> { "decision" };
        if (request.IsProduction)
        {
            properties["confirmation"] = new ElicitRequestParams.StringSchema
            {
                Title = "Production confirmation",
                Description = $"PRODUCTION change. Type exactly: {request.RequiredConfirmationText}",
            };
            required.Add("confirmation");
        }

        string header = request.IsProduction
            ? $"⚠ PRODUCTION CHANGE REQUEST ({request.Environment})\n"
            : $"VisualCron change request ({request.Environment})\n";
        var result = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = header + request.Details,
            RequestedSchema = new ElicitRequestParams.RequestSchema { Properties = properties, Required = required },
        }, ct);

        if (!string.Equals(result.Action, "accept", StringComparison.OrdinalIgnoreCase) || result.Content == null)
            return new ApprovalDecision { Approved = false, Channel = Channel, Reason = $"User {result.Action ?? "dismissed"} the approval request." };

        string? decision = Read(result.Content, "decision");
        string? confirmation = Read(result.Content, "confirmation");
        bool approved = string.Equals(decision, "approve", StringComparison.Ordinal);
        return new ApprovalDecision
        {
            Approved = approved,
            Channel = Channel,
            ConfirmationText = confirmation,
            Reason = approved ? "Approved via MCP elicitation." : "Rejected via MCP elicitation.",
        };
    }

    private static string? Read(IDictionary<string, JsonElement> content, string key) =>
        content.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
