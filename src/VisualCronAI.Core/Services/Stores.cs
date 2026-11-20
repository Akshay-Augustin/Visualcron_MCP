using System.Collections.Concurrent;
using System.Security.Cryptography;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Core.Services;

/// <summary>
/// In-memory proposal store. Proposals deliberately do not survive a server restart: an approval must always be
/// given in the same session that prepared and previewed the change.
/// </summary>
public sealed class ProposalStore
{
    private readonly ConcurrentDictionary<string, ChangeProposal> _items = new(StringComparer.Ordinal);

    public void Add(ChangeProposal p) => _items[p.Id] = p;

    public ChangeProposal? Get(string id) => _items.TryGetValue(id ?? "", out var p) ? p : null;

    public IReadOnlyList<ChangeProposal> All() => _items.Values.OrderByDescending(p => p.CreatedAt).ToList();

    public static string NewId(string prefix) => prefix + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}

public sealed class JobDraft
{
    public string Id { get; init; } = "";
    public string Environment { get; init; } = "";
    public JobDto Job { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class DraftStore
{
    private readonly ConcurrentDictionary<string, JobDraft> _items = new(StringComparer.Ordinal);
    public void Add(JobDraft d) => _items[d.Id] = d;
    public JobDraft? Get(string id) => _items.TryGetValue(id ?? "", out var d) ? d : null;
}
