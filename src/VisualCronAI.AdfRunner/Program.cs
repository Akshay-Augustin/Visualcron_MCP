using System.Text.Json;
using VisualCronAI.Adf.Models;
using VisualCronAI.Adf.Services;

// VisualCronAI.AdfRunner — invoked by a VisualCron "Execute" task:
//   VisualCronAI.AdfRunner.exe run --env DEV --pipeline p_D365_Product_Load --param LoadDate={DATEFORMAT(yyyy-MM-dd)}
// Exit code 0 = pipeline succeeded; non-zero = failure (see RunnerExitCodes). VisualCron's task flows (retries) and
// job error notifications act on the exit code. Settings come from adf-runner.json next to the executable
// (or the path in VISUALCRONAI_ADF_CONFIG). No secrets are passed on the command line or stored in the job.

if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
{
    Console.WriteLine("""
        Usage: VisualCronAI.AdfRunner run --env <ENV> --pipeline <name> [--param name[:type]=value]...
                                          [--timeout-minutes 120] [--poll-seconds 30] [--no-cancel-on-timeout]
        Types: string (default), int, bool, float, json.
        Exit codes: 0 succeeded, 1 pipeline failed, 2 cancelled, 3 timed out, 4 configuration error,
                    5 authentication error, 6 Azure API error.
        Config: adf-runner.json next to the executable, or VISUALCRONAI_ADF_CONFIG.
        """);
    return args.Length == 0 ? RunnerExitCodes.ConfigurationError : 0;
}

AdfRunnerArguments parsed;
try
{
    parsed = AdfRunnerArguments.Parse(args);
}
catch (FormatException ex)
{
    Console.Error.WriteLine("[adf-runner] Invalid arguments: " + ex.Message);
    return RunnerExitCodes.ConfigurationError;
}
var argErrors = parsed.Validate();
if (argErrors.Count > 0)
{
    foreach (var e in argErrors) Console.Error.WriteLine("[adf-runner] Invalid arguments: " + e);
    return RunnerExitCodes.ConfigurationError;
}

string configPath = Environment.GetEnvironmentVariable("VISUALCRONAI_ADF_CONFIG")
                    ?? Path.Combine(AppContext.BaseDirectory, "adf-runner.json");
AdfRunnerConfig config;
try
{
    config = JsonSerializer.Deserialize<AdfRunnerConfig>(File.ReadAllText(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }) ?? new AdfRunnerConfig();
    config.Environments = new Dictionary<string, AdfEnvironmentOptions>(config.Environments, StringComparer.OrdinalIgnoreCase);
}
catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"[adf-runner] Cannot read config '{configPath}': {ex.Message}");
    return RunnerExitCodes.ConfigurationError;
}

if (!config.Environments.TryGetValue(parsed.Environment, out var env))
{
    Console.Error.WriteLine($"[adf-runner] Environment '{parsed.Environment}' is not configured in {configPath}.");
    return RunnerExitCodes.ConfigurationError;
}
var configErrors = env.Validate(parsed.Environment);
if (configErrors.Count > 0)
{
    foreach (var e in configErrors) Console.Error.WriteLine("[adf-runner] CONFIG ERROR: " + e);
    return RunnerExitCodes.ConfigurationError;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var credential = await AdfCredentialFactory.CreateAsync(env, cts.Token);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
    var client = new AdfRestClient(http, credential, env);
    var runner = new AdfPipelineRunner(client, Console.Out, Console.Error);
    return await runner.RunAsync(parsed, cts.Token);
}
catch (AdfAuthenticationException ex)
{
    Console.Error.WriteLine("[adf-runner] AUTHENTICATION ERROR: " + ex.Message);
    return RunnerExitCodes.AuthenticationError;
}
catch (Azure.Identity.AuthenticationFailedException ex)
{
    Console.Error.WriteLine("[adf-runner] AUTHENTICATION ERROR: " + ex.Message.Split('\n')[0]);
    return RunnerExitCodes.AuthenticationError;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("[adf-runner] Cancelled.");
    return RunnerExitCodes.PipelineCancelled;
}
