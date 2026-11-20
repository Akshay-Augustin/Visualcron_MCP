using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using VisualCronAI.Adf.Models;
using VisualCronAI.Adf.Services;

namespace VisualCronAI.Tests;

public class AdfRunnerArgumentsTests
{
    [Fact]
    public void Round_trips_through_windows_command_line_quoting()
    {
        var a = new AdfRunnerArguments
        {
            Environment = "UAT",
            PipelineName = "p_D365 Product Load",
            Parameters = new() { ["LoadDate"] = "{DATEFORMAT(yyyy-MM-dd)}", ["Path"] = @"C:\data dir\", ["Count:int"] = "5", ["Empty"] = "" },
            TimeoutMinutes = 240,
        };
        var cmd = a.ToCommandLine();
        var back = AdfRunnerArguments.Parse(cmd);
        Assert.Equal(a.Environment, back.Environment);
        Assert.Equal(a.PipelineName, back.PipelineName);
        Assert.Equal(a.Parameters, back.Parameters);
        Assert.Equal(240, back.TimeoutMinutes);
        Assert.Empty(back.Validate());
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("line1\nline2")]
    public void Rejects_dangerous_parameter_values(string value)
    {
        var a = new AdfRunnerArguments { Environment = "DEV", PipelineName = "p", Parameters = new() { ["x"] = value } };
        Assert.NotEmpty(a.Validate());
    }

    [Theory]
    [InlineData("p; del *")]
    [InlineData("../p")]
    [InlineData("")]
    public void Rejects_invalid_pipeline_names(string name) =>
        Assert.NotEmpty(new AdfRunnerArguments { Environment = "DEV", PipelineName = name }.Validate());

    [Fact]
    public void Typed_parameters_are_checked() =>
        Assert.NotEmpty(new AdfRunnerArguments { Environment = "DEV", PipelineName = "p", Parameters = new() { ["n:int"] = "abc" } }.Validate());
}

public class AdfPipelineRunnerTests
{
    private sealed class FakeAdf : IAdfClient
    {
        public Queue<Func<PipelineRunInfo>> Statuses { get; } = new();
        public Exception? CreateError { get; set; }
        public int CreateCalls, CancelCalls, GetCalls;
        public int TransientGetFailures { get; set; }

        public Task<string> CreateRunAsync(string pipelineName, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
        {
            CreateCalls++;
            if (CreateError != null) throw CreateError;
            return Task.FromResult("run-123");
        }

        public Task<PipelineRunInfo> GetRunAsync(string runId, CancellationToken ct)
        {
            GetCalls++;
            if (TransientGetFailures-- > 0) throw new AdfApiException("429", HttpStatusCode.TooManyRequests, transient: true);
            return Task.FromResult(Statuses.Count > 1 ? Statuses.Dequeue()() : Statuses.Peek()());
        }

        public Task CancelRunAsync(string runId, CancellationToken ct) { CancelCalls++; return Task.CompletedTask; }

        public Task<IReadOnlyList<ActivityRunInfo>> QueryActivityRunsAsync(string runId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ActivityRunInfo>>([new ActivityRunInfo { ActivityName = "Copy_Product", ActivityType = "Copy", Status = "Failed", ErrorCode = "2200", ErrorMessage = "Sink table not found" }]);
    }

    private static readonly AdfRunnerArguments Args = new() { Environment = "DEV", PipelineName = "p_D365_Product_Load", PollSeconds = 5, TimeoutMinutes = 1 };
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static PipelineRunInfo S(string status) => new() { RunId = "run-123", Status = status, Message = status == "Failed" ? "Operation on target Copy_Product failed" : "" };

    [Fact]
    public async Task Success_returns_zero_and_logs_run_id()
    {
        var adf = new FakeAdf();
        adf.Statuses.Enqueue(() => S("Queued"));
        adf.Statuses.Enqueue(() => S("InProgress"));
        adf.Statuses.Enqueue(() => S("Succeeded"));
        var output = new StringWriter();
        int code = await new AdfPipelineRunner(adf, output, new StringWriter(), NoDelay).RunAsync(Args, default);
        Assert.Equal(RunnerExitCodes.Succeeded, code);
        Assert.Contains("RunId=run-123", output.ToString());
        Assert.Contains("succeeded", output.ToString());
    }

    [Fact]
    public async Task Failure_returns_one_with_activity_diagnostics()
    {
        var adf = new FakeAdf();
        adf.Statuses.Enqueue(() => S("Failed"));
        var error = new StringWriter();
        int code = await new AdfPipelineRunner(adf, new StringWriter(), error, NoDelay).RunAsync(Args, default);
        Assert.Equal(RunnerExitCodes.PipelineFailed, code);
        Assert.Contains("Copy_Product", error.ToString());
        Assert.Contains("Sink table not found", error.ToString());
    }

    /// <summary>Virtual clock: each poll delay advances time instantly.</summary>
    private sealed class VirtualTime : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public Task Delay(TimeSpan d, CancellationToken _) { Ticks += d.Ticks; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Timeout_cancels_the_run_and_returns_three()
    {
        var adf = new FakeAdf();
        adf.Statuses.Enqueue(() => S("InProgress"));
        var time = new VirtualTime();
        var error = new StringWriter();
        int code = await new AdfPipelineRunner(adf, new StringWriter(), error, time.Delay, time)
            .RunAsync(new AdfRunnerArguments { Environment = "DEV", PipelineName = "p", PollSeconds = 30, TimeoutMinutes = 10 }, default);

        Assert.Equal(RunnerExitCodes.TimedOut, code);
        Assert.Equal(1, adf.CancelCalls);
        Assert.Equal(21, adf.GetCalls); // polls at 0, 30s ... 600s
        Assert.Contains("Cancellation requested for run run-123", error.ToString());
    }

    [Fact]
    public async Task Timeout_without_cancel_leaves_run_alone()
    {
        var adf = new FakeAdf();
        adf.Statuses.Enqueue(() => S("InProgress"));
        var time = new VirtualTime();
        int code = await new AdfPipelineRunner(adf, new StringWriter(), new StringWriter(), time.Delay, time)
            .RunAsync(new AdfRunnerArguments { Environment = "DEV", PipelineName = "p", PollSeconds = 60, TimeoutMinutes = 5, CancelOnTimeout = false }, default);
        Assert.Equal(RunnerExitCodes.TimedOut, code);
        Assert.Equal(0, adf.CancelCalls);
    }

    [Fact]
    public async Task Transient_poll_errors_are_retried()
    {
        var adf = new FakeAdf { TransientGetFailures = 2 };
        adf.Statuses.Enqueue(() => S("Succeeded"));
        var error = new StringWriter();
        int code = await new AdfPipelineRunner(adf, new StringWriter(), error, NoDelay).RunAsync(Args, default);
        Assert.Equal(RunnerExitCodes.Succeeded, code);
        Assert.Contains("Transient ADF error", error.ToString());
    }

    [Fact]
    public async Task Ambiguous_create_failure_is_not_retried()
    {
        var adf = new FakeAdf { CreateError = new AdfApiException("Network error", status: null, transient: true) };
        int code = await new AdfPipelineRunner(adf, new StringWriter(), new StringWriter(), NoDelay).RunAsync(Args, default);
        Assert.Equal(RunnerExitCodes.AzureApiError, code);
        Assert.Equal(1, adf.CreateCalls); // never risk starting a duplicate run
    }

    [Fact]
    public async Task Invalid_arguments_return_configuration_error()
    {
        int code = await new AdfPipelineRunner(new FakeAdf(), new StringWriter(), new StringWriter(), NoDelay)
            .RunAsync(new AdfRunnerArguments { Environment = "DEV", PipelineName = "bad;name" }, default);
        Assert.Equal(RunnerExitCodes.ConfigurationError, code);
    }
}

public class AdfRestClientTests
{
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static readonly AdfEnvironmentOptions Options = new()
    {
        SubscriptionId = "sub-1", ResourceGroup = "rg-dev", FactoryName = "adf-dev", TenantId = "t", ClientId = "c",
        KeyVaultUri = "https://kv.vault.azure.net/", ClientSecretName = "s",
    };

    [Fact]
    public async Task CreateRun_posts_typed_parameters_to_the_documented_endpoint()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"runId\":\"abc\"}", Encoding.UTF8, "application/json") });
        var client = new AdfRestClient(new HttpClient(handler), new FakeCredential(), Options);
        var runId = await client.CreateRunAsync("p_Load", new Dictionary<string, string> { ["Date"] = "2026-11-19", ["Rows:int"] = "10", ["Full:bool"] = "true", ["Cfg:json"] = "{\"a\":1}" }, default);

        Assert.Equal("abc", runId);
        var (req, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-dev/providers/Microsoft.DataFactory/factories/adf-dev/pipelines/p_Load/createRun?api-version=2018-06-01", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("2026-11-19", json["Date"]!.GetValue<string>());
        Assert.Equal(10, json["Rows"]!.GetValue<long>());
        Assert.True(json["Full"]!.GetValue<bool>());
        Assert.Equal(1, json["Cfg"]!["a"]!.GetValue<int>());
    }

    [Fact]
    public async Task Throttling_is_reported_as_transient()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"message\":\"slow down\"}}") });
        var client = new AdfRestClient(new HttpClient(handler), new FakeCredential(), Options);
        var ex = await Assert.ThrowsAsync<AdfApiException>(() => client.GetRunAsync("r", default));
        Assert.True(ex.Transient);
        Assert.Contains("slow down", ex.Message);
    }

    [Fact]
    public async Task GetRun_parses_status()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"runId\":\"r\",\"status\":\"Failed\",\"message\":\"boom\",\"runStart\":\"2026-11-19T02:00:00Z\",\"durationInMs\":1234}"),
        });
        var run = await new AdfRestClient(new HttpClient(handler), new FakeCredential(), Options).GetRunAsync("r", default);
        Assert.Equal("Failed", run.Status);
        Assert.Equal("boom", run.Message);
        Assert.Equal(1234, run.DurationInMs);
    }

    [Fact]
    public void Config_validation_requires_key_vault_settings()
    {
        var errors = new AdfEnvironmentOptions().Validate("DEV");
        Assert.Contains(errors, e => e.Contains("KeyVaultUri"));
        Assert.Contains(errors, e => e.Contains("ClientSecretName"));
        Assert.Empty(Options.Validate("DEV"));
    }
}
