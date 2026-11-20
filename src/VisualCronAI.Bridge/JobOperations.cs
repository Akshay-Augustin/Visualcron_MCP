using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using VisualCron;
using VisualCronAPI;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge
{
    /// <summary>
    /// All reads and writes against a VisualCron server. Every write:
    ///   1. re-reads the job and checks the caller's version token (optimistic concurrency),
    ///   2. mutates a *cloned* vendor object, mapping only modeled fields (unmodeled settings are preserved),
    ///   3. saves it, then reads it back and compares with the requested state,
    ///   4. reconciles tasks/triggers with the targeted Tasks/Triggers APIs if the job-level save did not persist them,
    ///   5. rolls back (restore original / remove created job) if the state still does not match.
    /// </summary>
    internal sealed class JobOperations
    {
        private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(20);
        private readonly VcSessions _sessions;

        public JobOperations(VcSessions sessions) { _sessions = sessions; }

        // ------------------------------------------------------------------ reads

        public List<JobSummaryDto> ListJobs(ListJobsParams p)
        {
            var s = _sessions.Get(p.Environment);
            IEnumerable<JobClass> jobs = s.Jobs.GetAll(true) ?? new List<JobClass>();
            if (!string.IsNullOrWhiteSpace(p.NameContains))
                jobs = jobs.Where(j => (j.Name ?? "").IndexOf(p.NameContains, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(p.Group))
                jobs = jobs.Where(j => string.Equals(j.Group, p.Group, StringComparison.OrdinalIgnoreCase));
            return jobs.Select(j => JobMapper.ToSummary(j, s)).OrderBy(j => j.Group).ThenBy(j => j.Name).ToList();
        }

        public JobDto GetJob(JobIdParams p)
        {
            var s = _sessions.Get(p.Environment);
            return JobMapper.ToDto(GetClone(s, p.JobId), s);
        }

        public List<JobHistoryEntryDto> GetJobHistory(JobHistoryParams p)
        {
            var s = _sessions.Get(p.Environment);
            GetClone(s, p.JobId); // existence check
            DateTime from = ToServerLocal(s, p.FromUtc), to = ToServerLocal(s, p.ToUtc);
            var rows = s.Log.GetJobHistory(p.JobId, from, to, p.FailedOnly, false, CancellationToken.None) ?? new List<LogJobHistoryClass>();
            return rows.OrderByDescending(r => r.Started).Take(Clamp(p.MaxEntries, 1, 500)).Select(r => new JobHistoryEntryDto
            {
                ExecutionId = r.ExecutionId.ToString(),
                Started = r.Started,
                ExecutionSeconds = Math.Round(r.ExecutionTime, 2),
                ExitCode = r.ExitCode,
                Result = r.ExitCodeResult.ToString(),
            }).ToList();
        }

        public List<TaskLogEntryDto> GetJobLogs(JobLogsParams p)
        {
            var s = _sessions.Get(p.Environment);
            var job = GetClone(s, p.JobId);
            // Generous upper bound: VisualCron interprets these DateTimes with a time-zone shift, and a tight window
            // ("now + 5 min") excluded the most recent run. Results are filtered by execution ID anyway.
            DateTime to = s.ServerTime.AddDays(2), from = s.ServerTime.AddDays(-Clamp(p.LookbackDays, 1, 90));

            Guid executionId;
            if (string.IsNullOrWhiteSpace(p.ExecutionId))
            {
                var latest = (s.Log.GetJobHistory(p.JobId, from, to) ?? new List<LogJobHistoryClass>()).OrderByDescending(r => r.Started).FirstOrDefault();
                if (latest == null) return new List<TaskLogEntryDto>();
                executionId = latest.ExecutionId;
            }
            else if (!Guid.TryParse(p.ExecutionId, out executionId))
            {
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "ExecutionId must be a GUID.");
            }

            int max = Clamp(p.MaxCharsPerStream, 200, 1000000); // host redacts before truncating to its own limit
            var result = new List<TaskLogEntryDto>();
            foreach (var task in job.Tasks.OrderBy(t => t.Order))
            {
                var runs = s.Log.GetTaskHistory(task.Id, from, to) ?? new List<LogTaskHistoryClass>();
                foreach (var run in runs.Where(r => r.ExecutionId == executionId))
                {
                    var entry = new TaskLogEntryDto
                    {
                        TaskId = task.Id,
                        TaskName = task.Name,
                        ExecutionId = executionId.ToString(),
                        Started = run.Started,
                        ExitCode = run.ExitCode,
                        Result = run.ExitCodeResult.ToString(),
                    };
                    try
                    {
                        var output = s.Log.GetTaskOutput(executionId, task.Id, run.TaskPId);
                        if (output != null)
                        {
                            bool t1, t2;
                            entry.StandardOutput = Tail(output.StandardOutput, max, out t1);
                            entry.StandardError = Tail(output.StandardError, max, out t2);
                            entry.Truncated = t1 || t2;
                        }
                    }
                    catch (Exception ex)
                    {
                        entry.StandardError = "(output unavailable: " + ex.GetBaseException().Message + ")";
                    }
                    result.Add(entry);
                }
            }
            return result;
        }

        public List<NamedRefDto> ListNotifications(EnvParams p)
        {
            var s = _sessions.Get(p.Environment);
            var list = new List<NamedRefDto>();
            foreach (var n in s.Notifications.GetAll() ?? new List<TaskNotificationClass>())
            {
                var t = n.Task;
                if (t == null) continue;
                list.Add(new NamedRefDto { Id = t.Id ?? "", Name = t.Name ?? "", Type = t.TaskType.ToString() });
            }
            return list.OrderBy(n => n.Name).ToList();
        }

        public List<NamedRefDto> ListCredentials(EnvParams p)
        {
            // Only the credential ID and its description are returned — never user names or passwords.
            var s = _sessions.Get(p.Environment);
            var list = new List<NamedRefDto>();
            foreach (var c in s.Credentials.GetAll() ?? new List<NetworkCredentialClass>())
            {
                string name = "";
                try { name = s.Credentials.GetDescription(c) ?? ""; } catch { }
                list.Add(new NamedRefDto { Id = c.Id ?? "", Name = name, Type = "NetworkCredential" });
            }
            return list.OrderBy(c => c.Name).ToList();
        }

        public SchedulePreviewDto PreviewSchedule(PreviewScheduleParams p)
        {
            Server? s = null;
            if (!string.IsNullOrWhiteSpace(p.Environment) && _sessions.IsConnected(p.Environment)) s = _sessions.Get(p.Environment);
            DateTime now = s != null ? s.ServerTime : DateTime.Now;
            return BuildPreview(p.Job, now, s?.TimeZoneName ?? TimeZoneInfo.Local.Id + " (bridge machine; not connected)", p.Count, JobMapper.ServerOffset(s));
        }

        /// <summary>Offline schedule preview using VisualCron's own TimeAPI (no server needed).</summary>
        public static SchedulePreviewDto BuildPreview(JobDto job, DateTime serverNow, string serverTz, int count, TimeSpan? serverOffset = null)
        {
            var preview = new SchedulePreviewDto { ServerTimeZone = serverTz };
            var temp = new JobClass();
            foreach (var t in job.Triggers.Where(t => t.Active))
            {
                if (t.Kind == TriggerKinds.Schedule && t.Schedule != null && t.Schedule.Mode != ScheduleModes.Unmodeled)
                {
                    var tr = JobMapper.NewTrigger(temp, t, serverNow, serverOffset);
                    temp.Triggers.Add(tr);
                    preview.Triggers.Add(new TriggerPreviewDto { TriggerId = t.Id, Description = tr.Description });
                }
                else if (t.Kind == TriggerKinds.JobEvent && t.JobEvent != null)
                {
                    preview.Triggers.Add(new TriggerPreviewDto { TriggerId = t.Id, Description = "Runs when job '" + t.JobEvent.UpstreamJobName + "' raises " + t.JobEvent.Condition });
                }
                else
                {
                    preview.Triggers.Add(new TriggerPreviewDto { TriggerId = t.Id, Description = t.Description ?? "Unmodeled trigger" });
                }
            }
            if (temp.Triggers.Count > 0)
            {
                var next = TimeAPI.GetNextExecutions(temp, serverNow, serverNow.AddDays(62), TimeSpan.Zero) ?? new List<DateTime>();
                preview.NextExecutionsServerTime = next.Where(d => d > serverNow).OrderBy(d => d).Distinct().Take(Clamp(count, 1, 50)).ToList();
                if (preview.NextExecutionsServerTime.Count == 0)
                    preview.Warnings.Add("No executions in the next 62 days.");
            }
            if (!job.Triggers.Any(t => t.Active))
                preview.Warnings.Add("Job has no active triggers; it will only run manually.");
            return preview;
        }

        // ------------------------------------------------------------------ writes

        public WriteResultDto CreateJob(CreateJobParams p)
        {
            var s = _sessions.Get(p.Environment);
            var d = p.Job;
            ValidateForWrite(d, isNew: true);
            var j = BuildNewJob(d, s.ServerTime, JobMapper.ServerOffset(s));
            if (NameTaken(s, j.Name, j.Group))
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "A job named '" + j.Name + "' already exists in group '" + j.Group + "'.");
            BridgeLog.Info("Creating job " + j.Id + " in " + p.Environment);
            var added = s.Jobs.Add(j);
            string newId = added?.Id ?? j.Id;
            var expected = JobMapper.ToDto(j, null);

            try
            {
                var actual = WaitForState(s, newId, expected, null);
                var diffs = JobStateComparer.Differences(expected, actual);
                if (diffs.Count > 0)
                {
                    Reconcile(s, j, actual);
                    actual = WaitForState(s, newId, expected, null);
                    diffs = JobStateComparer.Differences(expected, actual);
                }
                if (diffs.Count > 0) throw new VerificationException(diffs);
                return new WriteResultDto { Job = actual, Verified = true, Notes = { "Job created and verified by read-back." } };
            }
            catch (Exception ex)
            {
                string rollback;
                try { s.Jobs.Remove(newId); rollback = "Rolled back: created job removed."; }
                catch (Exception rex) { rollback = "ROLLBACK FAILED, job " + newId + " may need manual removal: " + rex.GetBaseException().Message; }
                BridgeLog.Error("Create failed for " + newId + ": " + ex.GetBaseException().Message + " | " + rollback);
                throw new BridgeException(ex is VerificationException ? BridgeErrorCodes.VerificationFailed : BridgeErrorCodes.Internal,
                    "Create failed: " + ex.GetBaseException().Message + " " + rollback);
            }
        }

        public WriteResultDto UpdateJob(UpdateJobParams p)
        {
            var s = _sessions.Get(p.Environment);
            var d = p.Job;
            ValidateForWrite(d, isNew: false);

            var original = GetClone(s, d.Id);
            var originalDto = JobMapper.ToDto(original, s);
            CheckVersion(originalDto, p.ExpectedVersionToken);

            var j = GetClone(s, d.Id);
            ApplyDesiredState(j, d, s.ServerTime, JobMapper.ServerOffset(s));
            var expected = JobMapper.ToDto(j, null);

            BridgeLog.Info("Updating job " + j.Id + " in " + p.Environment);
            try
            {
                s.Jobs.Update(j);
                var actual = WaitForState(s, j.Id, expected, originalDto.VersionToken);
                var diffs = JobStateComparer.Differences(expected, actual);
                if (diffs.Count > 0)
                {
                    BridgeLog.Warn("Job-level update did not persist everything; reconciling tasks/triggers. " + string.Join("; ", diffs));
                    Reconcile(s, j, actual);
                    actual = WaitForState(s, j.Id, expected, null);
                    diffs = JobStateComparer.Differences(expected, actual);
                }
                if (diffs.Count > 0) throw new VerificationException(diffs);
                return new WriteResultDto { Job = actual, Verified = true, Notes = { "Job updated and verified by read-back." } };
            }
            catch (Exception ex)
            {
                string rollback = Restore(s, original);
                BridgeLog.Error("Update failed for " + d.Id + ": " + ex.GetBaseException().Message + " | " + rollback);
                throw new BridgeException(ex is VerificationException ? BridgeErrorCodes.VerificationFailed : BridgeErrorCodes.Internal,
                    "Update failed: " + ex.GetBaseException().Message + " " + rollback);
            }
        }

        public WriteResultDto CloneJob(CloneJobParams p)
        {
            var src = _sessions.Get(p.SourceEnvironment);
            var dst = _sessions.Get(p.TargetEnvironment);
            var source = GetClone(src, p.SourceJobId);
            CheckVersion(JobMapper.ToDto(source, src), p.ExpectedSourceVersionToken);

            var o = p.Overrides;
            if (string.IsNullOrWhiteSpace(o.Name)) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Clone needs a name.");
            string targetGroup = string.IsNullOrWhiteSpace(o.Group) ? (source.Group ?? "") : o.Group;
            if (NameTaken(dst, o.Name, targetGroup))
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "A job named '" + o.Name + "' already exists in group '" + targetGroup + "'.");

            var clone = CloneWithNewIds(source, out var taskIdMap, out var triggerIdMap);
            // Overrides reference *source* IDs; translate to the clone's new IDs before applying.
            var translated = TranslateIds(o, taskIdMap, triggerIdMap);
            ApplyDesiredState(clone, translated, dst.ServerTime, JobMapper.ServerOffset(dst));
            try { clone.ResetJobStats(); clone.ResetTaskStats(); } catch { }
            clone.Stats.Active = o.Active;
            foreach (var t in clone.Tasks)
            {
                var od = translated.Tasks.FirstOrDefault(x => x.Id == t.Id);
                if (od != null && t.Stats != null) t.Stats.Active = od.Active;
            }

            var expected = JobMapper.ToDto(clone, null);
            BridgeLog.Info("Cloning job " + p.SourceJobId + " (" + p.SourceEnvironment + ") to " + clone.Id + " (" + p.TargetEnvironment + ")");
            var added = dst.Jobs.Add(clone);
            string newId = added?.Id ?? clone.Id;
            try
            {
                var actual = WaitForState(dst, newId, expected, null);
                var diffs = JobStateComparer.Differences(expected, actual);
                if (diffs.Count > 0)
                {
                    Reconcile(dst, clone, actual);
                    actual = WaitForState(dst, newId, expected, null);
                    diffs = JobStateComparer.Differences(expected, actual);
                }
                if (diffs.Count > 0) throw new VerificationException(diffs);
                var notes = new List<string> { "Clone created and verified by read-back." };
                int unmodeled = clone.Tasks.Count(t => !JobMapper.EditableTaskTypes.Contains(t.TaskType));
                if (unmodeled > 0) notes.Add(unmodeled + " task(s) of types the MCP server does not model were copied verbatim; review their environment-specific settings in the VisualCron client.");
                return new WriteResultDto { Job = actual, Verified = true, Notes = notes };
            }
            catch (Exception ex)
            {
                string rollback;
                try { dst.Jobs.Remove(newId); rollback = "Rolled back: cloned job removed."; }
                catch (Exception rex) { rollback = "ROLLBACK FAILED, job " + newId + " may need manual removal: " + rex.GetBaseException().Message; }
                throw new BridgeException(ex is VerificationException ? BridgeErrorCodes.VerificationFailed : BridgeErrorCodes.Internal,
                    "Clone failed: " + ex.GetBaseException().Message + " " + rollback);
            }
        }

        public WriteResultDto SetJobActive(SetJobActiveParams p)
        {
            var s = _sessions.Get(p.Environment);
            var current = JobMapper.ToDto(GetClone(s, p.JobId), s);
            CheckVersion(current, p.ExpectedVersionToken);
            bool ok = p.Active ? s.Jobs.Activate(p.JobId) : s.Jobs.DeActivate(p.JobId);
            var sw = Stopwatch.StartNew();
            JobDto after;
            do
            {
                after = JobMapper.ToDto(GetClone(s, p.JobId), s);
                if (after.Active == p.Active) break;
                Thread.Sleep(300);
            } while (sw.Elapsed < VerifyTimeout);
            bool verified = after.Active == p.Active;
            if (!verified)
                throw new BridgeException(BridgeErrorCodes.VerificationFailed, "Server reported " + (ok ? "success" : "failure") + " but job active state is still " + after.Active + ".");
            return new WriteResultDto { Job = after, Verified = true, Notes = { "Job " + (p.Active ? "activated" : "deactivated") + "." } };
        }

        /// <summary>Integration-test cleanup. Refuses anything not named with the test prefix.</summary>
        public WriteResultDto DeleteTestJob(JobIdParams p)
        {
            var s = _sessions.Get(p.Environment);
            var j = GetClone(s, p.JobId);
            if (j.Name == null || !j.Name.StartsWith(BridgeMethods.TestJobPrefix, StringComparison.Ordinal))
                throw new BridgeException(BridgeErrorCodes.PermissionDenied, "Only jobs named " + BridgeMethods.TestJobPrefix + "* can be deleted through the bridge.");
            BridgeLog.Info("Deleting integration-test job " + j.Id + " in " + p.Environment);
            bool removed = s.Jobs.Remove(j.Id);
            return new WriteResultDto { Job = JobMapper.ToDto(j, null), Verified = removed, Notes = { removed ? "Deleted." : "Server reported the job was not removed." } };
        }

        // ------------------------------------------------------------------ helpers (internal for tests)

        internal static JobClass BuildNewJob(JobDto d, DateTime serverNow, TimeSpan? serverOffset = null)
        {
            var j = new JobClass();
            var taskIdMap = new Dictionary<string, string>();
            var ordered = d.Tasks.OrderBy(t => t.Order).ToList();
            var pending = new List<KeyValuePair<TaskDto, TaskClass>>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var td = ordered[i];
                var tc = j.NewTask(JobMapper.ParseTaskType(td.TaskType));
                tc.Order = i + 1;
                if (!string.IsNullOrEmpty(td.Id)) taskIdMap[td.Id] = tc.Id;
                j.Tasks.Add(tc);
                pending.Add(new KeyValuePair<TaskDto, TaskClass>(td, tc));
            }
            Func<string, string> map = id => taskIdMap.TryGetValue(id, out var n) ? n : id;
            foreach (var kv in pending) JobMapper.ApplyTask(kv.Key, kv.Value, map);
            foreach (var trd in d.Triggers)
                j.Triggers.Add(JobMapper.NewTrigger(j, trd, serverNow, serverOffset));
            JobMapper.ApplyJobFields(d, j);
            return j;
        }

        /// <summary>Mutates <paramref name="j"/> so its modeled state equals <paramref name="d"/>.</summary>
        internal static void ApplyDesiredState(JobClass j, JobDto d, DateTime serverNow, TimeSpan? serverOffset = null)
        {
            // Tasks
            var desiredIds = new HashSet<string>(d.Tasks.Where(t => !string.IsNullOrEmpty(t.Id)).Select(t => t.Id));
            foreach (var removed in j.Tasks.Where(t => !desiredIds.Contains(t.Id)).Select(t => t.Id).ToList())
                j.DeleteTask(removed);

            var taskIdMap = new Dictionary<string, string>();
            var ordered = d.Tasks.OrderBy(t => t.Order).ToList();
            var pending = new List<KeyValuePair<TaskDto, TaskClass>>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var td = ordered[i];
                var tc = string.IsNullOrEmpty(td.Id) ? null : j.Tasks.FirstOrDefault(t => t.Id == td.Id);
                if (tc == null)
                {
                    tc = j.NewTask(JobMapper.ParseTaskType(td.TaskType));
                    if (!string.IsNullOrEmpty(td.Id)) taskIdMap[td.Id] = tc.Id;
                    j.Tasks.Add(tc);
                }
                else if (!string.Equals(tc.TaskType.ToString(), td.TaskType, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Task '" + td.Name + "' cannot change type from " + tc.TaskType + " to " + td.TaskType + "; remove it and add a new task.");
                }
                tc.Order = i + 1;
                pending.Add(new KeyValuePair<TaskDto, TaskClass>(td, tc));
            }
            Func<string, string> map = id => taskIdMap.TryGetValue(id, out var n) ? n : id;
            foreach (var kv in pending) JobMapper.ApplyTask(kv.Key, kv.Value, map);

            // Triggers
            var desiredTriggerIds = new HashSet<string>(d.Triggers.Where(t => !string.IsNullOrEmpty(t.Id)).Select(t => t.Id));
            foreach (var removed in j.Triggers.Where(t => !desiredTriggerIds.Contains(t.Id)).Select(t => t.Id).ToList())
                j.DeleteTrigger(removed);
            foreach (var trd in d.Triggers)
            {
                var tr = string.IsNullOrEmpty(trd.Id) ? null : j.Triggers.FirstOrDefault(t => t.Id == trd.Id);
                if (tr != null && JobMapper.ApplyTrigger(trd, tr, serverNow, serverOffset)) continue;
                if (tr != null) j.DeleteTrigger(tr.Id); // kind changed: replace
                j.Triggers.Add(JobMapper.NewTrigger(j, trd, serverNow, serverOffset));
            }

            JobMapper.ApplyJobFields(d, j);
        }

        internal static JobClass CloneWithNewIds(JobClass source, out Dictionary<string, string> taskIdMap, out Dictionary<string, string> triggerIdMap)
        {
            var oldTaskIds = source.Tasks.Select(t => t.Id).ToList();
            var oldTriggerIds = source.Triggers.Select(t => t.Id).ToList();
            var clone = (JobClass)source.Clone();
            clone.CreateNewIds();
            taskIdMap = new Dictionary<string, string>();
            triggerIdMap = new Dictionary<string, string>();
            for (int i = 0; i < oldTaskIds.Count && i < clone.Tasks.Count; i++) taskIdMap[oldTaskIds[i]] = clone.Tasks[i].Id;
            for (int i = 0; i < oldTriggerIds.Count && i < clone.Triggers.Count; i++) triggerIdMap[oldTriggerIds[i]] = clone.Triggers[i].Id;
            // GotoTask flows and {TASK(<id>|...)} references must point at the clone's own tasks.
            var map = taskIdMap;
            Func<string, string> fix = s =>
            {
                if (string.IsNullOrEmpty(s)) return s;
                foreach (var kv in map) s = System.Text.RegularExpressions.Regex.Replace(s, System.Text.RegularExpressions.Regex.Escape(kv.Key), kv.Value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return s;
            };
            foreach (var t in clone.Tasks)
            {
                foreach (var f in t.Flow ?? new List<FlowClass>())
                    if (!string.IsNullOrEmpty(f.TaskId) && taskIdMap.TryGetValue(f.TaskId, out var nid)) f.TaskId = nid;
                if (t.HTTP != null)
                {
                    t.HTTP.URL = fix(t.HTTP.URL);
                    t.HTTP.BodyRawData = fix(t.HTTP.BodyRawData);
                    foreach (var p in (t.HTTP.Headers ?? new List<StringKeyValuePair>()).Concat(t.HTTP.BodyKeyValuePairs ?? new List<StringKeyValuePair>())) p.Value = fix(p.Value);
                }
                if (t.JSONFilter != null) { t.JSONFilter.Variable = fix(t.JSONFilter.Variable); t.JSONFilter.FilePath = fix(t.JSONFilter.FilePath); }
                if (t.Execute != null) { t.Execute.CmdLine = fix(t.Execute.CmdLine); t.Execute.Arguments = fix(t.Execute.Arguments); }
                if (t.PowerShell != null) t.PowerShell.Text = fix(t.PowerShell.Text);
            }
            return clone;
        }

        private static JobDto TranslateIds(JobDto o, Dictionary<string, string> taskMap, Dictionary<string, string> triggerMap)
        {
            // IDs are GUIDs, so a textual replace over the whole DTO rewrites task IDs, GotoTask flow targets AND
            // {TASK(<id>|StdOut)} references inside URLs, headers, bodies and JSON-filter inputs in one pass.
            var json = System.Text.Json.JsonSerializer.Serialize(o, BridgeJson.Options);
            foreach (var kv in taskMap.Concat(triggerMap))
                json = System.Text.RegularExpressions.Regex.Replace(json, System.Text.RegularExpressions.Regex.Escape(kv.Key), kv.Value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return System.Text.Json.JsonSerializer.Deserialize<JobDto>(json, BridgeJson.Options)!;
        }

        /// <summary>Second-chance write path using the per-object Tasks/Triggers APIs.</summary>
        private static void Reconcile(Server s, JobClass desired, JobDto actual)
        {
            var actualTaskIds = new HashSet<string>(actual.Tasks.Select(t => t.Id));
            var desiredTaskIds = new HashSet<string>(desired.Tasks.Select(t => t.Id));
            foreach (var t in desired.Tasks)
            {
                t.JobId = desired.Id;
                if (actualTaskIds.Contains(t.Id)) s.Jobs.Tasks.Update(t);
                else s.Jobs.Tasks.Add(t);
            }
            foreach (var extra in actual.Tasks.Where(t => !desiredTaskIds.Contains(t.Id)))
                s.Jobs.Tasks.Remove(desired.Id, extra.Id);

            var actualTriggerIds = new HashSet<string>(actual.Triggers.Select(t => t.Id));
            foreach (var tr in desired.Triggers)
            {
                tr.JobId = desired.Id;
                if (actualTriggerIds.Contains(tr.Id)) s.Jobs.Triggers.Update(desired, tr);
                else s.Jobs.Triggers.Add(tr);
            }
        }

        private static string Restore(Server s, JobClass original)
        {
            try
            {
                s.Jobs.Update(original);
                return "Rolled back: original job definition restored.";
            }
            catch (Exception ex)
            {
                return "ROLLBACK FAILED, restore job " + original.Id + " manually (VisualCron audit log keeps prior versions): " + ex.GetBaseException().Message;
            }
        }

        private static JobDto WaitForState(Server s, string jobId, JobDto expected, string? previousToken)
        {
            var sw = Stopwatch.StartNew();
            JobDto? last = null;
            while (sw.Elapsed < VerifyTimeout)
            {
                var j = s.Jobs.Get(jobId, false, true);
                if (j != null)
                {
                    last = JobMapper.ToDto(j, s);
                    bool changed = previousToken == null || last.VersionToken != previousToken;
                    if (changed && JobStateComparer.Differences(expected, last).Count == 0) return last;
                }
                Thread.Sleep(400);
            }
            if (last == null) throw new VerificationException(new List<string> { "Job " + jobId + " not found on the server after the write." });
            return last;
        }

        /// <summary>VisualCron allows duplicate names; we require uniqueness within a job group (DEV:/UAT:/PROD: groups may share names).</summary>
        private static bool NameTaken(Server s, string name, string group) =>
            (s.Jobs.GetAll() ?? new List<JobClass>()).Any(j =>
                string.Equals((j.Name ?? "").Trim(), (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals((j.Group ?? "").Trim(), (group ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        private static JobClass GetClone(Server s, string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId)) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "JobId is required.");
            var j = s.Jobs.Get(jobId, false, true);
            if (j == null) throw new BridgeException(BridgeErrorCodes.NotFound, "Job " + jobId + " not found.");
            return j;
        }

        private static void CheckVersion(JobDto current, string expected)
        {
            if (string.IsNullOrWhiteSpace(expected))
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "An expected version token is required for writes.");
            if (!string.Equals(current.VersionToken, expected, StringComparison.Ordinal))
                throw new BridgeException(BridgeErrorCodes.ConcurrencyConflict,
                    "Job '" + current.Name + "' changed on the server since the proposal was prepared (expected " + expected + ", found " + current.VersionToken + "). Prepare the change again.");
        }

        internal static void ValidateForWrite(JobDto d, bool isNew)
        {
            if (string.IsNullOrWhiteSpace(d.Name)) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Job name is required.");
            if (!isNew && string.IsNullOrWhiteSpace(d.Id)) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Job id is required for updates.");
            foreach (var t in d.Tasks)
            {
                var type = JobMapper.ParseTaskType(t.TaskType);
                bool isNewTask = isNew || string.IsNullOrEmpty(t.Id);
                if (isNewTask && !JobMapper.EditableTaskTypes.Contains(type))
                    throw new BridgeException(BridgeErrorCodes.Unsupported, "New tasks of type " + type + " are not supported; supported: " + string.Join(", ", JobMapper.EditableTaskTypes));
            }
            foreach (var tr in d.Triggers)
            {
                if (tr.Kind == TriggerKinds.Other && (isNew || string.IsNullOrEmpty(tr.Id)))
                    throw new BridgeException(BridgeErrorCodes.Unsupported, "Cannot create triggers of kind Other.");
            }
        }

        private static DateTime ToServerLocal(Server s, DateTime utc)
        {
            var u = utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime();
            try { return u + s.UTCTimeOffset; } catch { return u.ToLocalTime(); }
        }

        private static string Tail(string? text, int max, out bool truncated)
        {
            text = text ?? "";
            truncated = text.Length > max;
            return truncated ? "…" + text.Substring(text.Length - max) : text;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private sealed class VerificationException : Exception
        {
            public VerificationException(List<string> diffs) : base("Read-back verification failed: " + string.Join("; ", diffs)) { }
        }
    }
}
