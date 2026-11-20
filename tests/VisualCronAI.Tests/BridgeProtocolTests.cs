using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VisualCronAI.Contracts;

namespace VisualCronAI.Tests;

/// <summary>
/// Launches the real VisualCronAI.Bridge.exe (which loads the installed VisualCronAPI.dll) and exercises the wire
/// protocol. These tests NEVER connect to a VisualCron server: the only connect attempt targets 192.0.2.1
/// (RFC 5737 TEST-NET-1, guaranteed unroutable).
/// </summary>
public sealed class BridgeProtocolTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly Process _bridge;
    private long _id;

    public static string BridgeExe
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VisualCronAI.sln"))) dir = dir.Parent;
            var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            return Path.Combine(dir!.FullName, "src", "VisualCronAI.Bridge", "bin", config, "net48", "VisualCronAI.Bridge.exe");
        }
    }

    public BridgeProtocolTests()
    {
        Assert.True(File.Exists(BridgeExe), "Bridge not built: " + BridgeExe);
        _bridge = Process.Start(new ProcessStartInfo(BridgeExe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false),
        })!;
        _bridge.BeginErrorReadLine();
    }

    private BridgeResponse Call(string method, object? p = null, string? rawLine = null)
    {
        long id = ++_id;
        string line = rawLine ?? JsonSerializer.Serialize(new BridgeRequest { Id = id, Method = method, ParamsJson = JsonSerializer.Serialize(p ?? new { }, Json) }, Json);
        _bridge.StandardInput.WriteLine(line);
        _bridge.StandardInput.Flush();
        var read = _bridge.StandardOutput.ReadLineAsync();
        Assert.True(read.Wait(TimeSpan.FromSeconds(60)), "bridge did not answer " + method);
        return JsonSerializer.Deserialize<BridgeResponse>(read.Result!, Json)!;
    }

    [Fact]
    public void Hello_reports_installed_visualcron_api_version()
    {
        var r = Call(BridgeMethods.Hello);
        Assert.True(r.Ok);
        var hello = JsonSerializer.Deserialize<HelloResult>(r.ResultJson!, Json)!;
        Assert.StartsWith("12.", hello.VisualCronApiVersion);
        Assert.StartsWith("4.", hello.ClrVersion);
    }

    [Fact]
    public void Offline_schedule_preview_uses_visualcron_engine()
    {
        var job = new JobDto
        {
            Triggers =
            [
                new TriggerDto { Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Weekly, Times = ["03:30"], DaysOfWeek = ["Monday", "Wednesday"] } },
            ],
        };
        var r = Call(BridgeMethods.PreviewSchedule, new PreviewScheduleParams { Environment = "", Job = job, Count = 4 });
        Assert.True(r.Ok, r.Error?.Message);
        var preview = JsonSerializer.Deserialize<SchedulePreviewDto>(r.ResultJson!, Json)!;
        Assert.Equal(4, preview.NextExecutionsServerTime.Count);
        Assert.All(preview.NextExecutionsServerTime, d =>
        {
            Assert.Contains(d.DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday });
            Assert.Equal(new TimeSpan(3, 30, 0), d.TimeOfDay);
        });
    }

    [Fact]
    public void Calls_without_connection_fail_cleanly()
    {
        var r = Call(BridgeMethods.GetJob, new JobIdParams { Environment = "DEV", JobId = Guid.NewGuid().ToString() });
        Assert.False(r.Ok);
        Assert.Equal(BridgeErrorCodes.NotConnected, r.Error!.Code);
    }

    [Fact]
    public void Unknown_methods_and_malformed_json_are_rejected_without_killing_the_bridge()
    {
        Assert.Equal(BridgeErrorCodes.InvalidRequest, Call("deleteEverything").Error!.Code);
        Assert.Equal(BridgeErrorCodes.InvalidRequest, Call("", rawLine: "{not json").Error!.Code);
        Assert.True(Call(BridgeMethods.Hello).Ok);
    }

    [Fact]
    public void Credential_manager_mode_without_entry_fails_without_leaking()
    {
        var r = Call(BridgeMethods.Connect, new ConnectParams
        {
            Environment = "TEST", Address = "192.0.2.1", AuthMode = AuthModes.CredentialManager,
            CredentialTarget = "VisualCronAI/does-not-exist-" + Guid.NewGuid().ToString("N"), TimeoutSeconds = 5,
        });
        Assert.False(r.Ok);
        Assert.Equal(BridgeErrorCodes.ConnectionFailed, r.Error!.Code);
        Assert.Contains("Credential Manager", r.Error.Message);
    }

    [Fact]
    public void Unreachable_server_reports_connection_failure()
    {
        var r = Call(BridgeMethods.Connect, new ConnectParams { Environment = "TEST", Address = "192.0.2.1", Port = 16444, AuthMode = AuthModes.ActiveDirectory, TimeoutSeconds = 5 });
        Assert.False(r.Ok);
        Assert.Equal(BridgeErrorCodes.ConnectionFailed, r.Error!.Code);
    }

    public void Dispose()
    {
        try
        {
            _bridge.StandardInput.WriteLine(JsonSerializer.Serialize(new BridgeRequest { Method = BridgeMethods.Shutdown }, Json));
            _bridge.StandardInput.Flush();
            if (!_bridge.WaitForExit(5000)) _bridge.Kill(true);
        }
        catch { }
        _bridge.Dispose();
    }
}
