using System.Text.RegularExpressions;

namespace VisualCronAI.Core.Configuration;

/// <summary>
/// Job-group patterns, e.g. "DEV:*" or "DEV: ADF-D365 Loads". '*' matches any characters; matching is case-insensitive
/// and ignores surrounding whitespace. Used when several environments (DEV/UAT/PROD) live as job groups on one server.
/// </summary>
public static class GroupPattern
{
    public static bool Matches(string pattern, string? group)
    {
        string g = (group ?? "").Trim();
        string p = (pattern ?? "").Trim();
        if (p.Length == 0) return false;
        if (!p.Contains('*')) return string.Equals(p, g, StringComparison.OrdinalIgnoreCase);
        string regex = "^" + string.Join(".*", p.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(g, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>True when <paramref name="patterns"/> is empty (no restriction) or any pattern matches.</summary>
    public static bool MatchesAny(IReadOnlyCollection<string>? patterns, string? group) =>
        patterns == null || patterns.Count == 0 || patterns.Any(p => Matches(p, group));
}
