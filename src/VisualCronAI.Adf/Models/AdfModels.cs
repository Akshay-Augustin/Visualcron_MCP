namespace VisualCronAI.Adf.Models;

/// <summary>How the runner authenticates to Azure Key Vault to fetch the service principal's client secret.</summary>
public enum KeyVaultAccessMode
{
    /// <summary>Managed identity of the VisualCron server (Azure VM / Arc-enabled server).</summary>
    ManagedIdentity,
    /// <summary>Azure CLI login of the Windows account running the VisualCron task (az login once as the service account).</summary>
    AzureCli,
    /// <summary>Certificate in the Windows certificate store (no secret on disk) for a separate "vault reader" app registration.</summary>
    ClientCertificate,
    /// <summary>Azure.Identity DefaultAzureCredential chain.</summary>
    Default,
}

/// <summary>Per-environment ADF settings. Contains no secrets: the SP secret lives in Key Vault.</summary>
public sealed class AdfEnvironmentOptions
{
    public string TenantId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string FactoryName { get; set; } = "";
    /// <summary>Application (client) ID of the service principal that runs pipelines (needs Data Factory Contributor on the factory).</summary>
    public string ClientId { get; set; } = "";
    public string KeyVaultUri { get; set; } = "";
    /// <summary>Name of the Key Vault secret holding the service principal's client secret.</summary>
    public string ClientSecretName { get; set; } = "";
    public KeyVaultAccessMode KeyVaultAccess { get; set; } = KeyVaultAccessMode.ManagedIdentity;
    /// <summary>For ManagedIdentity: optional user-assigned identity client ID.</summary>
    public string ManagedIdentityClientId { get; set; } = "";
    /// <summary>For ClientCertificate: app registration used only to read the vault.</summary>
    public string KeyVaultReaderClientId { get; set; } = "";
    /// <summary>For ClientCertificate: thumbprint in LocalMachine\My or CurrentUser\My.</summary>
    public string KeyVaultReaderCertificateThumbprint { get; set; } = "";
    public string ManagementEndpoint { get; set; } = "https://management.azure.com";
    public string ApiVersion { get; set; } = "2018-06-01";

    public List<string> Validate(string envName)
    {
        var e = new List<string>();
        void Req(string v, string n) { if (string.IsNullOrWhiteSpace(v)) e.Add($"{envName}: {n} is required."); }
        Req(TenantId, nameof(TenantId));
        Req(SubscriptionId, nameof(SubscriptionId));
        Req(ResourceGroup, nameof(ResourceGroup));
        Req(FactoryName, nameof(FactoryName));
        Req(ClientId, nameof(ClientId));
        Req(KeyVaultUri, nameof(KeyVaultUri));
        Req(ClientSecretName, nameof(ClientSecretName));
        if (!string.IsNullOrWhiteSpace(KeyVaultUri) && !KeyVaultUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            e.Add($"{envName}: KeyVaultUri must be https.");
        if (KeyVaultAccess == KeyVaultAccessMode.ClientCertificate)
        {
            Req(KeyVaultReaderClientId, nameof(KeyVaultReaderClientId));
            Req(KeyVaultReaderCertificateThumbprint, nameof(KeyVaultReaderCertificateThumbprint));
        }
        return e;
    }
}

public sealed class AdfRunnerConfig
{
    public Dictionary<string, AdfEnvironmentOptions> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class PipelineRunStatus
{
    public const string Queued = "Queued";
    public const string InProgress = "InProgress";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Canceling = "Canceling";
    public const string Cancelled = "Cancelled";

    public static bool IsTerminal(string s) => s is Succeeded or Failed or Cancelled;
}

public sealed class PipelineRunInfo
{
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset? RunStart { get; set; }
    public DateTimeOffset? RunEnd { get; set; }
    public long? DurationInMs { get; set; }
}

public sealed class ActivityRunInfo
{
    public string ActivityName { get; set; } = "";
    public string ActivityType { get; set; } = "";
    public string Status { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
}

/// <summary>Process exit codes of VisualCronAI.AdfRunner.exe. VisualCron treats any non-zero code as task failure.</summary>
public static class RunnerExitCodes
{
    public const int Succeeded = 0;
    public const int PipelineFailed = 1;
    public const int PipelineCancelled = 2;
    public const int TimedOut = 3;
    public const int ConfigurationError = 4;
    public const int AuthenticationError = 5;
    public const int AzureApiError = 6;
}
