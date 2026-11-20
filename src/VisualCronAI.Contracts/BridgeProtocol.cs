using System;
using System.Collections.Generic;

namespace VisualCronAI.Contracts
{
    // Wire protocol between the MCP host (.NET 10) and the VisualCron bridge (.NET Framework 4.8).
    // One JSON object per line on stdin (requests) and stdout (responses). The bridge writes diagnostics to stderr.
    // Requests never contain secrets: for password auth the bridge reads Windows Credential Manager itself.

    public sealed class BridgeRequest
    {
        public long Id { get; set; }
        public string Method { get; set; } = "";
        /// <summary>Method-specific parameters serialized as JSON.</summary>
        public string ParamsJson { get; set; } = "{}";
    }

    public sealed class BridgeResponse
    {
        public long Id { get; set; }
        public bool Ok { get; set; }
        public string? ResultJson { get; set; }
        public BridgeError? Error { get; set; }
    }

    public sealed class BridgeError
    {
        /// <summary>One of <see cref="BridgeErrorCodes"/>.</summary>
        public string Code { get; set; } = BridgeErrorCodes.Internal;
        public string Message { get; set; } = "";
    }

    public static class BridgeErrorCodes
    {
        public const string Internal = "internal";
        public const string InvalidRequest = "invalid_request";
        public const string NotConnected = "not_connected";
        public const string ConnectionFailed = "connection_failed";
        public const string NotFound = "not_found";
        public const string ConcurrencyConflict = "concurrency_conflict";
        public const string VerificationFailed = "verification_failed";
        public const string PermissionDenied = "permission_denied";
        public const string Unsupported = "unsupported";
    }

    public static class BridgeMethods
    {
        public const string Hello = "hello";
        public const string Connect = "connect";
        public const string TestConnection = "testConnection";
        public const string ListJobs = "listJobs";
        public const string GetJob = "getJob";
        public const string GetJobHistory = "getJobHistory";
        public const string GetJobLogs = "getJobLogs";
        public const string ListNotifications = "listNotifications";
        public const string ListCredentials = "listCredentials";
        public const string PreviewSchedule = "previewSchedule";
        public const string CreateJob = "createJob";
        public const string UpdateJob = "updateJob";
        public const string CloneJob = "cloneJob";
        public const string SetJobActive = "setJobActive";
        /// <summary>Integration-test cleanup only: deletes a job whose name starts with <see cref="TestJobPrefix"/>.
        /// Not exposed as an MCP tool.</summary>
        public const string DeleteTestJob = "deleteTestJob";
        public const string TestJobPrefix = "VCAI_IT_";
        public const string Shutdown = "shutdown";
    }

    // ---- parameter / result payloads ----

    public sealed class HelloResult
    {
        public string BridgeVersion { get; set; } = "";
        public string VisualCronApiVersion { get; set; } = "";
        public string VisualCronInstallDir { get; set; } = "";
        public string ClrVersion { get; set; } = "";
    }

    public static class AuthModes
    {
        /// <summary>Windows / Active Directory logon using the identity running the bridge. No stored password.</summary>
        public const string ActiveDirectory = "AD";
        /// <summary>VisualCron user; password read by the bridge from Windows Credential Manager.</summary>
        public const string CredentialManager = "CredentialManager";
    }

    public sealed class ConnectParams
    {
        /// <summary>Logical environment name, e.g. DEV. Used as the connection key inside the bridge.</summary>
        public string Environment { get; set; } = "";
        public string Address { get; set; } = "";
        public int Port { get; set; } = 16444;
        public string AuthMode { get; set; } = AuthModes.ActiveDirectory;
        /// <summary>Generic credential target name in Windows Credential Manager (CredentialManager mode only).</summary>
        public string CredentialTarget { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 30;
    }

    public sealed class ServerInfoDto
    {
        public string Environment { get; set; } = "";
        public string Address { get; set; } = "";
        public int Port { get; set; }
        public bool Connected { get; set; }
        public string ServerVersion { get; set; } = "";
        public string TimeZoneName { get; set; } = "";
        public int UtcOffsetMinutes { get; set; }
        public DateTime ServerTime { get; set; }
        public string AuthMode { get; set; } = "";
        public int JobCount { get; set; }
    }

    public class EnvParams
    {
        public string Environment { get; set; } = "";
    }

    public sealed class ListJobsParams : EnvParams
    {
        public string? NameContains { get; set; }
        public string? Group { get; set; }
    }

    public sealed class JobIdParams : EnvParams
    {
        public string JobId { get; set; } = "";
    }

    public sealed class JobHistoryParams : EnvParams
    {
        public string JobId { get; set; } = "";
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public bool FailedOnly { get; set; }
        public int MaxEntries { get; set; } = 50;
    }

    public sealed class JobLogsParams : EnvParams
    {
        public string JobId { get; set; } = "";
        /// <summary>Empty = most recent execution.</summary>
        public string ExecutionId { get; set; } = "";
        public int MaxCharsPerStream { get; set; } = 4000;
        public int LookbackDays { get; set; } = 7;
    }

    public sealed class PreviewScheduleParams : EnvParams
    {
        public JobDto Job { get; set; } = new JobDto();
        public int Count { get; set; } = 5;
    }

    public sealed class SchedulePreviewDto
    {
        public string ServerTimeZone { get; set; } = "";
        public List<TriggerPreviewDto> Triggers { get; set; } = new List<TriggerPreviewDto>();
        /// <summary>Next executions for the whole job in server-local time, computed by VisualCron's own TimeAPI.</summary>
        public List<DateTime> NextExecutionsServerTime { get; set; } = new List<DateTime>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class TriggerPreviewDto
    {
        public string TriggerId { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public sealed class CreateJobParams : EnvParams
    {
        public JobDto Job { get; set; } = new JobDto();
    }

    public sealed class UpdateJobParams : EnvParams
    {
        public JobDto Job { get; set; } = new JobDto();
        /// <summary>Version token the change was prepared against. The bridge rejects the write if the job changed since.</summary>
        public string ExpectedVersionToken { get; set; } = "";
    }

    public sealed class CloneJobParams
    {
        public string SourceEnvironment { get; set; } = "";
        public string SourceJobId { get; set; } = "";
        public string ExpectedSourceVersionToken { get; set; } = "";
        public string TargetEnvironment { get; set; } = "";
        /// <summary>Desired state of the clone for all modeled fields (name, group, schedules, modeled task settings).
        /// Task IDs in this DTO refer to the *source* task IDs; unmodeled task settings are copied verbatim.</summary>
        public JobDto Overrides { get; set; } = new JobDto();
    }

    public sealed class SetJobActiveParams : EnvParams
    {
        public string JobId { get; set; } = "";
        public bool Active { get; set; }
        public string ExpectedVersionToken { get; set; } = "";
    }

    public sealed class WriteResultDto
    {
        public JobDto Job { get; set; } = new JobDto();
        /// <summary>True when the post-write read-back matched the requested state.</summary>
        public bool Verified { get; set; }
        public List<string> Notes { get; set; } = new List<string>();
    }
}
