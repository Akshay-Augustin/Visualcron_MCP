using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Services;
using VisualCronAI.Core.Validation;

namespace VisualCronAI.Infrastructure.Api;

/// <summary>
/// <see cref="IVisualCronGateway"/> backed by the VisualCronAI.Bridge (.NET Framework 4.8) child process, which is the
/// only component that loads the vendor VisualCronAPI.dll. Requests are serialized (one at a time) over stdin/stdout.
/// Connection settings come from configuration; for Credential Manager auth only the *target name* is sent — the
/// bridge reads the password itself, so no secret ever passes through this process or the MCP protocol.
/// </summary>
public sealed class BridgeProcessGateway : IVisualCronGateway, IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly VisualCronAIOptions _options;
    private readonly EnvironmentRegistry _environments;
    private readonly ILogger<BridgeProcessGateway> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, ServerInfoDto> _connected = new(StringComparer.OrdinalIgnoreCase);
    private Process? _process;
    private long _nextId;

    public BridgeProcessGateway(IOptions<VisualCronAIOptions> options, EnvironmentRegistry environments, ILogger<BridgeProcessGateway> logger)
    {
        _options = options.Value;
        _environments = environments;
        _logger = logger;
    }

    public string BridgePath => string.IsNullOrWhiteSpace(_options.BridgePath)
        ? Path.Combine(AppContext.BaseDirectory, "bridge", "VisualCronAI.Bridge.exe")
        : Environment.ExpandEnvironmentVariables(_options.BridgePath);

    // ------------------------------------------------------------ IVisualCronGateway

    public Task<HelloResult> GetBridgeInfoAsync(CancellationToken ct) => CallAsync<HelloResult>(BridgeMethods.Hello, new { }, ct);

    public async Task<ServerInfoDto> ConnectAsync(string environment, CancellationToken ct)
    {
        string env = _environments.Resolve(environment);
        if (_connected.TryGetValue(env, out var cached) && IsRunning) return cached;
        var o = _environments.Get(env);
        // Never attempt an (authenticated) connection to an unconfigured/placeholder host: corporate DNS may resolve it.
        if (string.IsNullOrWhiteSpace(o.Address) || o.Address.Contains("CHANGE-ME", StringComparison.OrdinalIgnoreCase) || o.Address.Contains(' '))
            throw new GatewayException(BridgeErrorCodes.InvalidRequest,
                $"Environment {env} has no VisualCron server address configured. Set VisualCronAI:Environments:{env}:Address in %APPDATA%\\VisualCronAI\\appsettings.json.");
        var info = await CallAsync<ServerInfoDto>(BridgeMethods.Connect, new ConnectParams
        {
            Environment = env,
            Address = o.Address,
            Port = o.Port,
            AuthMode = o.AuthMode,
            CredentialTarget = o.CredentialTarget,
            TimeoutSeconds = o.ConnectTimeoutSeconds,
        }, ct);
        _connected[env] = info;
        return info;
    }

    public async Task<ServerInfoDto> TestConnectionAsync(string environment, CancellationToken ct)
    {
        string env = _environments.Resolve(environment);
        _connected.TryRemove(env, out _);
        await ConnectAsync(env, ct);
        return await CallAsync<ServerInfoDto>(BridgeMethods.TestConnection, new EnvParams { Environment = env }, ct);
    }

    public Task<IReadOnlyList<JobSummaryDto>> ListJobsAsync(string environment, string? nameContains, string? group, CancellationToken ct) =>
        EnvCallAsync<IReadOnlyList<JobSummaryDto>>(environment, BridgeMethods.ListJobs,
            env => new ListJobsParams { Environment = env, NameContains = nameContains, Group = group }, ct);

    public Task<JobDto> GetJobAsync(string environment, string jobId, CancellationToken ct) =>
        EnvCallAsync<JobDto>(environment, BridgeMethods.GetJob, env => new JobIdParams { Environment = env, JobId = jobId }, ct);

    public Task<IReadOnlyList<JobHistoryEntryDto>> GetJobHistoryAsync(JobHistoryParams p, CancellationToken ct) =>
        EnvCallAsync<IReadOnlyList<JobHistoryEntryDto>>(p.Environment, BridgeMethods.GetJobHistory, env => { p.Environment = env; return p; }, ct);

    public Task<IReadOnlyList<TaskLogEntryDto>> GetJobLogsAsync(JobLogsParams p, CancellationToken ct) =>
        EnvCallAsync<IReadOnlyList<TaskLogEntryDto>>(p.Environment, BridgeMethods.GetJobLogs, env => { p.Environment = env; return p; }, ct);

    public Task<IReadOnlyList<NamedRefDto>> ListNotificationsAsync(string environment, CancellationToken ct) =>
        EnvCallAsync<IReadOnlyList<NamedRefDto>>(environment, BridgeMethods.ListNotifications, env => new EnvParams { Environment = env }, ct);

    public Task<IReadOnlyList<NamedRefDto>> ListCredentialsAsync(string environment, CancellationToken ct) =>
        EnvCallAsync<IReadOnlyList<NamedRefDto>>(environment, BridgeMethods.ListCredentials, env => new EnvParams { Environment = env }, ct);

    public Task<SchedulePreviewDto> PreviewScheduleAsync(string environment, JobDto job, int count, CancellationToken ct) =>
        EnvCallAsync<SchedulePreviewDto>(environment, BridgeMethods.PreviewSchedule, env => new PreviewScheduleParams { Environment = env, Job = job, Count = count }, ct);

    public Task<WriteResultDto> CreateJobAsync(string environment, JobDto job, CancellationToken ct) =>
        EnvCallAsync<WriteResultDto>(environment, BridgeMethods.CreateJob, env => new CreateJobParams { Environment = env, Job = job }, ct, retryOnReconnect: false);

    public Task<WriteResultDto> UpdateJobAsync(string environment, JobDto job, string expectedVersionToken, CancellationToken ct) =>
        EnvCallAsync<WriteResultDto>(environment, BridgeMethods.UpdateJob,
            env => new UpdateJobParams { Environment = env, Job = job, ExpectedVersionToken = expectedVersionToken }, ct, retryOnReconnect: false);

    public async Task<WriteResultDto> CloneJobAsync(CloneJobParams p, CancellationToken ct)
    {
        p.SourceEnvironment = _environments.Resolve(p.SourceEnvironment);
        p.TargetEnvironment = _environments.Resolve(p.TargetEnvironment);
        await ConnectAsync(p.SourceEnvironment, ct);
        await ConnectAsync(p.TargetEnvironment, ct);
        return await CallAsync<WriteResultDto>(BridgeMethods.CloneJob, p, ct);
    }

    public Task<WriteResultDto> SetJobActiveAsync(string environment, string jobId, bool active, string expectedVersionToken, CancellationToken ct) =>
        EnvCallAsync<WriteResultDto>(environment, BridgeMethods.SetJobActive,
            env => new SetJobActiveParams { Environment = env, JobId = jobId, Active = active, ExpectedVersionToken = expectedVersionToken }, ct, retryOnReconnect: false);

    /// <summary>Integration-test cleanup (VCAI_IT_* jobs only; enforced by the bridge). Not part of IVisualCronGateway.</summary>
    public Task<WriteResultDto> DeleteTestJobAsync(string environment, string jobId, CancellationToken ct) =>
        EnvCallAsync<WriteResultDto>(environment, BridgeMethods.DeleteTestJob, env => new JobIdParams { Environment = env, JobId = jobId }, ct, retryOnReconnect: false);

    // ------------------------------------------------------------ plumbing

    private async Task<T> EnvCallAsync<T>(string environment, string method, Func<string, object> buildParams, CancellationToken ct, bool retryOnReconnect = true)
    {
        string env = _environments.Resolve(environment);
        await ConnectAsync(env, ct);
        try
        {
            return await CallAsync<T>(method, buildParams(env), ct);
        }
        catch (GatewayException ex) when (ex.Code == BridgeErrorCodes.NotConnected && retryOnReconnect)
        {
            _logger.LogWarning("Connection to {Environment} lost; reconnecting once.", env);
            _connected.TryRemove(env, out _);
            await ConnectAsync(env, ct);
            return await CallAsync<T>(method, buildParams(env), ct);
        }
    }

    private bool IsRunning => _process is { HasExited: false };

    private async Task<T> CallAsync<T>(string method, object parameters, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            EnsureStarted();
            var request = new BridgeRequest
            {
                Id = Interlocked.Increment(ref _nextId),
                Method = method,
                ParamsJson = JsonSerializer.Serialize(parameters, parameters.GetType(), Json),
            };
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json));
            await _process.StandardInput.FlushAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _options.BridgeRequestTimeoutSeconds)));
            string? line;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // The stream position is now unknown: restart the bridge on the next call.
                KillBridge($"request '{method}' timed out or was cancelled");
                if (ct.IsCancellationRequested) throw;
                throw new GatewayException(BridgeErrorCodes.Internal, $"VisualCron bridge did not answer '{method}' within {_options.BridgeRequestTimeoutSeconds}s.");
            }
            if (line == null)
            {
                KillBridge("bridge closed its output");
                throw new GatewayException(BridgeErrorCodes.Internal, "VisualCron bridge exited unexpectedly. Check the MCP server log (stderr).");
            }

            var response = JsonSerializer.Deserialize<BridgeResponse>(line, Json)
                           ?? throw new GatewayException(BridgeErrorCodes.Internal, "Empty response from bridge.");
            if (response.Id != request.Id)
            {
                KillBridge("response id mismatch");
                throw new GatewayException(BridgeErrorCodes.Internal, "Bridge protocol out of sync; restarting it.");
            }
            if (!response.Ok)
                throw new GatewayException(response.Error?.Code ?? BridgeErrorCodes.Internal, SecretScanner.Redact(response.Error?.Message ?? "Unknown bridge error."));
            return JsonSerializer.Deserialize<T>(response.ResultJson ?? "null", Json)
                   ?? throw new GatewayException(BridgeErrorCodes.Internal, $"Bridge returned no result for '{method}'.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnsureStarted()
    {
        if (IsRunning) return;
        _connected.Clear();
        string path = BridgePath;
        if (!File.Exists(path))
            throw new GatewayException(BridgeErrorCodes.Internal,
                $"VisualCron bridge not found at '{path}'. Run build.ps1 (publishes the bridge next to the MCP server) or set VisualCronAI:BridgePath.");

        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        psi.ArgumentList.Add("--install-dir");
        psi.ArgumentList.Add(_options.VisualCronInstallDir);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) _logger.LogInformation("{BridgeLog}", SecretScanner.Redact(e.Data));
        };
        p.Exited += (_, _) => _logger.LogWarning("VisualCron bridge process exited.");
        if (!p.Start()) throw new GatewayException(BridgeErrorCodes.Internal, "Could not start the VisualCron bridge.");
        p.StandardInput.AutoFlush = false;
        p.BeginErrorReadLine();
        _process = p;
        _logger.LogInformation("Started VisualCron bridge (pid {Pid}) from {Path}", p.Id, path);
    }

    private void KillBridge(string reason)
    {
        _logger.LogWarning("Stopping VisualCron bridge: {Reason}", reason);
        _connected.Clear();
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _process?.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new BridgeRequest { Id = 0, Method = BridgeMethods.Shutdown }, Json));
                await _process.StandardInput.FlushAsync();
                if (!_process.WaitForExit(3000)) _process.Kill(true);
            }
            catch { /* ignore */ }
        }
        _process?.Dispose();
    }
}
