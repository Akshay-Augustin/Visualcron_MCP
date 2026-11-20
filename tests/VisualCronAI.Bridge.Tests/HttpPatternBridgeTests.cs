using System;
using System.Collections.Generic;
using System.Linq;
using VisualCron;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge.Tests
{
    /// <summary>Mirrors the real "Devices - Trigger ADF" job (7 native tasks) using the installed VisualCron classes.</summary>
    public class HttpPatternBridgeTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 19, 3, 0, 0);
        private static readonly TimeSpan Central = TimeSpan.FromHours(-6);
        private const string Factory = "https://management.azure.com/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/dev-d365-dataload-rg/providers/Microsoft.DataFactory/factories/TestInstance-d365";

        internal static JobClass BuildDevicesLikeJob(string literalSecret = "")
        {
            var j = new JobClass { Name = "Devices - Trigger ADF ", Group = "DEV: ADF-D365 Loads" };
            var token = j.AddTask(TaskClass.TaskT.HTTP); token.Name = "Get Access Token"; token.Order = 1;
            token.HTTP.Method = "POST"; token.HTTP.URL = "https://login.microsoftonline.com/{JOB(Active|Variable|tenantid)}/oauth2/v2.0/token";
            token.HTTP.BodyType = TaskHTTPClass.HTTPBodyT.xwwwformurlencoded;
            token.HTTP.BodyKeyValuePairs = new List<StringKeyValuePair>
            {
                new StringKeyValuePair { Key = "client_id", Value = "{JOB(Active|Variable|clientid)}" },
                new StringKeyValuePair { Key = "client_secret", Value = literalSecret.Length > 0 ? literalSecret : "{JOB(Active|Variable|clientsecret)}" },
                new StringKeyValuePair { Key = "scope", Value = "https://management.azure.com/.default" },
                new StringKeyValuePair { Key = "grant_type", Value = "client_credentials" },
            };
            var tokenFilter = j.AddTask(TaskClass.TaskT.JSONFilter); tokenFilter.Name = "JSON filter"; tokenFilter.Order = 2;
            tokenFilter.JSONFilter.DataSource = EnumClass.DataSourceT.Variable; tokenFilter.JSONFilter.Variable = "{TASK(" + token.Id + "|StdOut)}"; tokenFilter.JSONFilter.JSONPath = "access_token";
            var create = j.AddTask(TaskClass.TaskT.HTTP); create.Name = "Trigger Pipeline"; create.Order = 3;
            create.HTTP.Method = "POST"; create.HTTP.URL = Factory + "/pipelines/s_D365_Devices_Blob_DataFlow/createRun?api-version=2018-06-01";
            create.HTTP.BodyType = TaskHTTPClass.HTTPBodyT.raw; create.HTTP.BodyRawData = "{}";
            create.HTTP.Headers = new List<StringKeyValuePair> { new StringKeyValuePair { Key = "Authorization", Value = "Bearer {TASK(" + tokenFilter.Id + "|StdOut)}" } };
            var runId = j.AddTask(TaskClass.TaskT.JSONFilter); runId.Name = "Get RunId"; runId.Order = 4;
            runId.JSONFilter.Variable = "{TASK(" + create.Id + "|StdOut)}"; runId.JSONFilter.JSONPath = "runId";
            var wait = j.AddTask(TaskClass.TaskT.Wait); wait.Name = "Wait"; wait.Order = 5; wait.Wait.Minutes = "5";
            var status = j.AddTask(TaskClass.TaskT.HTTP); status.Name = "Get Pipenline Status"; status.Order = 6;
            status.HTTP.URL = Factory + "/pipelineruns/{TASK(" + runId.Id + "|StdOut)}?api-version=2018-06-01";
            var statusFilter = j.AddTask(TaskClass.TaskT.JSONFilter); statusFilter.Name = "JSON filter 2"; statusFilter.Order = 7;
            statusFilter.JSONFilter.Variable = "{TASK(" + status.Id + "|StdOut)}"; statusFilter.JSONFilter.JSONPath = "status";
            return j;
        }

        [Fact]
        public void Http_json_wait_settings_round_trip_and_variables_are_not_masked()
        {
            var j = BuildDevicesLikeJob();
            var dto = JobMapper.ToDto(j, null);
            var token = dto.Tasks.Single(t => t.Name == "Get Access Token");
            Assert.True(token.Editable);
            Assert.Equal("xwwwformurlencoded", token.Http!.BodyType);
            Assert.Equal("{JOB(Active|Variable|clientsecret)}", token.Http.BodyFields.Single(f => f.Key == "client_secret").Value);
            Assert.DoesNotContain(token.Http.BodyFields, f => f.Masked);
            Assert.Equal(5, dto.Tasks.Single(t => t.TaskType == "Wait").Wait!.Minutes);
            Assert.Equal("status", dto.Tasks.Single(t => t.Name == "JSON filter 2").JsonFilter!.JsonPath);

            var j2 = JobOperations.BuildNewJob(dto, Now);
            Assert.Empty(JobStateComparer.Differences(dto, JobMapper.ToDto(j2, null)));
        }

        [Fact]
        public void Literal_secrets_are_masked_in_the_bridge_and_kept_on_write()
        {
            var j = BuildDevicesLikeJob(literalSecret: "Abc~8Q~supersecretvalue1234567890");
            var dto = JobMapper.ToDto(j, null);
            var secret = dto.Tasks.Single(t => t.Name == "Get Access Token").Http!.BodyFields.Single(f => f.Key == "client_secret");
            Assert.True(secret.Masked);
            Assert.Equal("", secret.Value);
            Assert.DoesNotContain("supersecret", System.Text.Json.JsonSerializer.Serialize(dto));

            // Change something else and write back: the masked secret must be preserved from the server object.
            dto.Tasks.Single(t => t.TaskType == "Wait").Wait!.Minutes = 1;
            JobOperations.ApplyDesiredState(j, dto, Now);
            var stored = j.Tasks.Single(t => t.Name == "Get Access Token").HTTP.BodyKeyValuePairs.Single(p => p.Key == "client_secret").Value;
            Assert.Equal("Abc~8Q~supersecretvalue1234567890", stored);
            Assert.Equal("1", j.Tasks.Single(t => t.TaskType == TaskClass.TaskT.Wait).Wait.Minutes);
        }

        [Fact]
        public void Clone_rewrites_task_references_to_its_own_tasks()
        {
            var source = BuildDevicesLikeJob();
            var clone = JobOperations.CloneWithNewIds(source, out var map, out _);
            var allText = string.Join("|", clone.Tasks.SelectMany(t => new[]
            {
                t.HTTP?.URL, t.JSONFilter?.Variable, string.Join(",", (t.HTTP?.Headers ?? new List<StringKeyValuePair>()).Select(h => h.Value)),
            }));
            foreach (var oldId in map.Keys) Assert.DoesNotContain(oldId, allText, StringComparison.OrdinalIgnoreCase);
            var cloneStatus = clone.Tasks.Single(t => t.Name == "Get Pipenline Status");
            var cloneRunId = clone.Tasks.Single(t => t.Name == "Get RunId");
            Assert.Contains(cloneRunId.Id, cloneStatus.HTTP.URL);
        }

        [Fact]
        public void Polling_flows_and_output_error_rule_are_written()
        {
            var j = BuildDevicesLikeJob();
            var dto = JobMapper.ToDto(j, null);
            var wait = dto.Tasks.Single(t => t.TaskType == "Wait");
            var filter = dto.Tasks.Single(t => t.Name == "JSON filter 2");
            filter.Flows = new List<FlowDto>
            {
                new FlowDto { EventType = FlowEvents.OnSuccess, ActionType = FlowActions.GotoTask, TaskId = wait.Id, Condition = "IfOutput", ConditionType = "RegExMatch", ConditionValue = "^(Queued|InProgress|Canceling)$" },
                new FlowDto { EventType = FlowEvents.OnSuccess, ActionType = FlowActions.ContinueNextTask },
                new FlowDto { EventType = FlowEvents.OnError, ActionType = FlowActions.StopJob },
            };
            filter.OutputErrorRule = new OutputErrorRuleDto { Enabled = true, ConditionType = "RegExNoMatch", Value = "^(Succeeded|Queued|InProgress|Canceling)$" };
            JobOperations.ApplyDesiredState(j, dto, Now);

            var t = j.Tasks.Single(x => x.Name == "JSON filter 2");
            var loop = t.Flow[0];
            Assert.Equal(FlowClass.FlowActionT.GotoTask, loop.ActionType);
            Assert.Equal(FlowClass.FlowEventConditionT.IfOutput, loop.EventCondition);
            // VisualCron compares output against ConditionValue2 (live finding: Value1-only showed "regex match String.Empty").
            Assert.Equal("^(Queued|InProgress|Canceling)$", loop.ConditionValue2);
            Assert.Equal("", loop.ConditionValue1);
            Assert.Equal(OnTaskErrorClass.RaiseErrorActionT.RaiseError, t.OnError.RaiseErrorAction);
            Assert.Equal(ConditionClass.ConditionCheckT.RegExNoMatch, t.OnError.ConditionType);
            Assert.Equal("^(Succeeded|Queued|InProgress|Canceling)$", t.OnError.ConditionValue2);
            Assert.Empty(JobStateComparer.Differences(dto, JobMapper.ToDto(j, null)));
        }

        [Fact]
        public void New_triggers_are_stamped_with_the_server_offset()
        {
            var dto = JobMapper.ToDto(BuildDevicesLikeJob(), null);
            dto.Triggers.Add(new TriggerDto { Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Daily, Times = { "02:00" } } });
            var j = JobOperations.BuildNewJob(dto, Now, Central);
            var tr = j.Triggers.Single();
            Assert.True(tr.IsUTCOffsetSet);
            Assert.Equal(Central, TimeSpan.FromTicks(tr.UTCOffsetTicks));
        }

        [Fact]
        public void Triggers_created_in_another_zone_are_shown_in_server_time()
        {
            // A trigger created from an IST (+05:30) client at 13:00 fires at 01:30 Central.
            var j = new JobClass();
            var dto = new TriggerDto { Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Daily, Times = { "13:00" } } };
            var tr = JobMapper.NewTrigger(j, dto, Now, TimeSpan.FromHours(5.5));
            var shown = JobMapper.ToDto(tr, Central);
            Assert.Equal(new[] { "01:30" }, shown.Schedule!.Times.ToArray());
        }
    }
}
