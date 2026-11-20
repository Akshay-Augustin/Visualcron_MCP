using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualCron;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge
{
    /// <summary>
    /// HTTP / JSON filter / Wait task settings and output-based error rules.
    /// SECURITY: secret-looking HTTP body fields, headers and raw bodies are masked HERE, inside the bridge, so they
    /// never reach the MCP host or the AI model. On write, a masked value means "keep the value stored on the server".
    /// </summary>
    public static class TaskSettingsMapper
    {
        private static readonly Regex SecretKey = new Regex(@"(?i)(secret|password|passwd|pwd|token|apikey|api[_-]?key|assertion|signature|^key$|authorization|cookie)", RegexOptions.Compiled);
        private static readonly Regex TokenLike = new Regex(@"[A-Za-z0-9_\-+=]{40,}", RegexOptions.Compiled);
        private static readonly Regex RawSecret = new Regex(@"(?i)(client_secret|password|passwd|secret|token|apikey|api_key)""?\s*[=:]\s*""?(?!\{)[^\s&""{}]{4,}", RegexOptions.Compiled);

        /// <summary>A VisualCron variable reference ({JOB(...)}, {TASK(...)}, {CREDENTIAL(...)}) is not itself a secret.</summary>
        private static bool IsVariableOnly(string value)
        {
            string v = (value ?? "").Trim();
            if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) v = v.Substring(7).Trim();
            return v.StartsWith("{") && v.EndsWith("}");
        }

        internal static bool ShouldMask(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (IsVariableOnly(value)) return false;
            return SecretKey.IsMatch(key ?? "") || TokenLike.IsMatch(value);
        }

        internal static bool RawBodyHasSecret(string body) =>
            !string.IsNullOrEmpty(body) && (RawSecret.IsMatch(body) || TokenLike.IsMatch(Regex.Replace(body, @"\{[^}]*\}", "")));

        // ------------------------------------------------------------------ HTTP

        public static HttpSettingsDto ToDto(TaskHTTPClass h)
        {
            var dto = new HttpSettingsDto
            {
                Method = h.Method ?? "",
                Url = h.URL ?? "",
                BodyType = h.BodyType.ToString(),
                ContentType = h.ContentTypeHeader ?? "",
                TimeoutSeconds = h.TimeOut,
                AuthType = h.AuthType.ToString(),
                CredentialId = h.NetworkCredential ?? "",
            };
            string raw = h.BodyRawData ?? "";
            if (RawBodyHasSecret(raw)) { dto.BodyRawMasked = true; dto.BodyRaw = ""; }
            else dto.BodyRaw = raw;
            dto.BodyFields = Pairs(h.BodyKeyValuePairs);
            dto.Headers = Pairs(h.Headers);
            return dto;
        }

        private static List<KeyValueDto> Pairs(List<StringKeyValuePair>? pairs) =>
            (pairs ?? new List<StringKeyValuePair>()).Select(p =>
            {
                bool mask = ShouldMask(p.Key, p.Value);
                return new KeyValueDto { Key = p.Key ?? "", Value = mask ? "" : (p.Value ?? ""), Masked = mask };
            }).ToList();

        public static void Apply(HttpSettingsDto d, TaskHTTPClass h, string taskName)
        {
            if (string.IsNullOrWhiteSpace(d.Url)) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "HTTP task '" + taskName + "' needs a URL.");
            h.Method = string.IsNullOrWhiteSpace(d.Method) ? "GET" : d.Method.Trim().ToUpperInvariant();
            h.URL = d.Url.Trim();
            h.BodyType = JobMapper.ParseEnum<TaskHTTPClass.HTTPBodyT>(string.IsNullOrWhiteSpace(d.BodyType) ? "NotSet" : d.BodyType, "Http.BodyType");
            if (!d.BodyRawMasked) h.BodyRawData = d.BodyRaw ?? "";
            else if (string.IsNullOrEmpty(h.BodyRawData))
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "HTTP task '" + taskName + "': masked body cannot be kept because the task has no stored body.");
            h.BodyKeyValuePairs = MergePairs(d.BodyFields, h.BodyKeyValuePairs, taskName, "body field");
            h.Headers = MergePairs(d.Headers, h.Headers, taskName, "header");
            h.ContentTypeHeader = d.ContentType ?? "";
            if (d.TimeoutSeconds > 0) h.TimeOut = d.TimeoutSeconds;
            // AuthType/credentials are not written from DTOs (secrets live only on the server).
        }

        private static List<StringKeyValuePair> MergePairs(List<KeyValueDto> desired, List<StringKeyValuePair>? existing, string taskName, string what)
        {
            var result = new List<StringKeyValuePair>();
            foreach (var d in desired ?? new List<KeyValueDto>())
            {
                string value = d.Value ?? "";
                if (d.Masked)
                {
                    var old = (existing ?? new List<StringKeyValuePair>()).FirstOrDefault(p => string.Equals(p.Key, d.Key, StringComparison.OrdinalIgnoreCase));
                    if (old == null)
                        throw new BridgeException(BridgeErrorCodes.InvalidRequest, "HTTP task '" + taskName + "': masked " + what + " '" + d.Key + "' has no stored value to keep.");
                    value = old.Value;
                }
                result.Add(new StringKeyValuePair { Key = d.Key ?? "", Value = value });
            }
            return result;
        }

        // ------------------------------------------------------------------ JSON filter

        public static JsonFilterSettingsDto ToDto(TaskJsonFilterClass j) => new JsonFilterSettingsDto
        {
            DataSource = j.DataSource.ToString(),
            Variable = j.Variable ?? "",
            FilePath = j.FilePath ?? "",
            JsonPath = j.JSONPath ?? "",
            Engine = j.FilterEngineType.ToString(),
        };

        public static void Apply(JsonFilterSettingsDto d, TaskJsonFilterClass j)
        {
            j.DataSource = JobMapper.ParseEnum<EnumClass.DataSourceT>(string.IsNullOrWhiteSpace(d.DataSource) ? "Variable" : d.DataSource, "JsonFilter.DataSource");
            j.Variable = d.Variable ?? "";
            j.FilePath = d.FilePath ?? "";
            j.JSONPath = d.JsonPath ?? "";
            j.FilterEngineType = JobMapper.ParseEnum<TaskJsonFilterClass.FilterEngineTypeT>(string.IsNullOrWhiteSpace(d.Engine) ? "JSONPath" : d.Engine, "JsonFilter.Engine");
        }

        // ------------------------------------------------------------------ Wait

        public static WaitSettingsDto ToDto(TaskWaitClass w) => new WaitSettingsDto
        {
            Hours = Int(w.Days) * 24 + Int(w.Hours),
            Minutes = Int(w.Minutes),
            Seconds = Int(w.Seconds),
        };

        public static void Apply(WaitSettingsDto d, TaskWaitClass w)
        {
            if (d.Hours < 0 || d.Minutes < 0 || d.Seconds < 0) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Wait values must not be negative.");
            w.Days = "0";
            w.Hours = d.Hours.ToString();
            w.Minutes = d.Minutes.ToString();
            w.Seconds = d.Seconds.ToString();
            w.MilliSeconds = "0";
        }

        private static int Int(string s) => int.TryParse((s ?? "").Trim(), out int v) ? v : 0;

        // ------------------------------------------------------------------ output-based error rule

        public static OutputErrorRuleDto ToDto(OnTaskErrorClass? e)
        {
            if (e == null) return new OutputErrorRuleDto();
            bool enabled = e.RaiseErrorAction == OnTaskErrorClass.RaiseErrorActionT.RaiseError && e.EventCondition == FlowClass.FlowEventConditionT.IfOutput;
            return new OutputErrorRuleDto
            {
                Enabled = enabled,
                ConditionType = enabled ? e.ConditionType.ToString() : "RegExNoMatch",
                Value = enabled ? (e.ConditionValue2 ?? "") : "",   // compared value lives in ConditionValue2 (see FlowClass)
            };
        }

        public static void Apply(OutputErrorRuleDto d, TaskClass t)
        {
            if (t.OnError == null) t.OnError = new OnTaskErrorClass();
            var e = t.OnError;
            bool currentlyOurs = e.RaiseErrorAction == OnTaskErrorClass.RaiseErrorActionT.RaiseError && e.EventCondition == FlowClass.FlowEventConditionT.IfOutput;
            if (d.Enabled)
            {
                e.RaiseErrorAction = OnTaskErrorClass.RaiseErrorActionT.RaiseError;
                e.EventCondition = FlowClass.FlowEventConditionT.IfOutput;
                e.ConditionType = JobMapper.ParseEnum<ConditionClass.ConditionCheckT>(d.ConditionType, "OutputErrorRule.ConditionType");
                e.ConditionValue2 = d.Value ?? "";
                e.ConditionValue1 = "";
                e.ValueType = TypeCode.String;
            }
            else if (currentlyOurs)
            {
                e.RaiseErrorAction = OnTaskErrorClass.RaiseErrorActionT.NotSet;
            }
        }

        /// <summary>VisualCron's auto-description fails for some task types and appends an error; drop that noise.</summary>
        public static string CleanSummary(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s!.IndexOf(", Error setting auto-description", StringComparison.Ordinal);
            string r = i >= 0 ? s.Substring(0, i) : s;
            return Regex.Replace(r, @"^\d+:\s*", "").Trim();
        }
    }
}
