using System.Text.RegularExpressions;

namespace VisualCronAI.Core.Validation;

/// <summary>
/// Detects and masks credential-like material. Used twice:
///  1. validation rejects job definitions that embed secrets (they belong in Key Vault / VisualCron credentials),
///  2. every MCP tool response and audit entry passes through <see cref="Redact"/> as a last line of defence.
/// </summary>
public static partial class SecretScanner
{
    private sealed record Rule(string Name, Regex Pattern, int SecretGroup);

    private static readonly Rule[] Rules =
    [
        new("password assignment", PasswordAssign(), 2),
        new("client secret", ClientSecret(), 2),
        new("storage account key", AccountKey(), 1),
        new("SAS signature", SasSig(), 1),
        new("bearer token", Bearer(), 1),
        new("JWT", Jwt(), 0),
        new("private key block", PrivateKey(), 0),
        new("AWS access key", AwsKey(), 0),
        new("connection string password", ConnStringPassword(), 1),
        new("JSON token field", JsonTokenField(), 1),
        // Catch-all for token-like blobs (JWT signatures, partial tokens after truncation, keys): 100+ chars of base64url.
        new("long token-like string", LongTokenLike(), 0),
    ];

    [GeneratedRegex(@"(?i)""(?:access_token|refresh_token|id_token|client_secret|password|secret|token|sig|accountkey)""\s*:\s*""([^""]{8,})""")]
    private static partial Regex JsonTokenField();
    // No '/' so URL paths (/subscriptions/<guid>/resourceGroups/...) are not mistaken for tokens.
    [GeneratedRegex(@"[A-Za-z0-9_\-+=]{100,}")]
    private static partial Regex LongTokenLike();

    // In each pattern the capture group named in the Rule holds the secret value itself.
    // "token" only with '=' (so prose like "Get Access Token: HTTP POST" is not treated as a secret);
    // JSON "access_token":"..." is covered by JsonTokenField.
    [GeneratedRegex(@"(?i)\b(?:(?:password|passwd|pwd|secret|apikey|api_key|api-key)\s*[=:]|(?:access_token|token)\s*=)\s*(""?)([^\s;""']{3,})")]
    private static partial Regex PasswordAssign();
    [GeneratedRegex(@"(?i)\bclient[_-]?secret\b[""']?\s*[=:\s]\s*(""?)([^\s;""']{8,})")]
    private static partial Regex ClientSecret();
    [GeneratedRegex(@"(?i)AccountKey=([A-Za-z0-9+/=]{20,})")]
    private static partial Regex AccountKey();
    // Covers Azure Storage SAS and Logic App / Function trigger URLs (base64url signatures use '-' and '_').
    [GeneratedRegex(@"(?i)(?:[?&]|\\u0026)(?:sig|code)=([A-Za-z0-9%+/=_\-]{16,})")]
    private static partial Regex SasSig();
    [GeneratedRegex(@"(?i)\bBearer\s+([A-Za-z0-9\-_\.=]{20,})")]
    private static partial Regex Bearer();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")]
    private static partial Regex Jwt();
    // Only base64/whitespace (incl. JSON-escaped \n) is consumed so redaction can never swallow JSON delimiters.
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----(?:[A-Za-z0-9+/=\s]|\\[nr])*(?:-----END [A-Z ]*PRIVATE KEY-----)?")]
    private static partial Regex PrivateKey();
    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey();
    [GeneratedRegex(@"(?i)\b(?:Password|Pwd)\s*=\s*([^;""'\s\\]+)")]
    private static partial Regex ConnStringPassword();

    /// <summary>Names of the secret patterns found in <paramref name="text"/> (empty when clean).</summary>
    public static List<string> Scan(string? text)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text)) return found;
        foreach (var rule in Rules)
        {
            foreach (Match m in rule.Pattern.Matches(text))
            {
                string value = rule.SecretGroup == 0 ? m.Value : m.Groups[rule.SecretGroup].Value;
                if (IsPlaceholder(value)) continue;
                found.Add(rule.Name);
                break;
            }
        }
        return found;
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string result = text;
        foreach (var rule in Rules)
        {
            result = rule.Pattern.Replace(result, m =>
            {
                var g = rule.SecretGroup == 0 ? m.Groups[0] : m.Groups[rule.SecretGroup];
                if (!g.Success || g.Length == 0 || IsPlaceholder(g.Value)) return m.Value;
                int start = g.Index - m.Index;
                return m.Value[..start] + "***REDACTED***" + m.Value[(start + g.Length)..];
            });
        }
        return result;
    }

    /// <summary>VisualCron variables ({CREDENTIAL(...)}), Key Vault references and obvious placeholders are not secrets.</summary>
    private static bool IsPlaceholder(string value)
    {
        string v = value.Trim('"', '\'');
        return v.StartsWith('{') || v.StartsWith("$(") || v.StartsWith("%") || v.StartsWith("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase)
               || v.Equals("***REDACTED***", StringComparison.Ordinal) || v.Length == 0;
    }
}
