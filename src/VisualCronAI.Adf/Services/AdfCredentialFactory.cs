using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using VisualCronAI.Adf.Models;

namespace VisualCronAI.Adf.Services;

/// <summary>
/// Builds the credential that runs ADF pipelines: a service principal whose client secret is fetched from
/// Azure Key Vault at run time. The secret is held only in process memory; it is never written to disk,
/// logged, or stored in the VisualCron job definition.
/// </summary>
public static class AdfCredentialFactory
{
    public static async Task<TokenCredential> CreateAsync(AdfEnvironmentOptions o, CancellationToken ct)
    {
        TokenCredential vaultAccess = CreateVaultAccessCredential(o);
        var secrets = new SecretClient(new Uri(o.KeyVaultUri), vaultAccess);
        KeyVaultSecret secret;
        try
        {
            secret = (await secrets.GetSecretAsync(o.ClientSecretName, cancellationToken: ct)).Value;
        }
        catch (Azure.RequestFailedException ex)
        {
            throw new AdfAuthenticationException($"Could not read secret '{o.ClientSecretName}' from {o.KeyVaultUri}: {ex.Status} {ex.ErrorCode}.");
        }
        catch (AuthenticationFailedException ex)
        {
            throw new AdfAuthenticationException($"Could not authenticate to Key Vault using {o.KeyVaultAccess}: {FirstLine(ex.Message)}");
        }
        if (string.IsNullOrEmpty(secret.Value))
            throw new AdfAuthenticationException($"Key Vault secret '{o.ClientSecretName}' is empty.");
        return new ClientSecretCredential(o.TenantId, o.ClientId, secret.Value);
    }

    public static TokenCredential CreateVaultAccessCredential(AdfEnvironmentOptions o) => o.KeyVaultAccess switch
    {
        KeyVaultAccessMode.ManagedIdentity => string.IsNullOrWhiteSpace(o.ManagedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(o.ManagedIdentityClientId)),
        KeyVaultAccessMode.AzureCli => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = o.TenantId }),
        KeyVaultAccessMode.ClientCertificate => new ClientCertificateCredential(o.TenantId, o.KeyVaultReaderClientId, FindCertificate(o.KeyVaultReaderCertificateThumbprint)),
        _ => new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = o.TenantId }),
    };

    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", ""), validOnly: false);
            if (found.Count > 0) return found[0];
        }
        throw new AdfAuthenticationException($"Certificate with thumbprint {thumbprint} not found in LocalMachine\\My or CurrentUser\\My.");
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
}

public sealed class AdfAuthenticationException(string message) : Exception(message);
