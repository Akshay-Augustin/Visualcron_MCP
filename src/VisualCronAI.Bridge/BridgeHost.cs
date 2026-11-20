using System;
using System.IO;
using System.Text.Json;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge
{
    /// <summary>Reads one JSON request per line, executes it sequentially and writes one JSON response per line.</summary>
    internal sealed class BridgeHost : IDisposable
    {
        private readonly TextReader _input;
        private readonly TextWriter _output;
        private readonly VcSessions _sessions;
        private readonly JobOperations _ops;
        private readonly string _installDir;

        public BridgeHost(string installDir, TextReader input, TextWriter output)
        {
            _installDir = installDir;
            _input = input;
            _output = output;
            _sessions = new VcSessions();
            _ops = new JobOperations(_sessions);
        }

        public void RunLoop()
        {
            BridgeLog.Info("Bridge started. VisualCron install dir: " + _installDir);
            string? line;
            while ((line = _input.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                BridgeResponse response;
                BridgeRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<BridgeRequest>(line, BridgeJson.Options);
                    if (request == null) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Empty request.");
                    if (request.Method == BridgeMethods.Shutdown)
                    {
                        Write(new BridgeResponse { Id = request.Id, Ok = true, ResultJson = "{}" });
                        break;
                    }
                    string result = Dispatch(request);
                    response = new BridgeResponse { Id = request.Id, Ok = true, ResultJson = result };
                }
                catch (BridgeException bex)
                {
                    response = Fail(request, bex.Code, bex.Message);
                }
                catch (JsonException jex)
                {
                    response = Fail(request, BridgeErrorCodes.InvalidRequest, "Malformed JSON: " + jex.Message);
                }
                catch (Exception ex)
                {
                    var inner = ex.GetBaseException();
                    BridgeLog.Error("Unhandled error in " + (request?.Method ?? "?") + ": " + inner.GetType().Name + ": " + inner.Message);
                    response = Fail(request, BridgeErrorCodes.Internal, inner.GetType().Name + ": " + inner.Message);
                }
                Write(response);
            }
            BridgeLog.Info("Bridge stopping.");
        }

        private string Dispatch(BridgeRequest r)
        {
            switch (r.Method)
            {
                case BridgeMethods.Hello:
                    return Ser(new HelloResult
                    {
                        BridgeVersion = typeof(BridgeHost).Assembly.GetName().Version?.ToString() ?? "",
                        VisualCronApiVersion = VcSessions.ApiVersion(),
                        VisualCronInstallDir = _installDir,
                        ClrVersion = Environment.Version.ToString(),
                    });
                case BridgeMethods.Connect: return Ser(_sessions.Connect(P<ConnectParams>(r)));
                case BridgeMethods.TestConnection: return Ser(_sessions.Test(P<EnvParams>(r).Environment));
                case BridgeMethods.ListJobs: return Ser(_ops.ListJobs(P<ListJobsParams>(r)));
                case BridgeMethods.GetJob: return Ser(_ops.GetJob(P<JobIdParams>(r)));
                case BridgeMethods.GetJobHistory: return Ser(_ops.GetJobHistory(P<JobHistoryParams>(r)));
                case BridgeMethods.GetJobLogs: return Ser(_ops.GetJobLogs(P<JobLogsParams>(r)));
                case BridgeMethods.ListNotifications: return Ser(_ops.ListNotifications(P<EnvParams>(r)));
                case BridgeMethods.ListCredentials: return Ser(_ops.ListCredentials(P<EnvParams>(r)));
                case BridgeMethods.PreviewSchedule: return Ser(_ops.PreviewSchedule(P<PreviewScheduleParams>(r)));
                case BridgeMethods.CreateJob: return Ser(_ops.CreateJob(P<CreateJobParams>(r)));
                case BridgeMethods.UpdateJob: return Ser(_ops.UpdateJob(P<UpdateJobParams>(r)));
                case BridgeMethods.CloneJob: return Ser(_ops.CloneJob(P<CloneJobParams>(r)));
                case BridgeMethods.SetJobActive: return Ser(_ops.SetJobActive(P<SetJobActiveParams>(r)));
                case BridgeMethods.DeleteTestJob: return Ser(_ops.DeleteTestJob(P<JobIdParams>(r)));
                default:
                    throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Unknown method: " + r.Method);
            }
        }

        private static T P<T>(BridgeRequest r) where T : new()
        {
            if (string.IsNullOrWhiteSpace(r.ParamsJson)) return new T();
            return JsonSerializer.Deserialize<T>(r.ParamsJson, BridgeJson.Options) ?? new T();
        }

        private static string Ser<T>(T value) => JsonSerializer.Serialize(value, BridgeJson.Options);

        private static BridgeResponse Fail(BridgeRequest? r, string code, string message) =>
            new BridgeResponse { Id = r?.Id ?? 0, Ok = false, Error = new BridgeError { Code = code, Message = message } };

        private void Write(BridgeResponse response)
        {
            _output.WriteLine(JsonSerializer.Serialize(response, BridgeJson.Options));
            _output.Flush();
        }

        public void Dispose() => _sessions.Dispose();
    }

    internal static class BridgeJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };
    }

    public sealed class BridgeException : Exception
    {
        public string Code { get; }
        public BridgeException(string code, string message) : base(message) { Code = code; }
    }
}
