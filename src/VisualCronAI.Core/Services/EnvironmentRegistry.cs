using Microsoft.Extensions.Options;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Core.Services;

/// <summary>Resolves and validates environment names against configuration (untrusted input → known environment).</summary>
public sealed class EnvironmentRegistry(IOptions<VisualCronAIOptions> options)
{
    private readonly VisualCronAIOptions _options = options.Value;

    public IReadOnlyCollection<string> Names => _options.Environments.Keys;

    public string Resolve(string? name)
    {
        string n = string.IsNullOrWhiteSpace(name) ? _options.DefaultEnvironment : name.Trim();
        if (!EnvironmentNames.Valid().IsMatch(n)) throw new VisualCronAIException($"Invalid environment name '{n}'.");
        foreach (var key in _options.Environments.Keys)
            if (string.Equals(key, n, StringComparison.OrdinalIgnoreCase)) return key;
        throw new VisualCronAIException($"Environment '{n}' is not configured. Configured: {string.Join(", ", _options.Environments.Keys)}.");
    }

    public EnvironmentOptions Get(string name) => _options.Environments[Resolve(name)];

    public ApprovalOptions Approval => _options.Approval;
}
