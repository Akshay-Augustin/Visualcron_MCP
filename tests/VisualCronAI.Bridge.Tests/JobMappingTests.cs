using System;
using System.Collections.Generic;
using System.Linq;
using VisualCron;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge.Tests
{
    public class JobMappingTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 19, 10, 0, 0);

        internal static JobDto AdfJob() => new JobDto
        {
            Name = "D365_Product_Daily_Load",
            Group = "ADF",
            Description = "Runs p_D365_Product_Load",
            Active = true,
            ErrorNotificationIds = { "11111111-1111-1111-1111-111111111111" },
            Tasks =
            {
                new TaskDto
                {
                    Id = "draft-task-1",
                    Name = "Run ADF pipeline p_D365_Product_Load",
                    Order = 1,
                    TaskType = TaskTypes.Execute,
                    Execute = new ExecuteSettingsDto
                    {
                        CmdLine = @"D:\VisualCronAI\adf-runner\VisualCronAI.AdfRunner.exe",
                        Arguments = "run --env DEV --pipeline p_D365_Product_Load",
                    },
                    Flows =
                    {
                        new FlowDto { EventType = FlowEvents.OnSuccess, ActionType = FlowActions.ContinueNextTask },
                        new FlowDto { EventType = FlowEvents.OnError, ActionType = FlowActions.RetryTask, RetryTimes = 2, RetryDelayMinutes = 5, RetryFailAction = "StopJob" },
                    },
                },
            },
            Triggers =
            {
                new TriggerDto { Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Daily, Times = { "02:00" } } },
                new TriggerDto { Kind = TriggerKinds.JobEvent, JobEvent = new JobEventDto { UpstreamJobId = "22222222-2222-2222-2222-222222222222", UpstreamJobName = "Upstream", Condition = JobEventConditions.CompletedSuccessfully } },
            },
        };

        [Fact]
        public void New_job_round_trips_through_vendor_objects()
        {
            var dto = AdfJob();
            var j = JobOperations.BuildNewJob(dto, Now);

            Assert.True(Guid.TryParse(j.Id, out _));
            Assert.Single(j.Tasks);
            Assert.Equal(TaskClass.TaskT.Execute, j.Tasks[0].TaskType);
            Assert.Equal(j.Id, j.Tasks[0].JobId);
            Assert.Equal(2, j.Triggers.Count);

            var back = JobMapper.ToDto(j, null);
            Assert.Empty(JobStateComparer.Differences(dto, back));

            var retry = back.Tasks[0].Flows.Single(f => f.EventType == FlowEvents.OnError);
            Assert.Equal(FlowActions.RetryTask, retry.ActionType);
            Assert.Equal(2, retry.RetryTimes);
            Assert.Equal(5, retry.RetryDelayMinutes);
            Assert.Equal(TriggerKinds.JobEvent, back.Triggers[1].Kind);
            Assert.Equal(JobEventConditions.CompletedSuccessfully, back.Triggers[1].JobEvent!.Condition);
            Assert.Contains("11111111-1111-1111-1111-111111111111", back.ErrorNotificationIds);
        }

        [Fact]
        public void Update_changes_schedule_adds_and_removes_tasks_and_preserves_unmodeled_task_settings()
        {
            // Existing job with an Execute task and an SQL task (a type the MCP server does not model).
            var j = JobOperations.BuildNewJob(AdfJob(), Now);
            var sql = j.AddTask(TaskClass.TaskT.SQL);
            sql.Name = "Legacy SQL step";
            sql.Order = 2;
            sql.SQL.ConnectionId = "conn-123";
            var before = JobMapper.ToDto(j, null);

            var desired = JobMapper.ToDto(j, null);
            desired.Triggers[0].Schedule!.Times = new List<string> { "03:00" };
            desired.Tasks[1].Name = "Legacy SQL step (renamed)";
            desired.Tasks.Add(new TaskDto
            {
                Name = "Second pipeline", Order = 3, TaskType = TaskTypes.Execute,
                Execute = new ExecuteSettingsDto { CmdLine = "runner.exe", Arguments = "run --pipeline p2" },
            });

            JobOperations.ApplyDesiredState(j, desired, Now);
            var after = JobMapper.ToDto(j, null);

            Assert.Empty(JobStateComparer.Differences(desired, after));
            Assert.Equal("03:00", after.Triggers[0].Schedule!.Times.Single());
            Assert.Equal(3, after.Tasks.Count);
            Assert.Equal("conn-123", j.Tasks.Single(t => t.TaskType == TaskClass.TaskT.SQL).SQL.ConnectionId);
            Assert.NotEqual(before.VersionToken, after.VersionToken);

            // Now remove the SQL task.
            var desired2 = JobMapper.ToDto(j, null);
            desired2.Tasks.RemoveAll(t => t.TaskType == "SQL");
            JobOperations.ApplyDesiredState(j, desired2, Now);
            Assert.DoesNotContain(j.Tasks, t => t.TaskType == TaskClass.TaskT.SQL);
            Assert.Equal(new[] { 1, 2 }, j.Tasks.OrderBy(t => t.Order).Select(t => t.Order).ToArray());
        }

        [Fact]
        public void Changing_task_type_in_place_is_rejected()
        {
            var j = JobOperations.BuildNewJob(AdfJob(), Now);
            var desired = JobMapper.ToDto(j, null);
            desired.Tasks[0].TaskType = TaskTypes.PowerShell;
            desired.Tasks[0].PowerShell = new PowerShellSettingsDto { ScriptSource = "Text", Text = "Write-Output 1" };
            Assert.Throws<BridgeException>(() => JobOperations.ApplyDesiredState(j, desired, Now));
        }

        [Fact]
        public void Replacing_schedule_trigger_with_job_event_replaces_trigger()
        {
            var j = JobOperations.BuildNewJob(AdfJob(), Now);
            var desired = JobMapper.ToDto(j, null);
            var oldId = desired.Triggers[0].Id;
            desired.Triggers[0] = new TriggerDto
            {
                Id = oldId, Kind = TriggerKinds.JobEvent,
                JobEvent = new JobEventDto { UpstreamJobId = "33333333-3333-3333-3333-333333333333", UpstreamJobName = "Accounts", Condition = JobEventConditions.CompletedSuccessfully },
            };
            JobOperations.ApplyDesiredState(j, desired, Now);
            var after = JobMapper.ToDto(j, null);
            Assert.Equal(2, after.Triggers.Count(t => t.Kind == TriggerKinds.JobEvent));
            Assert.DoesNotContain(after.Triggers, t => t.Kind == TriggerKinds.Schedule);
        }

        [Fact]
        public void Clone_gets_new_ids_and_remaps_goto_flows()
        {
            var dto = AdfJob();
            dto.Tasks.Add(new TaskDto
            {
                Id = "draft-task-2", Name = "Cleanup", Order = 2, TaskType = TaskTypes.Execute,
                Execute = new ExecuteSettingsDto { CmdLine = "cleanup.exe" },
            });
            dto.Tasks[0].Flows.Add(new FlowDto { EventType = FlowEvents.OnComplete, ActionType = FlowActions.GotoTask, TaskId = "draft-task-2" });
            var source = JobOperations.BuildNewJob(dto, Now);
            var cleanupId = source.Tasks.Single(t => t.Name == "Cleanup").Id;
            Assert.Equal(cleanupId, source.Tasks[0].Flow.Single(f => f.ActionType == FlowClass.FlowActionT.GotoTask).TaskId);

            var clone = JobOperations.CloneWithNewIds(source, out var taskMap, out var trigMap);

            Assert.NotEqual(source.Id, clone.Id);
            Assert.Equal(2, taskMap.Count);
            Assert.Equal(2, trigMap.Count);
            Assert.All(clone.Tasks, t => Assert.DoesNotContain(source.Tasks, s => s.Id == t.Id));
            var cloneCleanup = clone.Tasks.Single(t => t.Name == "Cleanup");
            Assert.Equal(cloneCleanup.Id, clone.Tasks.Single(t => t.Name != "Cleanup").Flow.Single(f => f.ActionType == FlowClass.FlowActionT.GotoTask).TaskId);
            Assert.Empty(JobStateComparer.Differences(JobMapper.ToDto(source, null), JobMapper.ToDto(clone, null)));
        }

        [Fact]
        public void Version_token_is_stable_and_sensitive_to_changes()
        {
            var j = JobOperations.BuildNewJob(AdfJob(), Now);
            var a = JobMapper.ToDto(j, null);
            var b = JobMapper.ToDto(j, null);
            Assert.Equal(a.VersionToken, b.VersionToken);
            j.Tasks[0].Execute.Arguments += " --param x=1";
            Assert.NotEqual(a.VersionToken, JobMapper.ToDto(j, null).VersionToken);
        }

        [Fact]
        public void New_tasks_of_unmodeled_types_are_rejected()
        {
            var dto = AdfJob();
            dto.Tasks[0].TaskType = "SQL";
            var ex = Assert.Throws<BridgeException>(() => JobOperations.ValidateForWrite(dto, isNew: true));
            Assert.Equal(BridgeErrorCodes.Unsupported, ex.Code);
        }
    }
}
