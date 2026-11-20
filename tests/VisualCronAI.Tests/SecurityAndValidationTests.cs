using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VisualCronAI.Contracts;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Models;
using VisualCronAI.Core.Scheduling;
using VisualCronAI.Core.Validation;
using VisualCronAI.Infrastructure.Audit;
using VisualCronAI.Mcp.Tools;

namespace VisualCronAI.Tests;

public class SecretScannerTests
{
    [Theory]
    [InlineData("--password=Hunter2!!", "password assignment")]
    [InlineData("Server=x;Database=y;User Id=u;Password=S3cr3t!;", "password assignment")]
    [InlineData("client_secret=abcDEF1234567890xyz", "client secret")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=a;AccountKey=Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MA==", "storage account key")]
    [InlineData("https://a.blob.core.windows.net/c?sv=2020&sig=abcdefghijklmnop1234%3D", "SAS signature")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789", "bearer token")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----", "private key block")]
    public void Detects_secrets(string text, string rule) => Assert.Contains(rule, SecretScanner.Scan(text));

    [Theory]
    [InlineData("run --env DEV --pipeline p_D365_Product_Load --param LoadDate={DATEFORMAT(yyyy-MM-dd)}")]
    [InlineData("password={CREDENTIAL(svc)}")]
    [InlineData("--param password_policy=strict")]
    [InlineData("Token count is 5")]
    public void Ignores_non_secrets(string text) => Assert.Empty(SecretScanner.Scan(text));

    [Fact]
    public void Redacts_logic_app_trigger_url_signatures()
    {
        // Shape of the ADF pipeline parameter emailLogicAppUrl seen in real pipeline-status output.
        const string sig = "aB3-dE_fGhIjK1-mNoPqRsT_uVwXyZ0123456789abc";
        string json = "{\"parameters\":{\"emailLogicAppUrl\":\"https://prod-14.southcentralus.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?api-version=2016-10-01&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=" + sig + "\"}}";
        var redacted = SecretScanner.Redact(json);
        Assert.DoesNotContain(sig, redacted);
        Assert.DoesNotContain(sig, SecretScanner.Redact(json.Replace("&", "\\u0026")));
        Assert.Contains("logic.azure.com", redacted);
        System.Text.Json.JsonDocument.Parse(redacted).Dispose();
        Assert.DoesNotContain(sig, SecretScanner.Redact("https://f.azurewebsites.net/api/run?code=" + sig));
    }

    [Fact]
    public void Redacts_oauth_token_response_and_truncated_token_fragments()
    {
        // Shape of the real VisualCron "Get Access Token" HTTP task output.
        string sig = new string('a', 60) + "_-" + new string('Z', 80);
        string resp = "{\"token_type\":\"Bearer\",\"expires_in\":3599,\"access_token\":\"eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL21hbmFnZW1lbnQuYXp1cmUuY29tIn0." + sig + "\"}";
        Assert.DoesNotContain(sig, SecretScanner.Redact(resp));
        Assert.DoesNotContain(sig[10..], SecretScanner.Redact("…" + sig[10..] + "\"}"));   // tail-only fragment
        Assert.Equal("2e9727a6-afa2-4a5b-9c1d-123456789abc", SecretScanner.Redact("2e9727a6-afa2-4a5b-9c1d-123456789abc"));
        const string url = "https://management.azure.com/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/dev-d365-dataload-rg/providers/Microsoft.DataFactory/factories/TestInstance-d365/pipelines/s_D365_Products_Blob_DataFlow/createRun?api-version=2018-06-01";
        Assert.Equal(url, SecretScanner.Redact(url));
    }

    [Fact]
    public async Task Job_logs_are_redacted_before_truncation()
    {
        var h = new TestHost();
        var job = h.Gateway.Add("DEV", new JobDto { Name = "J", Group = "ADF" });
        var tools = new ReadTools(h.Gateway, h.Service, h.Environments, Options.Create(h.Options));
        string logs = await tools.GetJobLogs(job.Id, "DEV", maxCharsPerStream: 200);
        Assert.DoesNotContain("Hunter2", logs);
        Assert.DoesNotContain("abcdefghijklmnop", logs);
    }

    [Fact]
    public void Redaction_keeps_json_valid()
    {
        var json = JsonSerializer.Serialize(new
        {
            a = "pwd=abc123; next",
            b = "-----BEGIN PRIVATE KEY-----\nMIIabc+/=\n",
            c = "Password=xyz\"quoted",
            d = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop",
        });
        var redacted = SecretScanner.Redact(json);
        using var doc = JsonDocument.Parse(redacted);
        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("MIIabc", redacted);
        Assert.DoesNotContain("eyJzdWIi", redacted);
        Assert.Contains("***REDACTED***", redacted);
    }
}

public class CredentialExposureTests
{
    [Fact]
    public void Wire_contracts_have_no_password_fields()
    {
        // The MCP host never holds VisualCron passwords: ConnectParams only carries a Credential Manager *target name*.
        var suspicious = typeof(BridgeRequest).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (t, p)))
            .Where(x => x.p.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || x.p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.t.Name + "." + x.p.Name).ToList();
        Assert.Empty(suspicious);
    }

    [Fact]
    public void Configuration_model_has_no_password_fields()
    {
        var types = new[] { typeof(VisualCronAIOptions), typeof(EnvironmentOptions), typeof(ApprovalOptions), typeof(Adf.Models.AdfEnvironmentOptions) };
        var suspicious = types.SelectMany(t => t.GetProperties()).Where(p =>
            (p.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase))
            && p.Name != "ClientSecretName").Select(p => p.DeclaringType!.Name + "." + p.Name).ToList();
        Assert.Empty(suspicious);
    }

    [Fact]
    public async Task Tool_responses_redact_secrets_found_on_the_server()
    {
        var h = new TestHost();
        var job = h.Gateway.Add("DEV", new JobDto
        {
            Name = "Legacy", Group = "ADF",
            Tasks = [new TaskDto { Name = "x", TaskType = TaskTypes.Execute, Execute = new ExecuteSettingsDto { CmdLine = "sqlcmd.exe", Arguments = "-S db -U sa -P x --password=Sup3rS3cret!" } }],
        });
        var tools = new ReadTools(h.Gateway, h.Service, h.Environments, Options.Create(h.Options));

        string jobJson = await tools.GetJob(job.Id, "DEV");
        string logsJson = await tools.GetJobLogs(job.Id, "DEV");

        Assert.DoesNotContain("Sup3rS3cret", jobJson);
        Assert.DoesNotContain("Hunter2", logsJson);
        Assert.DoesNotContain("eyJzdWIi", logsJson);
        Assert.Contains("***REDACTED***", jobJson);
        JsonDocument.Parse(jobJson).Dispose();
        JsonDocument.Parse(logsJson).Dispose();
    }

    [Fact]
    public async Task Secrets_in_new_jobs_are_rejected()
    {
        var h = new TestHost();
        var input = TestHost.D365Draft();
        input.Tasks.Add(new TaskInput { Type = "Execute", Name = "bad", CmdLine = "tool.exe", Arguments = "--api-key=0123456789abcdef --password=letmein123" });
        var d = await h.Service.DraftJobAsync(input, default);
        Assert.False(d.Validation.IsValid);
        Assert.Contains(d.Validation.Errors, e => e.Path.Contains("arguments") && e.Message.Contains("secret"));
    }

    [Fact]
    public async Task Audit_log_is_hash_chained_and_redacted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vcai-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new JsonlAuditLog(Options.Create(new VisualCronAIOptions { Audit = new AuditOptions { Directory = dir } }), NullLogger<JsonlAuditLog>.Instance);
            for (int i = 0; i < 3; i++)
                await log.WriteAsync(new AuditEvent { Timestamp = DateTimeOffset.UtcNow, EventType = "E" + i, User = "u", Environment = "DEV", Outcome = "ok", Details = "token=abcdef123456" }, default);

            Assert.Empty(JsonlAuditLog.VerifyChain(dir));
            var file = Directory.GetFiles(dir).Single();
            Assert.DoesNotContain("abcdef123456", File.ReadAllText(file));

            var lines = File.ReadAllLines(file);
            lines[1] = lines[1].Replace("\"E1\"", "\"TAMPERED\"");
            File.WriteAllLines(file, lines);
            Assert.NotEmpty(JsonlAuditLog.VerifyChain(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class ValidatorTests
{
    private static ValidationContext Ctx(bool isNew = true) => new()
    {
        Environment = "UAT",
        EnvironmentOptions = new EnvironmentOptions { AdfEnvironment = "UAT", AdfRunnerPath = @"C:\r\VisualCronAI.AdfRunner.exe" },
        IsNewJob = isNew,
        KnownNotificationIds = new HashSet<string> { "11111111-1111-1111-1111-111111111111" },
        KnownJobs = new Dictionary<string, string> { ["22222222-2222-2222-2222-222222222222"] = "Upstream" },
    };

    private static JobDto AdfJob(string env) => new()
    {
        Id = "33333333-3333-3333-3333-333333333333", Name = "Job_1", Group = "ADF", Active = true,
        Tasks = [new TaskDto { Name = "t", TaskType = TaskTypes.Execute, Execute = new ExecuteSettingsDto { CmdLine = @"C:\r\VisualCronAI.AdfRunner.exe", Arguments = $"run --env {env} --pipeline p_X" } }],
        Triggers = [new TriggerDto { Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Daily, Times = ["02:00"] } }],
    };

    [Fact]
    public void Adf_task_pointing_at_another_environment_is_an_error()
    {
        var r = JobValidator.Validate(AdfJob("DEV"), Ctx());
        Assert.Contains(r.Errors, e => e.Path.EndsWith("adf.environment"));
        Assert.True(JobValidator.Validate(AdfJob("UAT"), Ctx()).IsValid);
    }

    [Theory]
    [InlineData("bad<name>")]
    [InlineData("")]
    [InlineData("x; drop")]
    public void Invalid_job_names_are_rejected(string name)
    {
        var j = AdfJob("UAT"); j.Name = name;
        Assert.Contains(JobValidator.Validate(j, Ctx()).Errors, e => e.Path == "job.name");
    }

    [Fact]
    public void References_are_checked()
    {
        var j = AdfJob("UAT");
        j.ErrorNotificationIds = ["99999999-9999-9999-9999-999999999999"];
        j.Triggers.Add(new TriggerDto { Kind = TriggerKinds.JobEvent, JobEvent = new JobEventDto { UpstreamJobId = j.Id, Condition = JobEventConditions.CompletedSuccessfully } });
        j.Triggers.Add(new TriggerDto { Kind = TriggerKinds.JobEvent, JobEvent = new JobEventDto { UpstreamJobId = "44444444-4444-4444-4444-444444444444", Condition = JobEventConditions.CompletedSuccessfully } });
        var r = JobValidator.Validate(j, Ctx());
        Assert.Contains(r.Errors, e => e.Path == "job.errorNotificationIds");
        Assert.Contains(r.Errors, e => e.Message.Contains("depend on itself"));
        Assert.Contains(r.Errors, e => e.Message.Contains("does not exist"));
    }

    [Fact]
    public void Bad_schedules_and_flows_are_rejected()
    {
        var j = AdfJob("UAT");
        j.Triggers[0].Schedule!.Times = ["25:00"];
        j.Tasks[0].Flows.Add(new FlowDto { EventType = FlowEvents.OnError, ActionType = FlowActions.RetryTask, RetryTimes = 50 });
        j.Tasks[0].Flows.Add(new FlowDto { EventType = FlowEvents.OnSuccess, ActionType = FlowActions.GotoTask, TaskId = "nope" });
        var r = JobValidator.Validate(j, Ctx());
        Assert.Contains(r.Errors, e => e.Path.Contains("times"));
        Assert.Contains(r.Errors, e => e.Path.Contains("retryTimes"));
        Assert.Contains(r.Errors, e => e.Path.Contains("taskId"));
    }

    [Fact]
    public void New_unmodeled_task_types_are_rejected()
    {
        var j = AdfJob("UAT");
        j.Tasks.Add(new TaskDto { Name = "sql", TaskType = "SQL", Editable = false });
        Assert.Contains(JobValidator.Validate(j, Ctx()).Errors, e => e.Path.EndsWith("taskType"));
    }
}

public class ScheduleNormalizerTests
{
    private static readonly DateTime Nov = new(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData("CT")]
    [InlineData("Central")]
    [InlineData("America/Chicago")]
    [InlineData("Central Standard Time")]
    public void Resolves_time_zone_aliases(string name) =>
        Assert.Equal(TimeSpan.FromHours(-6), ScheduleNormalizer.ResolveTimeZone(name).GetUtcOffset(Nov));

    [Fact]
    public void Converts_2am_central_to_utc_server()
    {
        var r = ScheduleNormalizer.ToServerLocal(new ScheduleDto { Mode = ScheduleModes.Daily, Times = ["02:00"], TimeZone = "CT" }, Utc, Nov);
        Assert.Equal(["08:00"], Assert.Single(r.Schedules).Times);
        Assert.Contains(r.Warnings, w => w.Contains("winter") && w.Contains("summer")); // UTC has no DST, Central does
    }

    [Fact]
    public void Weekly_schedule_crossing_midnight_shifts_weekdays()
    {
        var r = ScheduleNormalizer.ToServerLocal(new ScheduleDto { Mode = ScheduleModes.Weekly, Times = ["20:00"], DaysOfWeek = ["Friday"], TimeZone = "CT" }, Utc, Nov);
        var s = Assert.Single(r.Schedules);
        Assert.Equal(["02:00"], s.Times);
        Assert.Equal(["Saturday"], s.DaysOfWeek);
    }

    [Fact]
    public void Monthly_schedule_crossing_midnight_is_refused() =>
        Assert.Throws<VisualCronAIException>(() => ScheduleNormalizer.ToServerLocal(
            new ScheduleDto { Mode = ScheduleModes.Monthly, Times = ["20:00"], DaysOfMonth = [1], TimeZone = "CT" }, Utc, Nov));

    [Fact]
    public void Same_zone_is_unchanged_and_non_grid_times_are_split()
    {
        var central = ScheduleNormalizer.ResolveTimeZone("CT");
        var r = ScheduleNormalizer.ToServerLocal(new ScheduleDto { Mode = ScheduleModes.Daily, Times = ["02:15", "14:30"], TimeZone = "America/Chicago" }, central, Nov);
        Assert.Equal(2, r.Schedules.Count);
        Assert.Empty(r.Warnings);
        Assert.All(r.Schedules, s => Assert.Equal("", s.TimeZone));
    }
}
