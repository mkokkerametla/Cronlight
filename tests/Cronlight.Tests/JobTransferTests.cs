using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Cronlight.Tests;

/// <summary>Exporting jobs to a JSON or YAML file, and importing them from one.</summary>
public class JobTransferTests
{
    private static readonly string Plain = typeof(PlainJob).FullName!;
    private static readonly string Invoice = typeof(InvoiceJob).FullName!;
    private static readonly string Retried = typeof(RetriedJob).FullName!;

    private static Task<HttpResponseMessage> Import(HttpClient client, string file, bool yaml = true, bool dryRun = false) =>
        client.PostAsync($"api/jobs/import?dryRun={dryRun}",
            new StringContent(file, Encoding.UTF8, yaml ? "application/yaml" : "application/json"));

    private static async Task<JsonElement> Imported(HttpClient client, string file, bool yaml = true, bool dryRun = false)
    {
        var response = await Import(client, file, yaml, dryRun);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>What importing did to each job, by its group and name.</summary>
    private static Dictionary<string, string> Actions(JsonElement result) =>
        result.GetProperty("jobs").EnumerateArray().ToDictionary(
            j => $"{j.GetProperty("group").GetString()}.{j.GetProperty("name").GetString()}",
            j => j.GetProperty("action").GetString()!);

    /// <summary>The exported jobs, without when and where they were exported from, to compare two schedulers' jobs.</summary>
    private static async Task<string> ExportedJobs(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("api/jobs/export")).GetProperty("jobs").GetRawText();

    private static async Task CreateJobs(HttpClient client)
    {
        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new
        {
            type = Invoice, name = "invoices", group = "billing", description = "Monthly\ninvoices",
            data = new { customerId = "42", smtpPassword = "hunter2", region = "007" },
            retry = new { maxRetries = 3, delaySeconds = 30, exponential = true },
            triggers = new object[]
            {
                new { name = "nightly", cronExpression = "0 0 2 * * ?", timeZone = "Europe/London", skipMissed = true },
                new { name = "launch", kind = "Once", startAtUtc = DateTimeOffset.UtcNow.AddDays(3) },
                // Starting later, so that importing it elsewhere does not move its start on to the next run
                new { name = "poll", kind = "Interval", intervalSeconds = 600, repeatCount = 5, startAtUtc = DateTimeOffset.UtcNow.AddHours(1) },
            }
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new
        {
            type = Retried, name = "no-retries", data = new { key = "x" }, retry = new { maxRetries = 0, delaySeconds = 0 },
            triggers = new[] { new { cronExpression = "*/30 * * * * ?" } }
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new { type = Plain, name = "manual" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/no-retries/pause", null)).StatusCode);
    }

    [Fact]
    public async Task Jobs_exported_from_one_scheduler_are_imported_into_another_as_they_were()
    {
        await using var source = await TestHost.Start();
        await using var target = await TestHost.Start();
        var from = source.Client();
        var to = target.Client();
        await CreateJobs(from);

        var export = await from.GetAsync("api/jobs/export?format=yaml");
        Assert.Equal("application/yaml", export.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^attachment; filename=""cronlight-test-[0-9a-f]{8}-\d{8}-\d{6}\.yaml""$",
            export.Content.Headers.ContentDisposition?.ToString());
        var yaml = await export.Content.ReadAsStringAsync();
        Assert.StartsWith("version: 1\nscheduler: test-", yaml);
        // Text that would read as a number stays quoted; a secret is masked
        Assert.Contains("region: \"007\"", yaml);
        Assert.Contains("smtpPassword: '********'", yaml);
        Assert.Contains("description: |-\n    Monthly\n    invoices", yaml);

        // The secret is not in the file, so the job that needs it cannot be created from it
        var refused = await Import(to, yaml);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("1 of 3 jobs cannot be imported, so none was. The first: billing.invoices: 'smtpPassword' was exported masked",
            problem.GetProperty("detail").GetString());
        Assert.Equal(["error", "create", "create"], Actions(problem).Values);
        Assert.Empty((await (await target.Scheduler()).GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup())));

        var result = await Imported(to, yaml.Replace("'********'", "hunter2"));
        Assert.Equal((3, 0, 0), (result.GetProperty("created").GetInt32(), result.GetProperty("updated").GetInt32(),
            result.GetProperty("unchanged").GetInt32()));
        Assert.Equal(await ExportedJobs(from), await ExportedJobs(to));

        var scheduler = await target.Scheduler();
        Assert.Equal("hunter2", (await scheduler.GetJobDetail(new JobKey("invoices", "billing")))!.JobDataMap.GetString("smtpPassword"));
        Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey("no-retries-trigger-1", "DEFAULT")));
        Assert.Null(Retries.For((await scheduler.GetJobDetail(new JobKey("no-retries")))!));
    }

    [Fact]
    public async Task Importing_a_file_again_changes_nothing()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await CreateJobs(client);
        var json = await client.GetStringAsync("api/jobs/export");
        var yaml = await client.GetStringAsync("api/jobs/export?format=yaml");

        Assert.All(Actions(await Imported(client, json, yaml: false)).Values, action => Assert.Equal("unchanged", action));
        Assert.All(Actions(await Imported(client, yaml)).Values, action => Assert.Equal("unchanged", action));
        // Written by hand, with no trigger names: each is the trigger with that schedule
        var file = $"version: 1\njobs:\n- name: manual\n  type: {Plain}\n  triggers:\n  - cronExpression: 0 15 10 * * ?\n";
        Assert.Equal("update", Actions(await Imported(client, file))["DEFAULT.manual"]);
        Assert.Equal("unchanged", Actions(await Imported(client, file))["DEFAULT.manual"]);
    }

    [Fact]
    public async Task Importing_updates_jobs_to_match_the_file_and_leaves_the_others_alone()
    {
        var sink = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(services: s => s.AddSingleton<ICronlightAuditSink>(sink));
        var client = host.Client();
        await CreateJobs(client);
        await client.PostAsync("api/jobs/billing/invoices/pause", null);

        var file = $$"""
            version: 1
            jobs:
            - group: billing
              name: invoices
              type: {{Invoice}}
              data:
                customerId: 43
                smtpPassword: '********'
              triggers:
              - name: nightly
                kind: Cron
                cronExpression: 0 30 3 * * ?
                timeZone: Europe/London
            - name: fresh
              type: {{Plain}}
              paused: true
              data: { enabled: true, note: ~ }
              triggers:
              - cronExpression: 0 0 12 * * ?
            """;
        var preview = await Imported(client, file, dryRun: true);
        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.Equal(new Dictionary<string, string> { ["billing.invoices"] = "update", ["DEFAULT.fresh"] = "create" }, Actions(preview));
        var changes = preview.GetProperty("jobs")[0].GetProperty("changes");
        Assert.Equal(["data.customerId", "data.region", "description", "triggers", "retry"],
            changes.EnumerateObject().Select(p => p.Name));
        Assert.Contains("0 0 2 * * ? (Europe/London, skips missed)", changes.GetProperty("triggers").GetProperty("before").GetString());
        Assert.Equal("0 30 3 * * ? (Europe/London)", changes.GetProperty("triggers").GetProperty("after").GetString());
        Assert.False(await (await host.Scheduler()).CheckExists(new JobKey("fresh")));

        await Imported(client, file);
        var scheduler = await host.Scheduler();
        var invoices = (await scheduler.GetJobDetail(new JobKey("invoices", "billing")))!;
        // The masked secret keeps its value; keys the file leaves out are removed
        Assert.Equal(new Dictionary<string, string> { ["customerId"] = "43", ["smtpPassword"] = "hunter2" },
            invoices.JobDataMap.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()!));
        Assert.True(string.IsNullOrEmpty(invoices.Description));
        Assert.Null(Retries.For(invoices));
        var triggers = await scheduler.GetTriggersOfJob(invoices.Key);
        Assert.Equal("0 30 3 * * ?", ((ICronTrigger)Assert.Single(triggers)).CronExpressionString);
        // A job the import updates stays paused, and one it creates is paused as the file says
        Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(triggers.Single().Key));
        Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey("fresh-trigger-1", "DEFAULT")));
        var fresh = (await scheduler.GetJobDetail(new JobKey("fresh")))!;
        Assert.Equal("true", fresh.JobDataMap.GetString("enabled"));
        Assert.False(fresh.JobDataMap.ContainsKey("note"));
        // Jobs the file does not list are left alone
        Assert.True(await scheduler.CheckExists(new JobKey("manual")));

        Assert.Equal(["import-dry-run", "import"], sink.Events.Where(e => e.Action.StartsWith("import")).Select(e => e.Action));
        var audited = sink.Events.Last().Changes;
        Assert.Equal("updated", audited["billing.invoices"].After);
        Assert.Equal("created", audited["DEFAULT.fresh"].After);
        Assert.Equal(new CronlightAuditChange("42", "43"), audited["billing.invoices: data.customerId"]);
        // The check recorded what importing would do
        var checkedChanges = sink.Events.First(e => e.Action == "import-dry-run").Changes;
        Assert.Equal("to update", checkedChanges["billing.invoices"].After);
        Assert.Equal("to create", checkedChanges["DEFAULT.fresh"].After);
        Assert.Equal(new CronlightAuditChange("42", "43"), checkedChanges["billing.invoices: data.customerId"]);
    }

    [Fact]
    public async Task Triggers_that_swap_schedules_are_a_change_and_the_order_they_are_listed_in_is_not()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        // Stored b first, then a: listed in the file by name
        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new
        {
            type = Plain, name = "pair",
            triggers = new[] { new { name = "b", cronExpression = "0 0 2 * * ?" }, new { name = "a", cronExpression = "0 0 1 * * ?" } }
        })).StatusCode);
        string File(string a, string b) => $"""
            version: 1
            jobs:
            - name: pair
              type: {Plain}
              triggers:
              - name: a
                cronExpression: {a}
              - name: b
                cronExpression: {b}
            """;

        Assert.Equal("unchanged", Actions(await Imported(client, File("0 0 1 * * ?", "0 0 2 * * ?")))["DEFAULT.pair"]);
        var swapped = await Imported(client, File("0 0 2 * * ?", "0 0 1 * * ?"));
        Assert.Equal("update", Actions(swapped)["DEFAULT.pair"]);
        var triggers = swapped.GetProperty("jobs")[0].GetProperty("changes").GetProperty("triggers");
        Assert.Equal("0 0 1 * * ? (UTC); 0 0 2 * * ? (UTC)", triggers.GetProperty("before").GetString());
        Assert.Equal("0 0 2 * * ? (UTC); 0 0 1 * * ? (UTC)", triggers.GetProperty("after").GetString());
        var stored = await (await host.Scheduler()).GetTrigger(new TriggerKey("a", "DEFAULT"));
        Assert.Equal("0 0 2 * * ?", ((ICronTrigger)stored!).CronExpressionString);
    }

    [Fact]
    public async Task A_job_imported_paused_never_runs_before_it_is_paused()
    {
        await using var host = await TestHost.Start(history: _ => { });
        var client = host.Client();
        // Due at once: an interval trigger with no start starts now
        var file = $"""
            version: 1
            jobs:
            - name: held
              type: {Plain}
              paused: true
              triggers:
              - name: every-second
                kind: Interval
                intervalSeconds: 1
            """;

        await Imported(client, file);

        var scheduler = await host.Scheduler();
        var trigger = (ISimpleTrigger)(await scheduler.GetTrigger(new TriggerKey("every-second", "DEFAULT")))!;
        Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(trigger.Key));
        Assert.True(trigger.StartTimeUtc > DateTimeOffset.UtcNow.AddSeconds(20));
        await Task.Delay(1500);
        Assert.Empty(await HistoryScenarios.History(client, "?name=held"));
    }

    [Fact]
    public async Task A_file_with_a_mistake_changes_nothing_and_says_what_is_wrong_with_each_job()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await CreateJobs(client);

        var file = $$"""
            version: 1
            jobs:
            - name: fine
              type: {{Plain}}
              triggers:
              - name: shared
                cronExpression: 0 0 1 * * ?
            - name: also-shared
              type: {{Plain}}
              triggers:
              - name: shared
                cronExpression: 0 0 2 * * ?
            - name: unknown
              type: Nope.Job
            - name: manual
              type: {{Invoice}}
            - name: fine
              type: {{Plain}}
            - type: {{Plain}}
            - name: list-data
              type: {{Plain}}
              data: { hosts: [a, b] }
            """;
        var response = await Import(client, file);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobs").EnumerateArray()
            .Select(j => j.GetProperty("error").GetString()).ToList();
        Assert.Equal([
            null,
            "Trigger name 'shared' in group 'DEFAULT' is also used by job DEFAULT.fine in the file",
            "Unknown job type 'Nope.Job'",
            $"Job DEFAULT.manual is a {Plain}, and the file has it as a {Invoice}. A job's type cannot be changed: delete it first, or give it another name",
            "Job DEFAULT.fine is in the file more than once",
            "A job name is required",
            "Job data 'hosts' is a list or an object; job data values are text",
        ], errors);
        Assert.False(await (await host.Scheduler()).CheckExists(new JobKey("fine")));
    }

    [Fact]
    public async Task Yaml_in_flow_style_sent_as_json_is_read_as_yaml_and_broken_json_is_answered_as_json()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        var flow = await Imported(client, $"{{version: 1, jobs: [{{name: flow, type: {Plain}}}]}}", yaml: false);
        Assert.Equal("create", Actions(flow)["DEFAULT.flow"]);

        var broken = await Import(client, "{\"version\": 1, \"jobs\": [}", yaml: false);
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.StartsWith("The file is not valid JSON", (await broken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_export_says_how_many_jobs_it_left_out()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await CreateJobs(client);

        var export = await client.GetAsync("api/jobs/export");

        Assert.Equal("0", Assert.Single(export.Headers.GetValues("X-Cronlight-Not-Exported")));
        Assert.False((await export.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("notExported", out _));
    }

    [Theory]
    [InlineData("version: 1\njobs:\n- name: a\n  cron: x\n", "The file has a property Cronlight does not know at $.jobs[0].cron")]
    [InlineData("version: 1\nbase: &b {name: a}\njobs: [*b]\n", "The file is not valid YAML (line 3, column 8): Aliases (*name) are not supported")]
    [InlineData("version: 1\njobs: []\n---\nversion: 1\n", "The file is not valid YAML (line 3, column 1): The file has more than one YAML document")]
    [InlineData("version: 2\njobs: []\n", "The file is version 2; this Cronlight reads version 1 files")]
    [InlineData("version: 1\n", "The file has no jobs list")]
    [InlineData("version: 1\njobs: yes\n", "The file has a value of the wrong kind at $.jobs")]
    [InlineData("", "The request has no file")]
    public async Task A_file_that_cannot_be_read_is_refused(string file, string error)
    {
        await using var host = await TestHost.Start();

        var response = await Import(host.Client(), file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Viewers_can_export_but_not_import()
    {
        await using var host = await TestHost.Start("Production", o =>
        {
            o.ViewPolicy = "Viewers";
            o.ManagePolicy = "Admins";
        });
        await CreateJobs(host.Client("admin"));
        var viewer = host.Client("viewer");

        var json = await viewer.GetStringAsync("api/jobs/export?group=billing");
        Assert.Equal(["invoices"], JsonDocument.Parse(json).RootElement.GetProperty("jobs").EnumerateArray()
            .Select(j => j.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await Import(viewer, json, yaml: false, dryRun: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Import(host.Client("admin", asDashboard: false), json, yaml: false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Import(host.Client("admin"), json, yaml: false)).StatusCode);
    }
}
