using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace VisualCronAI.Tests;

/// <summary>
/// Starts the real MCP server executable over STDIO with the official MCP C# client — the same way Claude Code and
/// VS Code start it — and checks tool discovery and error handling. Environments point at non-existent hosts, so no
/// VisualCron server is contacted.
/// </summary>
public sealed class McpServerEndToEndTests : IAsyncLifetime
{
    private McpClient _client = null!;
    private string _auditDir = "";

    public static readonly string[] RequiredTools =
    [
        "visualcron_test_connection", "visualcron_list_jobs", "visualcron_search_jobs", "visualcron_get_job",
        "visualcron_get_job_tasks", "visualcron_get_job_triggers", "visualcron_get_job_dependencies",
        "visualcron_get_job_history", "visualcron_get_job_logs",
        "visualcron_draft_job", "visualcron_clone_job", "visualcron_create_job", "visualcron_update_job",
        "visualcron_add_task", "visualcron_update_task", "visualcron_remove_task", "visualcron_update_schedule",
        "visualcron_enable_job", "visualcron_disable_job",
        "visualcron_validate_job", "visualcron_compare_jobs", "visualcron_preview_changes",
        "visualcron_request_approval", "visualcron_apply_approved_changes",
    ];

    public async Task InitializeAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "VisualCronAI.sln"))) root = root.Parent;
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        string exe = Path.Combine(root!.FullName, "src", "VisualCronAI.Mcp", "bin", config, "net10.0", "VisualCronAI.Mcp.exe");
        Assert.True(File.Exists(exe), "MCP server not built: " + exe);
        _auditDir = Path.Combine(Path.GetTempPath(), "vcai-e2e-" + Guid.NewGuid().ToString("N"));

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "visualcron-e2e",
            Command = exe,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                // Isolation: never load the developer's real per-user config (which points at a real server).
                ["VISUALCRONAI_USER_CONFIG"] = Path.Combine(Path.GetTempPath(), "vcai-no-user-config-" + Guid.NewGuid().ToString("N") + ".json"),
                ["VISUALCRONAI_VisualCronAI__BridgePath"] = BridgeProtocolTests.BridgeExe,
                ["VISUALCRONAI_VisualCronAI__Audit__Directory"] = _auditDir,
                ["VISUALCRONAI_VisualCronAI__Environments__DEV__Address"] = "vc-dev.invalid",
                ["VISUALCRONAI_VisualCronAI__Environments__DEV__ConnectTimeoutSeconds"] = "5",
            },
        });
        _client = await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task All_required_tools_are_discoverable_with_descriptions()
    {
        var tools = await _client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        foreach (var required in RequiredTools) Assert.Contains(required, names);
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Name + " has no description"));
        var apply = tools.Single(t => t.Name == "visualcron_apply_approved_changes");
        Assert.True(apply.ProtocolTool.Annotations?.DestructiveHint);
        var list = tools.Single(t => t.Name == "visualcron_list_jobs");
        Assert.True(list.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task List_environments_works_and_exposes_no_secrets()
    {
        var result = await _client.CallToolAsync("visualcron_list_environments", new Dictionary<string, object?>());
        Assert.NotEqual(true, result.IsError);
        string text = string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("DEV", doc.RootElement.GetProperty("defaultEnvironment").GetString());
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unreachable_server_returns_a_tool_error_not_a_crash()
    {
        var result = await _client.CallToolAsync("visualcron_list_jobs", new Dictionary<string, object?> { ["environment"] = "DEV" });
        Assert.True(result.IsError);
        string text = string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("connection_failed", text);

        // Server is still healthy afterwards.
        var again = await _client.CallToolAsync("visualcron_list_environments", new Dictionary<string, object?>());
        Assert.NotEqual(true, again.IsError);
    }

    [Fact]
    public async Task Unconfigured_environment_is_refused_before_any_connection()
    {
        // UAT has an empty Address in appsettings.json: the server must refuse without resolving/contacting anything.
        var result = await _client.CallToolAsync("visualcron_test_connection", new Dictionary<string, object?> { ["environment"] = "UAT" });
        Assert.True(result.IsError);
        Assert.Contains("no VisualCron server address configured", string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    [Fact]
    public async Task Unknown_environment_and_proposal_are_rejected()
    {
        var r1 = await _client.CallToolAsync("visualcron_list_jobs", new Dictionary<string, object?> { ["environment"] = "PRODUCTION; DROP" });
        Assert.True(r1.IsError);
        var r2 = await _client.CallToolAsync("visualcron_apply_approved_changes", new Dictionary<string, object?> { ["proposalId"] = "prp_doesnotexist" });
        Assert.True(r2.IsError);
        Assert.Contains("not found", string.Join("", r2.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        try { if (Directory.Exists(_auditDir)) Directory.Delete(_auditDir, true); } catch { }
    }
}
