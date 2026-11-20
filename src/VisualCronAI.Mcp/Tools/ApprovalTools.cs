using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Diff;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Models;
using VisualCronAI.Core.Services;
using VisualCronAI.Infrastructure.Approval;
using VisualCronAI.Mcp.Approval;

namespace VisualCronAI.Mcp.Tools;

[McpServerToolType]
public sealed class ApprovalTools(ChangeService changes, EnvironmentRegistry environments, WindowsDialogApprovalPrompt dialog)
{
    [McpServerTool(Name = "visualcron_validate_job", ReadOnly = true)]
    [Description("Validates an existing job, a draft (draftId) or a proposal (proposalId): naming, tasks, ADF arguments and target environment, schedules, dependencies, notification/credential references, embedded secrets. Also returns the next run times.")]
    public Task<string> ValidateJob(string? job = null, string? environment = null, string? draftId = null, string? proposalId = null, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            if (!string.IsNullOrWhiteSpace(proposalId))
            {
                var p = changes.GetProposal(proposalId);
                return new { proposalId, valid = p.Validation.IsValid, issues = p.Validation.Issues.Select(i => i.ToString()), nextRuns = p.SchedulePreview?.NextExecutionsServerTime };
            }
            var (report, preview) = !string.IsNullOrWhiteSpace(draftId)
                ? await changes.ValidateDraftAsync(draftId, ct)
                : await changes.ValidateExistingAsync(environment ?? "", job ?? throw new VisualCronAIException("Provide job, draftId or proposalId."), ct);
            return new
            {
                valid = report.IsValid,
                errors = report.Errors.Select(i => i.ToString()),
                warnings = report.Warnings.Select(i => i.ToString()),
                nextRuns = preview == null ? null : new { preview.ServerTimeZone, times = preview.NextExecutionsServerTime.Select(d => d.ToString("ddd yyyy-MM-dd HH:mm")) },
            };
        });

    [McpServerTool(Name = "visualcron_compare_jobs", ReadOnly = true)]
    [Description("Compares two jobs (possibly in different environments, e.g. DEV vs UAT) and lists differences in job settings, tasks, ADF pipelines and parameters, schedules, dependencies, error handling and notifications. With normalizeEnvironmentTokens, expected environment-name differences (configured tokens) are ignored.")]
    public Task<string> CompareJobs(string leftJob, string rightJob, string? leftEnvironment = null, string? rightEnvironment = null,
        bool normalizeEnvironmentTokens = true, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            string le = environments.Resolve(leftEnvironment), re = environments.Resolve(rightEnvironment);
            var l = await changes.ResolveJobAsync(le, leftJob, ct);
            var r = await changes.ResolveJobAsync(re, rightJob, ct);
            var opts = new CompareOptions
            {
                NormalizeEnvironmentTokens = normalizeEnvironmentTokens && le != re,
                LeftTokens = environments.Get(le).EnvironmentTokens,
                RightTokens = environments.Get(re).EnvironmentTokens,
            };
            var diffs = JobComparer.Compare(l, r, opts);
            return new
            {
                left = new { environment = le, l.Id, l.Name },
                right = new { environment = re, r.Id, r.Name },
                identical = diffs.Count == 0,
                differenceCount = diffs.Count,
                differences = ToolResult.DiffView(diffs),
                note = opts.NormalizeEnvironmentTokens ? "Environment tokens were normalized; token-only differences are not listed." : null,
            };
        });

    [McpServerTool(Name = "visualcron_preview_changes", ReadOnly = true, Idempotent = true)]
    [Description("Full preview of a proposal: before/after job definitions, all changes, validation, next run times and approval status.")]
    public Task<string> PreviewChanges(string proposalId) =>
        ToolResult.Run(() => Task.FromResult(ToolResult.ProposalView(changes.GetProposal(proposalId), full: true)));

    [McpServerTool(Name = "visualcron_request_approval")]
    [Description("Asks the HUMAN user to approve a proposal through an approval form in their MCP client (or a Windows dialog). Blocks until they decide. You cannot answer this prompt for the user. Production changes additionally require a typed confirmation and an authorized approver.")]
    public Task<string> RequestApproval(McpServer server, string proposalId, CancellationToken ct = default) =>
        ToolResult.Run(async () =>
        {
            IReadOnlyList<IApprovalPrompt> channels = [new ElicitationApprovalPrompt(server), dialog];
            var p = await changes.RequestApprovalAsync(proposalId, channels, ct);
            return ToolResult.ProposalView(p);
        });

    [McpServerTool(Name = "visualcron_apply_approved_changes", Destructive = true)]
    [Description("Applies a proposal that the human approved: re-checks approval, environment write permission and that the job has not changed since (optimistic concurrency), writes to VisualCron, verifies by reading the job back, and rolls back on failure. Every step is audited.")]
    public Task<string> ApplyApprovedChanges(string proposalId, CancellationToken ct = default) =>
        ToolResult.Run(async () => ToolResult.ProposalView(await changes.ApplyAsync(proposalId, ct)));
}
