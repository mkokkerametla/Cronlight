using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cronlight.Tests;

internal static class JobSearchScenarios
{
    /// <summary>Five jobs: three plain ones (one paused), an invoice job and a report job.</summary>
    public static async Task CreateJobs(HttpClient client)
    {
        var cron = new[] { new { cronExpression = "0 0 6 * * ?" } };
        foreach (var (group, name) in new[] { ("billing", "invoices-eu"), ("billing", "invoices-us"), ("ops", "cleanup") })
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name, group, triggers = cron })).StatusCode);
        await client.PostJson("api/jobs", new { type = typeof(InvoiceJob).FullName, name = "send", group = "billing", data = new { customerId = "1" } });
        await client.PostJson("api/jobs", new { type = typeof(ReportJob).FullName, name = "weekly", group = "reports" });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/ops/cleanup/pause", null)).StatusCode);
    }

    public static async Task<JsonElement> Search(HttpClient client, string query = "") =>
        await client.GetFromJsonAsync<JsonElement>($"api/jobs/search{query}");

    public static string[] Names(JsonElement page) =>
        page.GetProperty("jobs").EnumerateArray().Select(j => $"{j.GetProperty("group").GetString()}.{j.GetProperty("name").GetString()}").ToArray();

    /// <summary>Search, filters, paging and counts, the same whichever way the index is read.</summary>
    public static async Task SearchesFiltersAndPages(TestHost host)
    {
        var client = host.Client();
        await CreateJobs(client);

        var all = await Search(client);
        Assert.Equal(new[] { "billing.invoices-eu", "billing.invoices-us", "billing.send", "ops.cleanup", "reports.weekly" }, Names(all));
        Assert.Equal(5, all.GetProperty("total").GetInt32());
        var counts = all.GetProperty("counts");
        Assert.Equal((5, 4, 1, 0), (counts.GetProperty("all").GetInt32(), counts.GetProperty("active").GetInt32(),
            counts.GetProperty("paused").GetInt32(), counts.GetProperty("unloadable").GetInt32()));
        Assert.Equal(new[] { "Invoice job", "PlainJob", "Report" }, all.GetProperty("types").EnumerateArray().Select(t => t.GetString()));

        // Name or group, ignoring case
        Assert.Equal(new[] { "billing.invoices-eu", "billing.invoices-us" }, Names(await Search(client, "?search=INVOICES")));
        Assert.Equal(new[] { "ops.cleanup" }, Names(await Search(client, "?search=ops")));
        Assert.Equal(new[] { "ops.cleanup" }, Names(await Search(client, "?status=paused")));
        Assert.Equal(new[] { "billing.send" }, Names(await Search(client, "?type=Invoice%20job")));
        // Counts stay over every job whatever the filters
        Assert.Equal(5, (await Search(client, "?search=nothing")).GetProperty("counts").GetProperty("all").GetInt32());

        var second = await Search(client, "?pageSize=2&page=2");
        Assert.Equal(new[] { "billing.send", "ops.cleanup" }, Names(second));
        Assert.Equal((5, 2, 2), (second.GetProperty("total").GetInt32(), second.GetProperty("page").GetInt32(), second.GetProperty("pageSize").GetInt32()));
        // Past the end: the last page
        var past = await Search(client, "?pageSize=2&page=9");
        Assert.Equal(3, past.GetProperty("page").GetInt32());
        Assert.Equal(new[] { "reports.weekly" }, Names(past));

        // The page carries full job details, as GET /jobs does
        var send = all.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("name").GetString() == "send");
        Assert.Equal("Invoice job", send.GetProperty("displayName").GetString());
        Assert.Equal("1", send.GetProperty("data").GetProperty("customerId").GetString());
    }
}

public class JobSearchTests
{
    [Fact]
    public Task Searches_filters_and_pages_from_the_Quartz_tables() =>
        Run(ramJobStore: false);

    [Fact]
    public Task Searches_filters_and_pages_the_in_memory_store() =>
        Run(ramJobStore: true);

    private static async Task Run(bool ramJobStore)
    {
        await using var host = await TestHost.Start(ramJobStore: ramJobStore);
        await JobSearchScenarios.SearchesFiltersAndPages(host);
    }

    [Fact]
    public async Task Jobs_from_other_apps_are_counted_and_filtered_as_unloadable()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await JobSearchScenarios.CreateJobs(client);
        host.InsertForeignJob("other", "report");

        var page = await JobSearchScenarios.Search(client, "?status=Unloadable");
        Assert.Equal(new[] { "other.report" }, JobSearchScenarios.Names(page));
        Assert.False(page.GetProperty("jobs")[0].GetProperty("loadable").GetBoolean());
        Assert.Equal(1, page.GetProperty("counts").GetProperty("unloadable").GetInt32());
        Assert.Contains("Unloadable", page.GetProperty("types").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(new[] { "other.report" }, JobSearchScenarios.Names(await JobSearchScenarios.Search(client, "?type=Unloadable")));
    }

    [Fact]
    public async Task An_unknown_status_is_refused()
    {
        await using var host = await TestHost.Start();
        var response = await host.Client().GetAsync("api/jobs/search?status=sleeping");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Active, Paused, Unloadable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Loads_details_only_for_the_page_shown()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        for (var i = 0; i < 30; i++)
            await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = $"job-{i:00}" });

        var page = await JobSearchScenarios.Search(client, "?pageSize=10&page=3");

        Assert.Equal(Enumerable.Range(20, 10).Select(i => $"DEFAULT.job-{i:00}"), JobSearchScenarios.Names(page));
        Assert.Equal(30, page.GetProperty("total").GetInt32());
    }
}
