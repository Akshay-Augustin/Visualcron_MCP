using System.Text.RegularExpressions;

namespace VisualCronAI.Core.Configuration;

/// <summary>Root configuration (appsettings.json section "VisualCronAI"). Contains no secrets by design.</summary>
public sealed class VisualCronAIOptions
{
    public const string SectionName = "VisualCronAI";

    /// <summary>Path to VisualCronAI.Bridge.exe. Empty = "bridge\VisualCronAI.Bridge.exe" next to the MCP server.</summary>
    public string BridgePath { get; set; } = "";
    public string VisualCronInstallDir { get; set; } = @"C:\Program Files (x86)\VisualCron";
    public int BridgeRequestTimeoutSeconds { get; set; } = 120;
    public string DefaultEnvironment { get; set; } = "DEV";
    public Dictionary<string, EnvironmentOptions> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ApprovalOptions Approval { get; set; } = new();
    public AuditOptions Audit { get; set; } = new();
}

public sealed class EnvironmentOptions
{
    /// <summary>VisualCron server host name.</summary>
    public string Address { get; set; } = "";
    public int Port { get; set; } = 16444;
    /// <summary>"AD" (Windows logon of the account running the MCP server — recommended) or "CredentialManager".</summary>
    public string AuthMode { get; set; } = "AD";
    /// <summary>Windows Credential Manager generic target, only for AuthMode=CredentialManager. The password never leaves the bridge.</summary>
    public string CredentialTarget { get; set; } = "";
    public bool IsProduction { get; set; }
    /// <summary>Master switch for any write. Default false: environments are read-only until explicitly enabled.</summary>
    public bool AllowWrites { get; set; }
    /// <summary>Job groups that belong to this environment (patterns, e.g. "DEV:*"). When several environments share one
    /// VisualCron server, this scopes reads and lookups to the environment's groups. Empty = the whole server.</summary>
    public List<string> JobGroups { get; set; } = [];
    /// <summary>Least privilege: when non-empty, writes are only allowed to jobs in these job groups (patterns, e.g. "DEV:*").</summary>
    public List<string> WritableJobGroups { get; set; } = [];
    /// <summary>Full path of VisualCronAI.AdfRunner.exe on that VisualCron server.</summary>
    public string AdfRunnerPath { get; set; } = "";
    /// <summary>Environment key in adf-runner.json on that server. Empty = the environment name.</summary>
    public string AdfEnvironment { get; set; } = "";
    public string DefaultJobGroup { get; set; } = "";
    /// <summary>Strings that identify this environment inside names/arguments (e.g. "DEV", "_dev"). Used by clone and compare.
    /// Tokens are matched positionally across environments: DEV.Tokens[i] ↔ UAT.Tokens[i].</summary>
    public List<string> EnvironmentTokens { get; set; } = [];
    public int ConnectTimeoutSeconds { get; set; } = 30;

    // ---- ADF via native HTTP tasks (the team's existing "Trigger ADF" job pattern)
    /// <summary>Azure subscription of this environment's Data Factory (used to retarget createRun/pipelineruns URLs).</summary>
    public string AdfSubscriptionId { get; set; } = "";
    public string AdfResourceGroup { get; set; } = "";
    public string AdfFactoryName { get; set; } = "";
    /// <summary>Existing job used as the template for new ADF jobs (e.g. "Products - Trigger ADF"). Its token request,
    /// job variables (tenant/client id/secret) and task chain are copied by VisualCron itself.</summary>
    public string AdfTemplateJob { get; set; } = "";
    public int AdfPollMinutes { get; set; } = 1;
    public int AdfMaxRunMinutes { get; set; } = 180;
}

public sealed class ApprovalOptions
{
    /// <summary>Auto (MCP elicitation if the client supports it, otherwise Windows dialog), Elicitation, or WindowsDialog.</summary>
    public string Mode { get; set; } = "Auto";
    public int ProposalTtlMinutes { get; set; } = 60;
    public int ApprovalTtlMinutes { get; set; } = 15;
    public int DialogTimeoutSeconds { get; set; } = 300;
    /// <summary>Windows accounts (DOMAIN\user) allowed to approve production changes. Empty = nobody.</summary>
    public List<string> ProductionApprovers { get; set; } = [];
}

public sealed class AuditOptions
{
    /// <summary>Empty = %LOCALAPPDATA%\VisualCronAI\audit.</summary>
    public string Directory { get; set; } = "";
}

public static partial class EnvironmentNames
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-]{0,31}$")]
    public static partial Regex Valid();
}
