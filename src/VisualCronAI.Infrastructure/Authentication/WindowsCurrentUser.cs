using VisualCronAI.Core.Interfaces;

namespace VisualCronAI.Infrastructure.Authentication;

/// <summary>
/// The Windows identity running the MCP server (and therefore the bridge). With AuthMode=AD this identity is what
/// VisualCron authorizes, so least privilege is configured in VisualCron's user/group permissions for this account
/// (or for a dedicated service account the MCP server is started as).
/// </summary>
public sealed class WindowsCurrentUser : ICurrentUser
{
    public string Name { get; } = string.IsNullOrEmpty(Environment.UserDomainName)
        ? Environment.UserName
        : Environment.UserDomainName + "\\" + Environment.UserName;
}
