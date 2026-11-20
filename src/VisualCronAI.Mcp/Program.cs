using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Services;
using VisualCronAI.Infrastructure.Api;
using VisualCronAI.Infrastructure.Approval;
using VisualCronAI.Infrastructure.Audit;
using VisualCronAI.Infrastructure.Authentication;
using VisualCronAI.Mcp;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Configuration layers (later wins): appsettings.json next to the exe → per-user overrides → environment variables.
// None of these may contain secrets; VisualCron auth uses Windows logon or a Credential Manager *target name*.
// VISUALCRONAI_USER_CONFIG overrides the per-user file location (tests point it at a non-existent file for isolation).
string userConfig = Environment.GetEnvironmentVariable("VISUALCRONAI_USER_CONFIG")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VisualCronAI", "appsettings.json");
builder.Configuration
    .AddJsonFile(userConfig, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("VISUALCRONAI_");

// STDIO transport: stdout carries MCP JSON-RPC only, so ALL logging goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.Configure<VisualCronAIOptions>(builder.Configuration.GetSection(VisualCronAIOptions.SectionName));
builder.Services.AddSingleton<EnvironmentRegistry>();
builder.Services.AddSingleton<ProposalStore>();
builder.Services.AddSingleton<DraftStore>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ICurrentUser, WindowsCurrentUser>();
builder.Services.AddSingleton<IAuditLog, JsonlAuditLog>();
builder.Services.AddSingleton<BridgeProcessGateway>();
builder.Services.AddSingleton<IVisualCronGateway>(sp => sp.GetRequiredService<BridgeProcessGateway>());
builder.Services.AddSingleton<WindowsDialogApprovalPrompt>();
builder.Services.AddSingleton<ChangeService>();

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "visualcron", Version = "0.1.0" };
        o.ServerInstructions = ServerInstructions.Text;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
