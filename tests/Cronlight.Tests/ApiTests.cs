using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quartz;

namespace Cronlight.Tests;

public class ApiTests
{
    [Fact]
    public async Task Creating_a_job_without_a_type_or_name_is_a_bad_request()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        var noType = await client.PostJson("api/jobs", new { name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, noType.StatusCode);
        Assert.Contains("job type is required", await noType.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJson("api/jobs", new { })).StatusCode);
    }

    [Fact]
    public async Task A_job_is_created_with_the_jobType_it_is_read_back_with()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        var created = await client.PostJson("api/jobs", new { jobType = typeof(PlainJob).FullName, name = "alias" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task A_job_reports_the_description_of_its_type()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = typeof(InvoiceJob).FullName, name = "monthly", data = new { customerId = "1" } });

        var job = await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/monthly");

        Assert.Equal("Invoice job", job.GetProperty("displayName").GetString());
        Assert.Equal("Sends the monthly invoices", job.GetProperty("typeDescription").GetString());
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(3600, 300)]
    public async Task Scheduler_info_reports_how_often_the_dashboard_refreshes(int? setting, int expected)
    {
        await using var host = await TestHost.Start(configure: o =>
        {
            if (setting is { } seconds)
                o.RefreshSeconds = seconds;
        });

        var scheduler = await host.Client().GetFromJsonAsync<JsonElement>("api/scheduler");

        Assert.Equal(expected, scheduler.GetProperty("refreshSeconds").GetInt32());
    }

    [Fact]
    public async Task Ui_is_served_under_the_prefix_with_relative_assets()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        var redirect = await client.GetAsync("/cronlight"); // TestServer does not follow redirects
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/cronlight/", redirect.Headers.Location!.OriginalString);

        var index = await client.GetAsync("");
        Assert.Equal("text/html", index.Content.Headers.ContentType!.MediaType);
        var html = await index.Content.ReadAsStringAsync();
        Assert.DoesNotContain("src=\"/", html);
        Assert.DoesNotContain("href=\"/", html);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Unloadable_jobs_are_listed_but_read_only()
    {
        await using var host = await TestHost.Start();
        host.InsertForeignJob("other", "report");
        var client = host.Client();

        var jobs = await client.GetFromJsonAsync<JsonElement>("api/jobs");
        var foreign = jobs.EnumerateArray().Single(j => j.GetProperty("name").GetString() == "report");
        Assert.False(foreign.GetProperty("loadable").GetBoolean());
        Assert.Contains("OtherApp", foreign.GetProperty("loadError").GetString());

        // Refused in one wording, whatever was asked
        foreach (var response in new[]
        {
            await client.PostAsync("api/jobs/other/report/pause", null),
            await client.PostAsync("api/jobs/other/report/trigger", null),
            await client.PutJson("api/jobs/other/report", new { description = "x" }),
            await client.DeleteAsync("api/jobs/other/report"),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Job is read-only in this dashboard.", problem.GetProperty("title").GetString());
            Assert.Contains("belongs to another application sharing this scheduler, so it cannot be",
                problem.GetProperty("detail").GetString());
        }
        Assert.Equal(1, await host.CountJobRows("other", "report"));
    }

    [Fact]
    public async Task A_job_named_in_any_script_is_created_and_answered_with_where_to_find_it()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        var created = await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "café ünd 报告", group = "ventes/eu" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("jobs/ventes%2Feu/caf%C3%A9%20%C3%BCnd%20%E6%8A%A5%E5%91%8A", created.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/" + created.Headers.Location.OriginalString)).StatusCode);
        // The slash in the group arrives escaped, as the dashboard sends it
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"api/{created.Headers.Location.OriginalString}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/groups/ventes%2Feu/resume", null)).StatusCode);
    }

    [Theory]
    [InlineData("forged\nAudit: alice delete billing.invoices", "DEFAULT", null)]
    [InlineData("tab\tname", "DEFAULT", null)]
    [InlineData("report", "bill\ring", null)]
    [InlineData("report", "DEFAULT", "line\nbreak")]
    public async Task Names_with_control_characters_are_refused_so_they_cannot_forge_log_lines(string name, string group, string? trigger)
    {
        await using var host = await TestHost.Start();

        var response = await host.Client().PostJson("api/jobs", new
        {
            type = typeof(PlainJob).FullName, name, group,
            triggers = trigger == null ? null : new[] { new { name = trigger, cronExpression = "0 0 6 * * ?" } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("control characters", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Empty(await (await host.Scheduler()).GetJobGroupNames());
    }

    [Fact]
    public async Task Errors_are_problem_details_whose_detail_says_what_is_wrong()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "taken" });

        foreach (var (response, status, title, detail) in new[]
        {
            (await client.PostJson("api/jobs", new { type = "Nope.Job", name = "bad" }), HttpStatusCode.BadRequest, "Invalid request.",
                "Unknown job type 'Nope.Job'"),
            (await client.GetAsync("api/jobs/DEFAULT/missing"), HttpStatusCode.NotFound, "Not found.", "Job DEFAULT.missing does not exist."),
            (await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "taken" }), HttpStatusCode.Conflict,
                "Not possible as things stand.", "Job DEFAULT.taken already exists"),
            (await client.GetAsync("api/no-such-thing"), HttpStatusCode.NotFound, "Not found.", "There is no API endpoint GET api/no-such-thing."),
        })
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal((title, detail), (problem.GetProperty("title").GetString(), problem.GetProperty("detail").GetString()));
        }
    }

    [Fact]
    public async Task Scheduler_failures_return_problem_details_without_stack_traces()
    {
        // Opting in lets the call reach Quartz, which then fails to load the job type
        await using var host = await TestHost.Start(configure: o => o.AllowManagingUnloadableJobs = true);
        host.InsertForeignJob("other", "report");

        var response = await host.Client().PostAsync("api/jobs/other/report/trigger", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("   at ", body);
        Assert.Contains("traceId", body);
        // What the job store said can quote the database, its tables and its values: it goes to the log only
        Assert.DoesNotContain("OtherApp", body);
        Assert.Contains("The job store failed", body);
    }

    [Fact]
    public async Task Allowing_unloadable_jobs_permits_pause_and_delete()
    {
        await using var host = await TestHost.Start(configure: o => o.AllowManagingUnloadableJobs = true);
        host.InsertForeignJob("other", "report");
        var client = host.Client();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/other/report/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("api/jobs/other/report")).StatusCode);
        Assert.Equal(0, await host.CountJobRows("other", "report"));
    }

    [Fact]
    public async Task Create_validates_required_parameters_cron_and_time_zone()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var type = typeof(InvoiceJob).FullName;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJson("api/jobs", new { type, name = "a" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJson("api/jobs", new
        {
            type, name = "a", data = new { customerId = "1" }, triggers = new[] { new { cronExpression = "nope" } }
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJson("api/jobs", new
        {
            type, name = "a", data = new { customerId = "1" }, triggers = new[] { new { cronExpression = "0 0 9 * * ?", timeZone = "Mars/Olympus" } }
        })).StatusCode);
        Assert.Empty(await (await host.Scheduler()).GetJobGroupNames());
    }

    [Fact]
    public async Task Parameter_choices_are_offered_and_enforced()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var type = typeof(ExportJob).FullName;

        var types = await client.GetFromJsonAsync<JsonElement>("api/job-types");
        var format = types.EnumerateArray().Single(t => t.GetProperty("typeName").GetString() == type)
            .GetProperty("parameters")[0];
        Assert.Equal(new[] { "csv", "json" }, format.GetProperty("choices").EnumerateArray().Select(c => c.GetString()));

        var rejected = await client.PostJson("api/jobs", new { type, name = "e", data = new { format = "xml" } });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("expected one of csv, json", await rejected.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new { type, name = "e", data = new { format = "csv" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutJson("api/jobs/DEFAULT/e", new { data = new { format = "pdf" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/e", new { data = new { format = "json" } })).StatusCode);
    }

    [Fact]
    public async Task Editing_triggers_adds_reschedules_and_removes_cron_triggers_and_keeps_paused_state()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new
        {
            type = typeof(PlainJob).FullName,
            name = "p",
            triggers = new[]
            {
                new { name = "keep", cronExpression = "0 0 9 * * ?", timeZone = (string?)null },
                new { name = "drop", cronExpression = "0 0 10 * * ?", timeZone = (string?)null }
            }
        });
        var scheduler = await host.Scheduler();
        await scheduler.PauseTrigger(new TriggerKey("keep", "DEFAULT"));

        var response = await client.PutJson("api/jobs/DEFAULT/p", new
        {
            triggers = new[]
            {
                new { name = "keep", cronExpression = "0 30 8 * * ?", timeZone = (string?)"Europe/London" },
                new { name = "new", cronExpression = "0 0 12 * * ?", timeZone = (string?)null }
            }
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var triggers = (await scheduler.GetTriggersOfJob(new JobKey("p"))).Cast<ICronTrigger>().ToDictionary(t => t.Key.Name);
        Assert.Equal(new[] { "keep", "new" }, triggers.Keys.Order());
        Assert.Equal("0 30 8 * * ?", triggers["keep"].CronExpressionString);
        Assert.Equal("Europe/London", triggers["keep"].TimeZone.Id);
        Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey("keep", "DEFAULT")));
    }

    [Fact]
    public async Task Editing_triggers_never_deletes_a_job_registered_by_the_host()
    {
        await using var host = await TestHost.Start();
        await EditScenarios.HostRegisteredJobSurvivesTriggerEdits(host);
    }

    private static async Task CreateInvoice(HttpClient client) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new
        {
            type = typeof(InvoiceJob).FullName,
            name = "inv",
            data = new { customerId = "7", smtpPassword = "hunter2", region = "eu" }
        })).StatusCode);

    [Fact]
    public async Task Updating_some_job_data_keeps_the_rest()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await CreateInvoice(client);

        // A script changing one value sends only that one
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/inv", new { data = new { customerId = "8" } })).StatusCode);

        var data = (await (await host.Scheduler()).GetJobDetail(new JobKey("inv")))!.JobDataMap;
        Assert.Equal(("8", "hunter2", "eu"), (data.GetString("customerId"), data.GetString("smtpPassword"), data.GetString("region")));
    }

    [Fact]
    public async Task Updating_job_data_with_null_removes_that_key_and_still_checks_required_parameters()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await CreateInvoice(client);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutJson("api/jobs/DEFAULT/inv", new { data = new Dictionary<string, string?> { ["region"] = null } })).StatusCode);
        var data = (await (await host.Scheduler()).GetJobDetail(new JobKey("inv")))!.JobDataMap;
        Assert.False(data.ContainsKey("region"));
        Assert.Equal("7", data.GetString("customerId"));

        var removeRequired = await client.PutJson("api/jobs/DEFAULT/inv", new { data = new Dictionary<string, string?> { ["customerId"] = null } });
        Assert.Equal(HttpStatusCode.BadRequest, removeRequired.StatusCode);
        Assert.Contains("Missing required parameters: customerId", await removeRequired.Content.ReadAsStringAsync());
        Assert.Equal("7", (await (await host.Scheduler()).GetJobDetail(new JobKey("inv")))!.JobDataMap.GetString("customerId"));
    }

    [Fact]
    public async Task Editing_keeps_the_type_of_job_data_values()
    {
        await using var host = await TestHost.Start(useProperties: false);
        var client = host.Client();
        var scheduler = await host.Scheduler();
        var key = new JobKey("typed");
        await scheduler.AddJob(JobBuilder.Create<PlainJob>().WithIdentity(key).StoreDurably()
            .SetJobData(new JobDataMap
            {
                ["count"] = 5, ["enabled"] = true, ["strict"] = false, ["label"] = "a", ["items"] = new List<string> { "x", "y" },
                ["tags"] = new List<string> { "eu" }
            })
            .Build(), replace: false);
        var before = (await scheduler.GetJobDetail(key))!.JobDataMap;

        // What the edit form sends back: every value as the text it was shown, one of them changed
        var shown = (await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/typed")).GetProperty("data")
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        shown["count"] = "6";
        shown["enabled"] = "sometimes"; // does not convert to bool, so it is kept as text
        shown["tags"] = "eu, us"; // a list cannot be made from text, so it becomes text
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/typed", new { data = shown })).StatusCode);

        var after = (await scheduler.GetJobDetail(key))!.JobDataMap;
        Assert.Equal(before["count"].GetType(), after["count"].GetType());
        Assert.Equal(6L, Convert.ToInt64(after["count"]));
        Assert.Equal("sometimes", after["enabled"]);
        // Sent back unchanged as "False", so it is still a bool
        Assert.Equal(before["strict"].GetType(), after["strict"].GetType());
        Assert.Equal(false, after["strict"]);
        Assert.Equal(before["items"].GetType(), after["items"].GetType());
        Assert.Equal("a", after["label"]);
        Assert.Equal("eu, us", after["tags"]);
    }

    // The database stores' serializer keeps an enum as its number; Quartz's in-memory store keeps the value itself
    [Fact]
    public async Task Editing_keeps_an_enum_value_an_enum_where_the_store_keeps_it()
    {
        await using var host = await TestHost.Start(ramJobStore: true);
        var scheduler = await host.Scheduler();
        var key = new JobKey("weekly");
        await scheduler.AddJob(JobBuilder.Create<PlainJob>().WithIdentity(key).StoreDurably()
            .SetJobData(new JobDataMap { ["day"] = DayOfWeek.Monday }).Build(), replace: false);

        Assert.Equal(HttpStatusCode.NoContent,
            (await host.Client().PutJson("api/jobs/DEFAULT/weekly", new { data = new { day = "friday" } })).StatusCode);
        Assert.Equal(DayOfWeek.Friday, (await scheduler.GetJobDetail(key))!.JobDataMap["day"]);
        // Text that names no value of it is kept as text
        Assert.Equal(HttpStatusCode.NoContent,
            (await host.Client().PutJson("api/jobs/DEFAULT/weekly", new { data = new { day = "someday" } })).StatusCode);
        Assert.Equal("someday", (await scheduler.GetJobDetail(key))!.JobDataMap["day"]);
    }

    [Fact]
    public async Task Jobs_from_other_apps_cannot_be_run_with_different_data_even_when_managing_them_is_allowed()
    {
        await using var host = await TestHost.Start(configure: o => o.AllowManagingUnloadableJobs = true);
        host.InsertForeignJob("other", "report");

        var response = await host.Client().PostJson("api/jobs/other/report/trigger", new { data = new { month = "2026-09" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("cannot be run with different data", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_stores_nothing_when_a_trigger_cannot_be_scheduled()
    {
        await using var host = await TestHost.Start();
        await EditScenarios.CreateStoresNothingWhenATriggerCannotBeScheduled(host);
    }

    [Fact]
    public async Task Editing_rejects_invalid_triggers_before_changing_anything()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var type = typeof(PlainJob).FullName;
        await client.PostJson("api/jobs", new { type, name = "other", triggers = new[] { new { name = "taken", cronExpression = "0 0 9 * * ?" } } });
        await client.PostJson("api/jobs", new { type, name = "p", description = "before", triggers = new[] { new { name = "mine", cronExpression = "0 0 6 * * ?" } } });

        var nameTaken = await client.PutJson("api/jobs/DEFAULT/p", new { description = "after", triggers = new[] { new { name = "taken", cronExpression = "0 0 7 * * ?" } } });
        Assert.Equal(HttpStatusCode.Conflict, nameTaken.StatusCode);
        var neverFires = await client.PutJson("api/jobs/DEFAULT/p", new { description = "after", triggers = new[] { new { name = "mine", cronExpression = "0 0 0 1 1 ? 2020" } } });
        Assert.Equal(HttpStatusCode.BadRequest, neverFires.StatusCode);

        var scheduler = await host.Scheduler();
        Assert.Equal("before", (await scheduler.GetJobDetail(new JobKey("p")))!.Description);
        Assert.Equal("mine", Assert.Single(await scheduler.GetTriggersOfJob(new JobKey("p"))).Key.Name);
    }

    [Fact]
    public async Task Unnamed_triggers_get_names_not_already_in_use()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new
        {
            type = typeof(PlainJob).FullName,
            name = "p",
            triggers = new[] { new { cronExpression = "0 0 1 * * ?" }, new { cronExpression = "0 0 2 * * ?" } }
        });
        var scheduler = await host.Scheduler();
        Assert.Equal(new[] { "p-trigger-1", "p-trigger-2" }, (await scheduler.GetTriggersOfJob(new JobKey("p"))).Select(t => t.Key.Name).Order());

        // Remove the first trigger and add a blank one: the blank must not reuse an existing name
        var response = await client.PutJson("api/jobs/DEFAULT/p", new
        {
            triggers = new[] { new { name = (string?)"p-trigger-2", cronExpression = "0 0 2 * * ?" }, new { name = (string?)null, cronExpression = "0 0 3 * * ?" } }
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var crons = (await scheduler.GetTriggersOfJob(new JobKey("p"))).Cast<ICronTrigger>().ToDictionary(t => t.Key.Name, t => t.CronExpressionString);
        Assert.Equal("0 0 2 * * ?", crons["p-trigger-2"]);
        Assert.DoesNotContain("p-trigger-1", crons.Keys);
        Assert.Equal(2, crons.Count);
        Assert.Contains("0 0 3 * * ?", crons.Values);
    }

    [Fact]
    public async Task Jobs_are_read_back_with_their_triggers_defaults_and_masked_secrets()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var created = await client.PostJson("api/jobs", new
        {
            type = typeof(ReportJob).FullName,
            name = "r",
            group = "reports",
            description = "Monthly",
            data = new Dictionary<string, string> { ["format"] = "csv", ["apiToken"] = "s3cret" },
            triggers = new[] { new { name = "monthly", cronExpression = "0 0 6 1 * ?", timeZone = "Europe/London" } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var scheduler = await host.Scheduler();
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("once", "reports").ForJob("r", "reports")
            .StartAt(DateTimeOffset.UtcNow.AddDays(1)).Build());
        await scheduler.PauseTrigger(new TriggerKey("monthly", "reports"));

        var job = await client.GetFromJsonAsync<JsonElement>("api/jobs/reports/r");

        Assert.Equal("Monthly", job.GetProperty("description").GetString());
        Assert.Equal(typeof(ReportJob).FullName, job.GetProperty("jobType").GetString());
        Assert.Equal("Report", job.GetProperty("displayName").GetString());
        Assert.True(job.GetProperty("durable").GetBoolean());
        Assert.True(job.GetProperty("loadable").GetBoolean());

        var data = job.GetProperty("data");
        Assert.Equal("csv", data.GetProperty("format").GetString());
        Assert.Equal("10", data.GetProperty("pages").GetString()); // [JobParameter] default, applied on create
        Assert.Equal("********", data.GetProperty("apiToken").GetString()); // SensitiveKeyPatterns

        var triggers = job.GetProperty("triggers").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
        var monthly = triggers["monthly"];
        Assert.Equal("reports", monthly.GetProperty("group").GetString());
        Assert.Equal("Cron", monthly.GetProperty("kind").GetString());
        Assert.Equal("0 0 6 1 * ?", monthly.GetProperty("cronExpression").GetString());
        Assert.Equal("Europe/London", monthly.GetProperty("timeZone").GetString());
        Assert.Equal("Paused", monthly.GetProperty("state").GetString());

        var once = triggers["once"];
        Assert.Equal("Once", once.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, once.GetProperty("cronExpression").ValueKind);
        Assert.Equal("Normal", once.GetProperty("state").GetString());
        var next = once.GetProperty("nextFireTimeUtc").GetDateTimeOffset();
        Assert.InRange(next, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));
        Assert.Equal(JsonValueKind.Null, once.GetProperty("previousFireTimeUtc").ValueKind);

        // The list endpoint returns the same shape
        var listed = (await client.GetFromJsonAsync<JsonElement>("api/jobs")).EnumerateArray().Single(j => j.GetProperty("name").GetString() == "r");
        Assert.Equal(2, listed.GetProperty("triggers").GetArrayLength());
    }

    [Fact]
    public async Task Missing_and_conflicting_requests_get_clear_errors()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var type = typeof(PlainJob).FullName;

        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new { type, name = "dup" })).StatusCode);
        var duplicate = await client.PostJson("api/jobs", new { type, name = "dup" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("already exists", await duplicate.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/jobs/DEFAULT/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutJson("api/jobs/DEFAULT/missing", new { description = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("api/jobs/DEFAULT/missing/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("api/jobs/DEFAULT/missing/trigger", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("api/jobs/DEFAULT/missing")).StatusCode);

        var sameName = await client.PostJson("api/jobs", new
        {
            type, name = "twice",
            triggers = new[] { new { name = "same", cronExpression = "0 0 1 * * ?" }, new { name = "same", cronExpression = "0 0 2 * * ?" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, sameName.StatusCode);
        Assert.Contains("Duplicate trigger name 'same'", await sameName.Content.ReadAsStringAsync());

        var preview = await client.GetFromJsonAsync<JsonElement>($"api/cron/preview?expression={Uri.EscapeDataString("0 0 9 * * ?")}&timeZone=Mars/Olympus");
        Assert.False(preview.GetProperty("isValid").GetBoolean());
        Assert.Equal("Unknown time zone 'Mars/Olympus'", preview.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task Jobs_from_other_apps_cannot_be_edited_even_when_managing_them_is_allowed()
    {
        await using var host = await TestHost.Start(configure: o => o.AllowManagingUnloadableJobs = true);
        host.InsertForeignJob("other", "report");

        var response = await host.Client().PutJson("api/jobs/other/report", new { description = "x" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Job is read-only in this dashboard.", problem.GetProperty("title").GetString());
        Assert.EndsWith("so it cannot be edited here.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Pausing_and_resuming_a_job_changes_the_state_of_its_triggers()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "p", triggers = new[] { new { cronExpression = "0 0 6 * * ?" } } });
        async Task<string?> State() =>
            (await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/p")).GetProperty("triggers")[0].GetProperty("state").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/p/pause", null)).StatusCode);
        Assert.Equal("Paused", await State());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/p/resume", null)).StatusCode);
        Assert.Equal("Normal", await State());
    }

    [Fact]
    public async Task Running_jobs_are_read_from_the_job_store()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        await client.PostJson("api/jobs", new { type = typeof(BlockingJob).FullName, name = "block", data = new { gate = host.SchedulerName } });
        await client.PostAsync("api/jobs/DEFAULT/block/trigger", null);

        try
        {
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            var running = await client.GetFromJsonAsync<JsonElement>("api/running");

            Assert.Equal("cluster", running.GetProperty("source").GetString());
            var job = Assert.Single(running.GetProperty("jobs").EnumerateArray());
            Assert.Equal("block", job.GetProperty("name").GetString());
            Assert.Equal(typeof(BlockingJob).FullName, job.GetProperty("jobType").GetString());
        }
        finally
        {
            gate.Release.Release();
        }
    }

    [Fact]
    public async Task Cron_preview_reports_validity_and_next_times()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        var valid = await client.GetFromJsonAsync<JsonElement>("api/cron/preview?expression=0%200%209%20*%20*%20%3F&timeZone=America/Chicago&count=3");
        Assert.True(valid.GetProperty("isValid").GetBoolean());
        Assert.Equal(3, valid.GetProperty("nextFireTimes").GetArrayLength());

        var invalid = await client.GetFromJsonAsync<JsonElement>("api/cron/preview?expression=nope");
        Assert.False(invalid.GetProperty("isValid").GetBoolean());
    }
}

/// <summary>Edit scenarios that depend on the job store, also run against each database in DatabaseTests.</summary>
public static class EditScenarios
{
    public static async Task HostRegisteredJobSurvivesTriggerEdits(TestHost host)
    {
        // Like AddQuartz().ScheduleJob<T>(): a non-durable job whose trigger is in the DEFAULT group
        var client = host.Client();
        var scheduler = await host.Scheduler();
        var key = new JobKey("cleanup", "maintenance");
        await scheduler.ScheduleJob(
            JobBuilder.Create<PlainJob>().WithIdentity(key).Build(),
            TriggerBuilder.Create().WithIdentity("nightly").ForJob(key).WithCronSchedule("0 0 2 * * ?").Build());

        // Same name: rescheduled in place, keeping its group
        var edited = await client.PutJson("api/jobs/maintenance/cleanup", new { triggers = new[] { new { name = "nightly", cronExpression = "0 0 3 * * ?" } } });
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var trigger = Assert.IsAssignableFrom<ICronTrigger>(Assert.Single(await scheduler.GetTriggersOfJob(key)));
        Assert.Equal(new TriggerKey("nightly", "DEFAULT"), trigger.Key);
        Assert.Equal("0 0 3 * * ?", trigger.CronExpressionString);

        // Renaming the only trigger adds the new one before removing the old one
        var renamed = await client.PutJson("api/jobs/maintenance/cleanup", new { triggers = new[] { new { name = "renamed", cronExpression = "0 0 4 * * ?" } } });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal("renamed", Assert.Single(await scheduler.GetTriggersOfJob(key)).Key.Name);

        // Removing every trigger keeps the job, now durable, for manual runs
        var cleared = await client.PutJson("api/jobs/maintenance/cleanup", new { triggers = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Empty(await scheduler.GetTriggersOfJob(key));
        Assert.True((await scheduler.GetJobDetail(key))!.Durable);
    }

    public static async Task CreateStoresNothingWhenATriggerCannotBeScheduled(TestHost host)
    {
        var client = host.Client();
        var type = typeof(PlainJob).FullName;
        await client.PostJson("api/jobs", new { type, name = "other", triggers = new[] { new { name = "taken", cronExpression = "0 0 9 * * ?" } } });

        var neverFires = await client.PostJson("api/jobs", new { type, name = "a", triggers = new[] { new { name = (string?)null, cronExpression = "0 0 0 1 1 ? 2020" } } });
        Assert.Equal(HttpStatusCode.BadRequest, neverFires.StatusCode);
        Assert.Contains("never fires", await neverFires.Content.ReadAsStringAsync());

        var nameTaken = await client.PostJson("api/jobs", new
        {
            type, name = "b",
            triggers = new[] { new { name = (string?)"fresh", cronExpression = "0 0 8 * * ?" }, new { name = (string?)"taken", cronExpression = "0 0 9 * * ?" } }
        });
        Assert.Equal(HttpStatusCode.Conflict, nameTaken.StatusCode);

        var scheduler = await host.Scheduler();
        Assert.False(await scheduler.CheckExists(new JobKey("a")));
        Assert.False(await scheduler.CheckExists(new JobKey("b")));
        Assert.False(await scheduler.CheckExists(new TriggerKey("fresh")));
    }
}
