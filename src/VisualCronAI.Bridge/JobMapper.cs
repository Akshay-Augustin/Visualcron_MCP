using System;
using System.Collections.Generic;
using System.Linq;
using VisualCron;
using VisualCronAPI;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge
{
    /// <summary>Maps vendor JobClass/TaskClass/TriggerClass objects to and from the shared DTOs.</summary>
    public static class JobMapper
    {
        public static readonly HashSet<TaskClass.TaskT> EditableTaskTypes = new HashSet<TaskClass.TaskT>
        {
            TaskClass.TaskT.Execute,
            TaskClass.TaskT.PowerShell,
            TaskClass.TaskT.HTTP,
            TaskClass.TaskT.JSONFilter,
            TaskClass.TaskT.Wait,
        };

        /// <summary>
        /// VisualCron returns some timestamps as machine-local (Kind=Local) and others as server-local (Unspecified).
        /// Normalize everything to server-local time so the AI and the user see what the VisualCron client shows.
        /// </summary>
        internal static DateTime? ServerTime(DateTime d, Server? server)
        {
            if (d == DateTime.MinValue || d.Year < 1900) return null;
            if (server == null || d.Kind == DateTimeKind.Unspecified) return DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
            TimeSpan offset;
            try { offset = server.UTCTimeOffset; } catch { return DateTime.SpecifyKind(d, DateTimeKind.Unspecified); }
            return DateTime.SpecifyKind(d.ToUniversalTime() + offset, DateTimeKind.Unspecified);
        }

        private static readonly HashSet<InternalResultObjectClass.InternalEventT> JobEvents = new HashSet<InternalResultObjectClass.InternalEventT>
        {
            InternalResultObjectClass.InternalEventT.JobCompletedSuccessfully,
            InternalResultObjectClass.InternalEventT.JobCompletedFailed,
            InternalResultObjectClass.InternalEventT.JobCompleted,
            InternalResultObjectClass.InternalEventT.JobStarted,
        };

        // ------------------------------------------------------------------ read

        public static JobDto ToDto(JobClass j, Server? server)
        {
            var dto = new JobDto
            {
                Id = j.Id ?? "",
                Name = j.Name ?? "",
                Group = j.Group ?? "",
                Description = j.Description ?? "",
                Active = j.Stats != null && j.Stats.Active,
                RunTasksInOrder = j.RunTasksInOrder,
                NotStartIfRunning = j.NotStartIfRunning,
                QueueJobs = j.QueueJobs,
                Timeout = ToDto(j.TimeOut),
                ErrorNotificationIds = Copy(j.ErrorNotifications),
                CompleteNotificationIds = Copy(j.CompleteNotifications),
                StartNotificationIds = Copy(j.StartNotifications),
                ConditionSetIds = Copy(j.Conditions),
            };
            if (j.Tasks != null)
                dto.Tasks = j.Tasks.OrderBy(t => t.Order).Select(t => ToDto(t, server)).ToList();
            if (j.Triggers != null)
                dto.Triggers = j.Triggers.Select(t => ToDto(t, ServerOffset(server))).ToList();
            if (j.Stats != null)
            {
                dto.Stats = new JobStatsDto
                {
                    Status = j.Stats.Status.ToString(),
                    LastExecution = ServerTime(j.Stats.DateLastExecution, server),
                    NextExecution = ServerTime(j.Stats.DateNextExecution, server),
                    LastExitCode = j.Stats.ExitCode,
                    LastResult = j.Stats.ExitCodeResult.ToString(),
                    ExecutionCount = j.Stats.NoExecutes,
                    DateCreated = ServerTime(j.Stats.DateCreated, server),
                    DateModified = ServerTime(j.Stats.DateModified, server),
                    CreatedBy = j.CreatedBy ?? "",
                    ModifiedBy = j.ModifiedBy ?? "",
                };
            }
            dto.VersionToken = VersionToken.Compute(dto);
            if (server != null) dto.NextRunsServerTime = NextRuns(j, server, 5);
            // Names only — job variables commonly hold secrets (e.g. clientsecret), so values never leave the bridge.
            try { dto.VariableNames = (j.Variables ?? new List<JobVariableClass>()).Select(v => v.Key ?? "").OrderBy(k => k).ToList(); } catch { }
            return dto;
        }

        /// <summary>Next runs of the job's REAL triggers (any trigger type VisualCron can schedule), via TimeAPI.</summary>
        internal static List<DateTime> NextRuns(JobClass j, Server server, int count)
        {
            var result = new List<DateTime>();
            try
            {
                if (j.Stats != null && !j.Stats.Active) return result;
                DateTime now = server.ServerTime;
                var offset = ServerOffset(server);
                // TimeAPI works on raw wall-clock trigger times; shift each trigger's results into server time.
                foreach (var tr in (j.Triggers ?? new List<TriggerClass>()).Where(t => t.Active && t.TriggerType == TriggerClass.TriggerT.TimeType))
                {
                    var single = new JobClass();
                    single.Triggers.Add(tr);
                    var shift = TriggerShift(tr, offset);
                    var runs = TimeAPI.GetNextExecutions(single, now - shift, now - shift + TimeSpan.FromDays(40), TimeSpan.Zero) ?? new List<DateTime>();
                    result.AddRange(runs.Select(d => DateTime.SpecifyKind(d + shift, DateTimeKind.Unspecified)));
                }
                result = result.Where(d => d > now).Distinct().OrderBy(d => d).Take(count).ToList();

                // Cross-check against the server's own next execution; if they disagree, trust the server.
                var serverNext = j.Stats == null ? null : ServerTime(j.Stats.DateNextExecution, server);
                if (serverNext != null && result.Count > 0 && Math.Abs((result[0] - serverNext.Value).TotalMinutes) > 1)
                    return new List<DateTime> { serverNext.Value };
                if (result.Count == 0 && serverNext != null && serverNext.Value > now) result.Add(serverNext.Value);
                return result;
            }
            catch { return result; }
        }

        public static JobSummaryDto ToSummary(JobClass j, Server? server = null) => new JobSummaryDto
        {
            Id = j.Id ?? "",
            Name = j.Name ?? "",
            Group = j.Group ?? "",
            Active = j.Stats != null && j.Stats.Active,
            Status = j.Stats?.Status.ToString() ?? "",
            LastExecution = j.Stats == null ? null : ServerTime(j.Stats.DateLastExecution, server),
            NextExecution = j.Stats == null ? null : ServerTime(j.Stats.DateNextExecution, server),
            LastExitCode = j.Stats?.ExitCode ?? 0,
            LastResult = j.Stats?.ExitCodeResult.ToString() ?? "",
            TaskCount = j.Tasks?.Count ?? 0,
            TriggerCount = j.Triggers?.Count ?? 0,
        };

        public static TaskDto ToDto(TaskClass t, Server? server)
        {
            var dto = new TaskDto
            {
                Id = t.Id ?? "",
                Name = t.Name ?? "",
                Description = t.Description ?? "",
                Order = t.Order,
                Active = t.Stats == null || t.Stats.Active,
                TaskType = t.TaskType.ToString(),
                Editable = EditableTaskTypes.Contains(t.TaskType),
                Timeout = ToDto(t.TimeOut),
                ErrorNotificationIds = Copy(t.ErrorNotifications),
            };
            if (t.Flow != null) dto.Flows = t.Flow.Select(ToDto).ToList();

            if (t.TaskType == TaskClass.TaskT.Execute && t.Execute != null)
            {
                dto.Execute = new ExecuteSettingsDto
                {
                    CmdLine = t.Execute.CmdLine ?? "",
                    Arguments = t.Execute.Arguments ?? "",
                    WorkingDirectory = t.Execute.UseWorkingDirectory ? (t.Execute.WorkingDirectory ?? "") : "",
                    CredentialId = t.Execute.NetworkCredential ?? "",
                    RunHidden = t.Execute.RunHidden,
                };
            }
            else if (t.TaskType == TaskClass.TaskT.PowerShell && t.PowerShell != null)
            {
                dto.PowerShell = new PowerShellSettingsDto
                {
                    ScriptSource = t.PowerShell.ScriptSource.ToString(),
                    File = t.PowerShell.File ?? "",
                    Text = t.PowerShell.Text ?? "",
                    WorkingDirectory = t.PowerShell.WorkingDirectory ?? "",
                    CredentialId = t.PowerShell.Credential ?? "",
                };
            }

            else if (t.TaskType == TaskClass.TaskT.HTTP && t.HTTP != null)
            {
                dto.Http = TaskSettingsMapper.ToDto(t.HTTP);
            }
            else if (t.TaskType == TaskClass.TaskT.JSONFilter && t.JSONFilter != null)
            {
                dto.JsonFilter = TaskSettingsMapper.ToDto(t.JSONFilter);
            }
            else if (t.TaskType == TaskClass.TaskT.Wait && t.Wait != null)
            {
                dto.Wait = TaskSettingsMapper.ToDto(t.Wait);
            }
            dto.OutputErrorRule = TaskSettingsMapper.ToDto(t.OnError);
            if (t.Stats != null)
            {
                string output = "";
                try
                {
                    var bytes = t.Stats.StandardOutput;
                    if (bytes != null && bytes.Length > 0) output = System.Text.Encoding.UTF8.GetString(bytes);
                    // Live output can hold tokens (e.g. the OAuth response); only short, non-token-like values are exposed.
                    if (output.Length > 200 || System.Text.RegularExpressions.Regex.IsMatch(output, @"[A-Za-z0-9_\-+=]{40,}")
                        || System.Text.RegularExpressions.Regex.IsMatch(output, @"(?i)token|secret|password|sig="))
                        output = "(withheld: " + output.Length + " chars; see job logs)";
                }
                catch { }
                dto.Runtime = new TaskRuntimeDto
                {
                    Status = t.Stats.Status.ToString(),
                    Executions = t.Stats.NoExecutes,
                    LastExecution = ServerTime(t.Stats.DateLastExecution, server),
                    LastExitCode = t.Stats.ExitCode,
                    LastResult = t.Stats.ExitCodeResult.ToString(),
                    LastOutput = output,
                };
            }

            if (server != null)
            {
                try { dto.Summary = TaskSettingsMapper.CleanSummary(server.Jobs.Tasks.GetTaskAutoDescription(t)); } catch { /* best effort */ }
            }
            return dto;
        }

        public static FlowDto ToDto(FlowClass f) => new FlowDto
        {
            Id = f.Id ?? "",
            EventType = f.EventType.ToString(),
            ActionType = f.ActionType.ToString(),
            RetryTimes = f.RetryTimes,
            RetryDelayMinutes = f.Minutes,
            RetryDelaySeconds = f.Seconds,
            RetryFailAction = f.RetryFailAction.ToString(),
            TaskId = f.TaskId ?? "",
            JobId = f.JobId ?? "",
            NotificationId = f.NotificationId ?? "",
            Condition = f.EventCondition.ToString(),
            ConditionType = f.ConditionType.ToString(),
            // VisualCron compares the output against ConditionValue2 (verified in the VisualCron client:
            // a value stored only in ConditionValue1 is shown as "regex match String.Empty").
            ConditionValue = f.ConditionValue2 ?? "",
            ConditionValue2 = string.IsNullOrEmpty(f.ConditionValue1) ? null : f.ConditionValue1,
        };

        internal static TimeSpan? ServerOffset(Server? server)
        {
            if (server == null) return null;
            try { return server.UTCTimeOffset; } catch { return null; }
        }

        /// <summary>
        /// VisualCron stores trigger times as wall-clock time in the trigger's UTCOffset (the offset of the client that
        /// created it) and the server converts them to its own time zone. Returns server offset − trigger offset.
        /// </summary>
        internal static TimeSpan TriggerShift(TriggerClass tr, TimeSpan? serverOffset)
        {
            if (serverOffset == null || !tr.IsUTCOffsetSet) return TimeSpan.Zero;
            return serverOffset.Value - TimeSpan.FromTicks(tr.UTCOffsetTicks);
        }

        /// <summary>Stamps a trigger with the server's offset so its times are interpreted as server-local time.</summary>
        internal static void StampServerOffset(TriggerClass tr, TimeSpan? serverOffset)
        {
            if (serverOffset == null) return;
            tr.UTCOffsetTicks = serverOffset.Value.Ticks;
            tr.IsUTCOffsetSet = true;
        }

        public static TriggerDto ToDto(TriggerClass tr) => ToDto(tr, null);

        public static TriggerDto ToDto(TriggerClass tr, TimeSpan? serverOffset)
        {
            var dto = new TriggerDto { Id = tr.Id ?? "", Active = tr.Active, Description = tr.Description };
            if (tr.TriggerType == TriggerClass.TriggerT.TimeType)
            {
                dto.Kind = TriggerKinds.Schedule;
                dto.Schedule = ScheduleMapper.Shift(ScheduleMapper.ToDto(tr.TTime), TriggerShift(tr, serverOffset));
                dto.Editable = dto.Schedule.Mode != ScheduleModes.Unmodeled;
                if (string.IsNullOrWhiteSpace(dto.Description)) dto.Description = DescribeTime(tr.TTime, dto.Schedule);
                return dto;
            }

            var ev = tr.TEvent;
            if (ev != null && ev.EventType == EventClass.EventTy.VisualCron && ev.VisualCronEvent != null)
            {
                var vce = ev.VisualCronEvent;
                var types = vce.InternalEventTypes ?? new List<InternalResultObjectClass.InternalEventT>();
                if (!string.IsNullOrEmpty(vce.JobId) && types.Count == 1 && JobEvents.Contains(types[0]))
                {
                    dto.Kind = TriggerKinds.JobEvent;
                    dto.JobEvent = new JobEventDto
                    {
                        UpstreamJobId = vce.JobId,
                        UpstreamJobName = vce.JobName ?? "",
                        Condition = types[0].ToString(),
                    };
                    if (string.IsNullOrWhiteSpace(dto.Description))
                        dto.Description = "When job '" + vce.JobName + "' raises " + types[0];
                    return dto;
                }
            }

            dto.Kind = TriggerKinds.Other;
            dto.Editable = false;
            if (string.IsNullOrWhiteSpace(dto.Description))
                dto.Description = "Event trigger: " + (ev?.EventType.ToString() ?? "unknown");
            return dto;
        }

        public static string DescribeTime(TimeClass tt, ScheduleDto? mapped)
        {
            try
            {
                string d = TimeAPI.GetDescription(tt, DateTime.MaxValue);
                if (!string.IsNullOrWhiteSpace(d) && d.IndexOf("Unhandled error", StringComparison.OrdinalIgnoreCase) < 0) return d;
            }
            catch { /* VisualCron 12 throws for weekday triggers; fall back below */ }
            return mapped != null ? ScheduleMapper.Describe(mapped) : "Time trigger";
        }

        private static TimeoutDto? ToDto(TimeOutClass? t) => t == null ? null : new TimeoutDto
        {
            Enabled = t.Use,
            Hours = t.Hours,
            Minutes = t.Minutes,
            Seconds = t.Seconds,
            ConsiderError = t.ConsiderError,
        };

        // ------------------------------------------------------------------ write

        /// <summary>Applies job-level modeled fields. Unmodeled fields on <paramref name="j"/> are left untouched.</summary>
        public static void ApplyJobFields(JobDto d, JobClass j)
        {
            j.Name = d.Name;
            j.Group = string.IsNullOrWhiteSpace(d.Group) ? j.Group : d.Group;
            j.Description = d.Description ?? "";
            j.RunTasksInOrder = d.RunTasksInOrder;
            j.NotStartIfRunning = d.NotStartIfRunning;
            j.QueueJobs = d.QueueJobs;
            if (d.Timeout != null)
            {
                if (j.TimeOut == null) j.TimeOut = new TimeOutClass();
                ApplyTimeout(d.Timeout, j.TimeOut);
            }
            j.ErrorNotifications = Copy(d.ErrorNotificationIds);
            j.CompleteNotifications = Copy(d.CompleteNotificationIds);
            j.StartNotifications = Copy(d.StartNotificationIds);
            j.Conditions = Copy(d.ConditionSetIds);
            if (j.Stats == null) j.Stats = new JobStatsClass();
            j.Stats.Active = d.Active;
        }

        /// <summary>
        /// Applies modeled task fields. Generic fields (name, order, flows, timeout, notifications) apply to every task type;
        /// type-specific settings are only written for editable types.
        /// </summary>
        public static void ApplyTask(TaskDto d, TaskClass t, Func<string, string> mapTaskId)
        {
            t.Name = d.Name;
            t.Description = d.Description ?? "";
            if (t.Stats == null) t.Stats = new TaskStatsClass();
            t.Stats.Active = d.Active;
            if (d.Timeout != null)
            {
                if (t.TimeOut == null) t.TimeOut = new TimeOutClass();
                ApplyTimeout(d.Timeout, t.TimeOut);
            }
            t.ErrorNotifications = Copy(d.ErrorNotificationIds);
            ApplyFlows(d.Flows, t, mapTaskId);
            if (d.OutputErrorRule != null) TaskSettingsMapper.Apply(d.OutputErrorRule, t);

            if (!EditableTaskTypes.Contains(t.TaskType)) return;

            if (t.TaskType == TaskClass.TaskT.HTTP)
            {
                var hd = d.Http ?? throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Task '" + d.Name + "' is an HTTP task but has no HTTP settings.");
                if (t.HTTP == null) t.CreateTaskTypeObject();
                TaskSettingsMapper.Apply(hd, t.HTTP ?? throw new BridgeException(BridgeErrorCodes.Internal, "No HTTP settings object."), d.Name);
                return;
            }
            if (t.TaskType == TaskClass.TaskT.JSONFilter)
            {
                var jd = d.JsonFilter ?? throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Task '" + d.Name + "' is a JSON filter task but has no JsonFilter settings.");
                if (t.JSONFilter == null) t.CreateTaskTypeObject();
                TaskSettingsMapper.Apply(jd, t.JSONFilter ?? throw new BridgeException(BridgeErrorCodes.Internal, "No JSON filter settings object."));
                return;
            }
            if (t.TaskType == TaskClass.TaskT.Wait)
            {
                var wd = d.Wait ?? throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Task '" + d.Name + "' is a Wait task but has no Wait settings.");
                if (t.Wait == null) t.CreateTaskTypeObject();
                TaskSettingsMapper.Apply(wd, t.Wait ?? throw new BridgeException(BridgeErrorCodes.Internal, "No Wait settings object."));
                return;
            }

            if (t.TaskType == TaskClass.TaskT.Execute)
            {
                var e = d.Execute ?? throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Task '" + d.Name + "' is an Execute task but has no Execute settings.");
                if (t.Execute == null) t.CreateTaskTypeObject();
                var x = t.Execute ?? throw new BridgeException(BridgeErrorCodes.Internal, "VisualCron did not create Execute settings for task '" + d.Name + "'.");
                x.CmdLine = e.CmdLine;
                x.Arguments = e.Arguments ?? "";
                x.UseWorkingDirectory = !string.IsNullOrWhiteSpace(e.WorkingDirectory);
                x.WorkingDirectory = e.WorkingDirectory ?? "";
                x.NetworkCredential = e.CredentialId ?? "";
                x.RunHidden = e.RunHidden;
            }
            else if (t.TaskType == TaskClass.TaskT.PowerShell)
            {
                var p = d.PowerShell ?? throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Task '" + d.Name + "' is a PowerShell task but has no PowerShell settings.");
                if (t.PowerShell == null) t.CreateTaskTypeObject();
                var ps = t.PowerShell ?? throw new BridgeException(BridgeErrorCodes.Internal, "VisualCron did not create PowerShell settings for task '" + d.Name + "'.");
                ps.ScriptSource = ParseEnum<TaskPowerShellClass.ScriptSourceT>(p.ScriptSource, "ScriptSource");
                ps.File = p.File ?? "";
                ps.Text = p.Text ?? "";
                ps.WorkingDirectory = p.WorkingDirectory ?? "";
                ps.Credential = p.CredentialId ?? "";
            }
        }

        private static void ApplyFlows(List<FlowDto> desired, TaskClass t, Func<string, string> mapTaskId)
        {
            if (desired == null) return;
            var existing = (t.Flow ?? new List<FlowClass>()).ToDictionary(f => f.Id ?? "", f => f);
            var result = new List<FlowClass>();
            foreach (var fd in desired)
            {
                FlowClass f;
                if (!string.IsNullOrEmpty(fd.Id) && existing.TryGetValue(fd.Id, out var found))
                {
                    f = found;
                }
                else
                {
                    f = new FlowClass
                    {
                        Id = Guid.NewGuid().ToString(),
                        EventCondition = FlowClass.FlowEventConditionT.Always,
                        RunFollowingTasks = true,
                    };
                }
                f.EventType = ParseEnum<FlowClass.FlowEventT>(fd.EventType, "Flow.EventType");
                f.ActionType = ParseEnum<FlowClass.FlowActionT>(fd.ActionType, "Flow.ActionType");
                f.RetryTimes = fd.RetryTimes;
                f.Minutes = fd.RetryDelayMinutes;
                f.Seconds = fd.RetryDelaySeconds;
                f.RetryFailAction = ParseEnum<FlowClass.RetryFailActionT>(string.IsNullOrEmpty(fd.RetryFailAction) ? "StopJob" : fd.RetryFailAction, "Flow.RetryFailAction");
                f.TaskId = string.IsNullOrEmpty(fd.TaskId) ? "" : mapTaskId(fd.TaskId);
                f.JobId = fd.JobId ?? "";
                f.NotificationId = fd.NotificationId ?? "";
                f.EventCondition = ParseEnum<FlowClass.FlowEventConditionT>(string.IsNullOrEmpty(fd.Condition) ? "Always" : fd.Condition, "Flow.Condition");
                if (f.EventCondition != FlowClass.FlowEventConditionT.Always)
                {
                    f.ConditionType = ParseEnum<ConditionClass.ConditionCheckT>(string.IsNullOrEmpty(fd.ConditionType) ? "Equal" : fd.ConditionType, "Flow.ConditionType");
                    f.ConditionValue2 = fd.ConditionValue ?? "";          // the value the output is compared with
                    if (f.EventCondition == FlowClass.FlowEventConditionT.IfOutput) f.ConditionValue1 = "";
                    f.ValueType = TypeCode.String;
                }
                result.Add(f);
            }
            t.Flow = result;
        }

        /// <summary>Applies a trigger DTO onto a trigger of the same kind. Returns false when the kinds differ (caller must replace).</summary>
        public static bool ApplyTrigger(TriggerDto d, TriggerClass tr, DateTime serverNow, TimeSpan? serverOffset = null)
        {
            tr.Active = d.Active;
            if (d.Kind == TriggerKinds.Schedule)
            {
                if (tr.TriggerType != TriggerClass.TriggerT.TimeType) return false;
                var desired = d.Schedule ?? throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Schedule trigger without schedule.");
                var current = ScheduleMapper.Shift(ScheduleMapper.ToDto(tr.TTime), TriggerShift(tr, serverOffset));
                if (!SameSchedule(current, desired))
                {
                    tr.TTime = ScheduleMapper.ToTimeClass(desired, serverNow);
                    StampServerOffset(tr, serverOffset);
                    SetFirstRun(tr);
                    tr.AutoDescription = true;
                    tr.AutoRefreshDescription = true;
                    tr.Description = DescribeTime(tr.TTime, desired);
                }
                return true;
            }
            if (d.Kind == TriggerKinds.JobEvent)
            {
                if (tr.TriggerType != TriggerClass.TriggerT.EventType || tr.TEvent == null || tr.TEvent.EventType != EventClass.EventTy.VisualCron) return false;
                ApplyJobEvent(d.JobEvent ?? throw new BridgeException(BridgeErrorCodes.InvalidRequest, "JobEvent trigger without JobEvent."), tr);
                return true;
            }
            // Other: only Active can change.
            return true;
        }

        public static TriggerClass NewTrigger(JobClass j, TriggerDto d, DateTime serverNow, TimeSpan? serverOffset = null)
        {
            TriggerClass tr;
            if (d.Kind == TriggerKinds.Schedule)
            {
                var s = d.Schedule ?? throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Schedule trigger without schedule.");
                tr = j.NewTrigger(s.Mode == ScheduleModes.EveryNMinutes ? TimeClass.TimeTriggerT.Interval : TimeClass.TimeTriggerT.Custom);
                tr.TTime = ScheduleMapper.ToTimeClass(s, serverNow);
                StampServerOffset(tr, serverOffset);
                SetFirstRun(tr);
                tr.AutoDescription = true;
                tr.AutoRefreshDescription = true;
                tr.Description = DescribeTime(tr.TTime, s);
            }
            else if (d.Kind == TriggerKinds.JobEvent)
            {
                tr = j.NewTrigger(EventClass.EventTy.VisualCron);
                ApplyJobEvent(d.JobEvent ?? throw new BridgeException(BridgeErrorCodes.InvalidRequest, "JobEvent trigger without JobEvent."), tr);
            }
            else
            {
                throw new BridgeException(BridgeErrorCodes.Unsupported, "Cannot create triggers of kind '" + d.Kind + "'.");
            }
            tr.Active = d.Active;
            return tr;
        }

        /// <summary>
        /// Interval triggers are computed from TriggerClass.FirstRun (verified with TimeAPI: when FirstRun is unset the
        /// engine counts from the current wall-clock time), so it must match the TimeClass InitDate.
        /// </summary>
        private static void SetFirstRun(TriggerClass tr)
        {
            if (tr.TTime != null && tr.TTime.TimeTriggerType == TimeClass.TimeTriggerT.Interval)
                tr.FirstRun = tr.TTime.InitDate;
        }

        private static void ApplyJobEvent(JobEventDto e, TriggerClass tr)
        {
            if (string.IsNullOrWhiteSpace(e.UpstreamJobId))
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "JobEvent.UpstreamJobId is required.");
            var type = ParseEnum<InternalResultObjectClass.InternalEventT>(e.Condition, "JobEvent.Condition");
            if (!JobEvents.Contains(type))
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Unsupported job event condition: " + e.Condition);
            var vce = tr.TEvent.VisualCronEvent ?? (tr.TEvent.VisualCronEvent = new VisualCronEventClass());
            vce.IsLocalConnection = true;
            vce.JobId = e.UpstreamJobId;
            vce.JobName = e.UpstreamJobName ?? "";
            vce.InternalEventTypes = new List<InternalResultObjectClass.InternalEventT> { type };
            vce.WhenStarted = type == InternalResultObjectClass.InternalEventT.JobStarted;
            vce.WhenCompleted = type != InternalResultObjectClass.InternalEventT.JobStarted;
            tr.Description = "When job '" + e.UpstreamJobName + "' raises " + type;
        }

        public static TaskClass.TaskT ParseTaskType(string name) => ParseEnum<TaskClass.TaskT>(name, "TaskType");

        public static bool SameSchedule(ScheduleDto a, ScheduleDto b)
        {
            if (a.Mode != b.Mode) return false;
            if (a.Mode == ScheduleModes.EveryNMinutes) return a.IntervalMinutes == b.IntervalMinutes;
            return Same(a.Times, b.Times) && Same(a.DaysOfWeek, b.DaysOfWeek, StringComparer.OrdinalIgnoreCase)
                && a.DaysOfMonth.OrderBy(x => x).SequenceEqual(b.DaysOfMonth.OrderBy(x => x));
        }

        private static bool Same(List<string> a, List<string> b, StringComparer? cmp = null)
        {
            cmp = cmp ?? StringComparer.Ordinal;
            return new HashSet<string>(a ?? new List<string>(), cmp).SetEquals(b ?? new List<string>());
        }

        private static void ApplyTimeout(TimeoutDto d, TimeOutClass t)
        {
            t.Use = d.Enabled;
            t.Hours = d.Hours;
            t.Minutes = d.Minutes;
            t.Seconds = d.Seconds;
            t.ConsiderError = d.ConsiderError;
        }

        public static T ParseEnum<T>(string value, string field) where T : struct
        {
            if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out T parsed) && Enum.IsDefined(typeof(T), parsed))
                return parsed;
            throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Invalid value '" + value + "' for " + field + ". Allowed: " + string.Join(", ", Enum.GetNames(typeof(T))));
        }

        private static List<string> Copy(List<string>? list) => list == null ? new List<string>() : list.Where(s => !string.IsNullOrEmpty(s)).ToList();

        private static DateTime? NullIfMin(DateTime d) => d == DateTime.MinValue || d.Year < 1900 ? (DateTime?)null : d;
    }
}
