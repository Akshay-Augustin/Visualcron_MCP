using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using VisualCronAI.Adf.Models;

namespace VisualCronAI.Adf.Services;

public interface IAdfClient
{
    Task<string> CreateRunAsync(string pipelineName, IReadOnlyDictionary<string, string> parameters, CancellationToken ct);
    Task<PipelineRunInfo> GetRunAsync(string runId, CancellationToken ct);
    Task CancelRunAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<ActivityRunInfo>> QueryActivityRunsAsync(string runId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public sealed class AdfApiException(string message, HttpStatusCode? status = null, bool transient = false) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public bool Transient { get; } = transient;
}

/// <summary>
/// Minimal Azure Data Factory REST client (api-version 2018-06-01):
///   POST pipelines/{name}/createRun, GET pipelineruns/{runId}, POST pipelineruns/{runId}/cancel,
///   POST pipelineruns/{runId}/queryActivityruns.
/// Authenticates with an Azure.Core <see cref="TokenCredential"/> (service principal from Key Vault in production).
/// </summary>
public sealed class AdfRestClient(HttpClient http, TokenCredential credential, AdfEnvironmentOptions options) : IAdfClient
{
    private readonly string _factoryUri =
        $"{options.ManagementEndpoint.TrimEnd('/')}/subscriptions/{Uri.EscapeDataString(options.SubscriptionId)}" +
        $"/resourceGroups/{Uri.EscapeDataString(options.ResourceGroup)}/providers/Microsoft.DataFactory/factories/{Uri.EscapeDataString(options.FactoryName)}";

    public async Task<string> CreateRunAsync(string pipelineName, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var body = new JsonObject();
        foreach (var (key, value) in parameters)
            body[AdfRunnerArguments.ParameterName(key)] = ConvertParameter(AdfRunnerArguments.ParameterType(key), value);

        using var response = await SendAsync(HttpMethod.Post, $"/pipelines/{Uri.EscapeDataString(pipelineName)}/createRun", body.ToJsonString(), ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        string? runId = json?["runId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(runId)) throw new AdfApiException("createRun response did not contain a runId.");
        return runId;
    }

    public async Task<PipelineRunInfo> GetRunAsync(string runId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/pipelineruns/{Uri.EscapeDataString(runId)}", null, ct);
        var j = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw new AdfApiException("Empty pipeline run response.");
        return new PipelineRunInfo
        {
            RunId = j["runId"]?.GetValue<string>() ?? runId,
            Status = j["status"]?.GetValue<string>() ?? "",
            Message = j["message"]?.GetValue<string>() ?? "",
            RunStart = ParseDate(j["runStart"]),
            RunEnd = ParseDate(j["runEnd"]),
            DurationInMs = j["durationInMs"]?.GetValue<long?>(),
        };
    }

    public async Task CancelRunAsync(string runId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"/pipelineruns/{Uri.EscapeDataString(runId)}/cancel?isRecursive=true", "{}", ct);
    }

    public async Task<IReadOnlyList<ActivityRunInfo>> QueryActivityRunsAsync(string runId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["lastUpdatedAfter"] = from.UtcDateTime.ToString("o"),
            ["lastUpdatedBefore"] = to.UtcDateTime.ToString("o"),
        };
        using var response = await SendAsync(HttpMethod.Post, $"/pipelineruns/{Uri.EscapeDataString(runId)}/queryActivityruns", body.ToJsonString(), ct);
        var j = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var list = new List<ActivityRunInfo>();
        foreach (var a in j?["value"]?.AsArray() ?? [])
        {
            if (a is null) continue;
            list.Add(new ActivityRunInfo
            {
                ActivityName = a["activityName"]?.GetValue<string>() ?? "",
                ActivityType = a["activityType"]?.GetValue<string>() ?? "",
                Status = a["status"]?.GetValue<string>() ?? "",
                ErrorCode = a["error"]?["errorCode"]?.ToString() ?? "",
                ErrorMessage = a["error"]?["message"]?.ToString() ?? "",
            });
        }
        return list;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relative, string? jsonBody, CancellationToken ct)
    {
        string sep = relative.Contains('?') ? "&" : "?";
        var request = new HttpRequestMessage(method, _factoryUri + relative + sep + "api-version=" + Uri.EscapeDataString(options.ApiVersion));
        var token = await credential.GetTokenAsync(new TokenRequestContext([options.ManagementEndpoint.TrimEnd('/') + "/.default"]), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AdfApiException($"Network error calling ADF: {ex.Message}", transient: true);
        }
        if (response.IsSuccessStatusCode) return response;

        string detail = "";
        try
        {
            var err = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            detail = err?["error"]?["message"]?.GetValue<string>() ?? "";
        }
        catch (JsonException) { }
        bool transient = response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
        var status = response.StatusCode;
        response.Dispose();
        throw new AdfApiException($"ADF API {method} {relative.Split('?')[0]} failed: {(int)status} {status}. {detail}".Trim(), status, transient);
    }

    private static JsonNode? ConvertParameter(string type, string value) => type switch
    {
        "int" when long.TryParse(value, out var l) => JsonValue.Create(l),
        "float" when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
        "bool" when bool.TryParse(value, out var b) => JsonValue.Create(b),
        "json" => JsonNode.Parse(value),
        _ => JsonValue.Create(value),
    };

    private static DateTimeOffset? ParseDate(JsonNode? n) =>
        n is null ? null : DateTimeOffset.TryParse(n.ToString(), out var d) ? d : null;
}
