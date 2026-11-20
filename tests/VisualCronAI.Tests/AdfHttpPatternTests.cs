using VisualCronAI.Contracts;
using VisualCronAI.Core.Adf;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Tests;

/// <summary>The team's HTTP-task ADF pattern ("Products - Trigger ADF"), in DTO form.</summary>
public class AdfHttpPatternTests
{
    private const string DevFactory = "https://management.azure.com/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/dev-d365-dataload-rg/providers/Microsoft.DataFactory/factories/TestInstance-d365";

    public static JobDto Template(string group = "DEV: ADF-D365 Loads", string pipeline = "s_D365_Products_Blob_DataFlow")
    {
        TaskDto T(string id, int order, string name, string type) => new() { Id = id, Order = order, Name = name, TaskType = type, Editable = true,
            Flows = [new FlowDto { EventType = FlowEvents.OnSuccess, ActionType = FlowActions.ContinueNextTask }, new FlowDto { EventType = FlowEvents.OnError, ActionType = FlowActions.StopJob }] };
        var token = T("11111111-0000-0000-0000-000000000001", 1, "Get Access Token", TaskTypes.Http);
        token.Http = new HttpSettingsDto { Method = "POST", Url = "https://login.microsoftonline.com/{JOB(Active|Variable|tenantid)}/oauth2/v2.0/token", BodyType = "xwwwformurlencoded",
            BodyFields = [new KeyValueDto { Key = "client_secret", Value = "{JOB(Active|Variable|clientsecret)}" }] };
        var tf = T("11111111-0000-0000-0000-000000000002", 2, "JSON filter", TaskTypes.JsonFilter);
        tf.JsonFilter = new JsonFilterSettingsDto { Variable = "{TASK(11111111-0000-0000-0000-000000000001|StdOut)}", JsonPath = "access_token" };
        var create = T("11111111-0000-0000-0000-000000000003", 3, "Trigger Pipeline", TaskTypes.Http);
        create.Http = new HttpSettingsDto { Method = "POST", Url = $"  {DevFactory}/pipelines/{pipeline}/createRun?api-version=2018-06-01", BodyType = "raw", BodyRaw = "{}",
            Headers = [new KeyValueDto { Key = "Authorization", Value = "Bearer {TASK(11111111-0000-0000-0000-000000000002|StdOut)}" }] };
        var rid = T("11111111-0000-0000-0000-000000000004", 4, "Get RunId", TaskTypes.JsonFilter);
        rid.JsonFilter = new JsonFilterSettingsDto { Variable = "{TASK(11111111-0000-0000-0000-000000000003|StdOut)}", JsonPath = "runId" };
        var wait = T("11111111-0000-0000-0000-000000000005", 5, "Wait", TaskTypes.Wait);
        wait.Wait = new WaitSettingsDto { Minutes = 5 };
        var status = T("11111111-0000-0000-0000-000000000006", 6, "Get Pipenline Status", TaskTypes.Http);
        status.Http = new HttpSettingsDto { Method = "GET", Url = $"{DevFactory}/pipelineruns/{{TASK(11111111-0000-0000-0000-000000000004|StdOut)}}?api-version=2018-06-01" };
        var sf = T("11111111-0000-0000-0000-000000000007", 7, "JSON filter 2", TaskTypes.JsonFilter);
        sf.JsonFilter = new JsonFilterSettingsDto { Variable = "{TASK(11111111-0000-0000-0000-000000000006|StdOut)}", JsonPath = "status" };
        return new JobDto
        {
            Name = "Products - Trigger ADF ", Group = group, Active = true,
            Tasks = [token, tf, create, rid, wait, status, sf],
            Triggers = [new TriggerDto { Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Unmodeled }, Editable = false, Description = "Everyday at 12:00:00" }],
        };
    }

    [Fact]
    public void Reads_the_pattern()
    {
        var v = AdfHttpPattern.TryRead(Template())!;
        Assert.Equal("dev-d365-dataload-rg", v.ResourceGroup);
        Assert.Equal("TestInstance-d365", v.FactoryName);
        Assert.Equal("s_D365_Products_Blob_DataFlow", v.PipelineName);
        Assert.Equal(5, v.WaitMinutes);
        Assert.False(v.PollsUntilComplete);
        Assert.Equal("Get Pipenline Status", v.StatusTask!.Name);
        Assert.Equal("JSON filter 2", v.StatusFilterTask!.Name);
    }

    [Fact]
    public void Set_pipeline_parameters_retarget_and_polling()
    {
        var j = Template();
        AdfHttpPattern.SetPipeline(j, "s_D365_Customers_Blob_DataFlow");
        AdfHttpPattern.SetParameters(j, new Dictionary<string, string> { ["LoadDate"] = "{DATEFORMAT(yyyy-MM-dd)}" }, null);
        int n = AdfHttpPattern.Retarget(j, null, "uat-d365-dataload-rg", "UatInstance-d365");
        AdfHttpPattern.ApplyPolling(j, 1, 180);

        var v = AdfHttpPattern.TryRead(j)!;
        Assert.Equal(2, n); // createRun + pipelineruns
        Assert.Equal("s_D365_Customers_Blob_DataFlow", v.PipelineName);
        Assert.Equal("uat-d365-dataload-rg", v.ResourceGroup);
        Assert.Equal("UatInstance-d365", v.FactoryName);
        Assert.Equal("{DATEFORMAT(yyyy-MM-dd)}", v.Parameters["LoadDate"]);
        Assert.True(v.PollsUntilComplete);
        Assert.Equal(1, v.WaitMinutes);
        Assert.Equal(180, j.Timeout!.Hours * 60 + j.Timeout.Minutes);
        Assert.True(j.Timeout.ConsiderError);
        var loop = v.StatusFilterTask!.Flows[0];
        Assert.Equal(FlowActions.GotoTask, loop.ActionType);
        Assert.Equal(v.WaitTask!.Id, loop.TaskId);
        Assert.Contains("login.microsoftonline.com", j.Tasks[0].Http!.Url); // token URL untouched
    }

    private static TestHost SharedHost()
    {
        var h = new TestHost(configure: o =>
        {
            foreach (var (env, rg, f) in new[] { ("DEV", "dev-d365-dataload-rg", "TestInstance-d365"), ("UAT", "uat-d365-dataload-rg", "UatInstance-d365"), ("PROD", "prod-rg", "ProdInstance") })
            {
                var e = o.Environments[env];
                e.Address = "vc-shared"; e.JobGroups = [env + ":*"]; e.WritableJobGroups = [env + ":*"];
                e.EnvironmentTokens = [env + ":"]; e.AdfResourceGroup = rg; e.AdfFactoryName = f; e.AdfTemplateJob = "Products - Trigger ADF";
            }
        });
        h.Gateway.ShareServer("DEV", "UAT", "PROD");
        return h;
    }

    [Fact]
    public async Task Create_adf_job_from_template()
    {
        var h = SharedHost();
        h.Gateway.Jobs["DEV"]["t"] = Template(); h.Gateway.Jobs["DEV"]["t"].Id = "t"; h.Gateway.Jobs["DEV"]["t"].VersionToken = "v1";
        var p = await h.Service.ProposeCreateAdfJobAsync(new CreateAdfJobInput
        {
            Environment = "DEV", Name = "Customers - Trigger ADF", PipelineName = "s_D365_Customers_Blob_DataFlow",
            Schedules = [new ScheduleInput { Times = ["01:30"], TimeZone = "CT" }],
        }, default);

        Assert.True(p.Validation.IsValid, string.Join("; ", p.Validation.Errors));
        Assert.Equal(ProposalOperation.Clone, p.Operation);
        Assert.Equal("t", p.SourceJobId);
        var v = AdfHttpPattern.TryRead(p.After)!;
        Assert.Equal("s_D365_Customers_Blob_DataFlow", v.PipelineName);
        Assert.True(v.PollsUntilComplete);
        Assert.False(p.After.Active);
        Assert.Equal("DEV: ADF-D365 Loads", p.After.Group);
        Assert.Equal(["01:30"], p.After.Triggers.Single().Schedule!.Times);
        Assert.DoesNotContain(p.Validation.Warnings, w => w.Path == "adf.polling");
        Assert.Empty(h.Gateway.Writes);
    }

    [Fact]
    public async Task Clone_dev_to_uat_retargets_factory_and_flags_existing_mismatch()
    {
        var h = SharedHost();
        var dev = Template(); dev.Id = "d"; dev.VersionToken = "v"; h.Gateway.Jobs["DEV"]["d"] = dev;
        var p = await h.Service.ProposeCloneAsync(new CloneInput { SourceEnvironment = "DEV", SourceJob = "Products - Trigger ADF", TargetEnvironment = "UAT", NewName = "Products - Trigger ADF (new)" }, default);
        var v = AdfHttpPattern.TryRead(p.After)!;
        Assert.Equal("uat-d365-dataload-rg", v.ResourceGroup);
        Assert.Equal("UatInstance-d365", v.FactoryName);
        Assert.Equal("UAT: ADF-D365 Loads", p.After.Group);
        Assert.True(p.Validation.IsValid, string.Join("; ", p.Validation.Errors));

        // The current real UAT job points at the DEV factory: validation warns about it.
        const string uatId = "22222222-0000-0000-0000-000000000099";
        var uat = Template("UAT: ADF-D365 Loads"); uat.Id = uatId; uat.VersionToken = "v";
        h.Gateway.Jobs["DEV"][uatId] = uat;
        var (report, _) = await h.Service.ValidateExistingAsync("UAT", uatId, default);
        Assert.Contains(report.Warnings, w => w.Path == "adf.resourceGroup" && w.Message.Contains("dev-d365-dataload-rg"));
        Assert.Contains(report.Warnings, w => w.Path == "adf.polling");
    }

    [Fact]
    public void Repairs_references_copied_from_another_job()
    {
        // As found on RentalItems: status filter reads Devices' task, status call sends another job's token.
        var j = Template();
        var statusFilter = j.Tasks.Single(t => t.Order == 7);
        statusFilter.JsonFilter!.Variable = "{TASK(6c7b4dcb-4759-44fa-8dee-dab2ef6ccfde|StdOut)}";
        var status = j.Tasks.Single(t => t.Order == 6);
        status.Http!.Headers = [new KeyValueDto { Key = "Authorization", Value = "Bearer {TASK(cf8d1a6b-0000-0000-0000-000000000000|StdOut)}" }];

        Assert.Equal(2, AdfHttpPattern.ForeignReferences(j).Count);
        Assert.False(AdfHttpPattern.TryRead(j)!.PollsUntilComplete);

        var notes = AdfHttpPattern.RepairReferences(j);
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Contains("ANOTHER job", n));
        Assert.Empty(AdfHttpPattern.ForeignReferences(j));
        Assert.Equal("{TASK(" + status.Id + "|StdOut)}", statusFilter.JsonFilter.Variable);
        Assert.Equal("Bearer {TASK(11111111-0000-0000-0000-000000000002|StdOut)}", status.Http.Headers[0].Value);
        Assert.Empty(AdfHttpPattern.RepairReferences(j)); // idempotent

        AdfHttpPattern.ApplyPolling(j, 1, 180);
        Assert.True(AdfHttpPattern.TryRead(j)!.PollsUntilComplete);
    }

    [Fact]
    public async Task Fix_polling_on_existing_job()
    {
        var h = SharedHost();
        var dev = Template(); dev.Id = "d"; dev.VersionToken = "v"; h.Gateway.Jobs["DEV"]["d"] = dev;
        var p = await h.Service.ProposeJobChangesAsync("DEV", "Products - Trigger ADF", new JobChangesInput { AdfPollUntilComplete = true }, default);
        Assert.Contains(p.Changes, c => c.Contains("statusCheck") && c.Contains("poll every 1 min"));
        Assert.Contains(p.Changes, c => c.Contains("raiseErrorWhenOutput"));
        Assert.True(AdfHttpPattern.TryRead(p.After)!.PollsUntilComplete);
    }

    [Fact]
    public async Task Retarget_existing_job_to_environment_factory_and_remove_schedules()
    {
        var h = SharedHost();
        var uat = Template("UAT: ADF-D365 Loads"); uat.Id = "u"; uat.VersionToken = "v";
        uat.Triggers = [new TriggerDto { Id = "tr1", Kind = TriggerKinds.Schedule, Schedule = new ScheduleDto { Mode = ScheduleModes.Unmodeled }, Editable = false, Description = "Everyday at 12:00:00", Active = true }];
        h.Gateway.Jobs["DEV"]["u"] = uat;

        var p = await h.Service.ProposeJobChangesAsync("UAT", "Products - Trigger ADF", new JobChangesInput { AdfRetargetToEnvironmentFactory = true, AdfPollUntilComplete = true }, default);
        var v = AdfHttpPattern.TryRead(p.After)!;
        Assert.Equal("uat-d365-dataload-rg", v.ResourceGroup);
        Assert.Equal("UatInstance-d365", v.FactoryName);
        Assert.True(v.PollsUntilComplete);
        Assert.DoesNotContain(p.Validation.Warnings, w => w.Path.StartsWith("adf.resourceGroup") || w.Path.StartsWith("adf.factory"));

        var s = await h.Service.ProposeScheduleAsync("UAT", "Products - Trigger ADF", [], null, true, default);
        Assert.Empty(s.After.Triggers);
        Assert.Empty(h.Gateway.Writes);
    }
}
