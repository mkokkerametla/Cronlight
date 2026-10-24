using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cronlight.Tests;

public class SecurityTests
{
    // Every kind of route the dashboard serves: UI shell, static asset, SPA fallback, API read
    public static TheoryData<string> AllRouteKinds => new() { "", "favicon.svg", "some/client/route", "api/jobs", "api/scheduler" };

    [Theory]
    [MemberData(nameof(AllRouteKinds))]
    public async Task Without_policy_outside_development_every_route_is_forbidden(string path)
    {
        await using var host = await TestHost.Start("Production");

        var response = await host.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Without_policy_outside_development_mutations_are_forbidden()
    {
        await using var host = await TestHost.Start("Production");

        var response = await host.Client().PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await (await host.Scheduler()).GetJobGroupNames());
    }

    [Fact]
    public async Task AllowAnonymousOutsideDevelopment_opts_out_of_the_default_deny()
    {
        await using var host = await TestHost.Start("Production", o => o.AllowAnonymousOutsideDevelopment = true);

        Assert.Equal(HttpStatusCode.OK, (await host.Client().GetAsync("api/jobs")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllRouteKinds))]
    public async Task With_view_policy_anonymous_users_are_challenged_on_every_route(string path)
    {
        await using var host = await TestHost.Start("Production", o => o.ViewPolicy = "Viewers");

        var response = await host.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_manage()
    {
        await using var host = await TestHost.Start("Production", o =>
        {
            o.ViewPolicy = "Viewers";
            o.ManagePolicy = "Admins";
        });
        var viewer = host.Client("viewer");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("api/jobs")).StatusCode);
        Assert.False(await CanManage(viewer));

        var create = await viewer.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Empty(await (await host.Scheduler()).GetJobGroupNames());
    }

    [Fact]
    public async Task Admin_can_manage()
    {
        await using var host = await TestHost.Start("Production", o =>
        {
            o.ViewPolicy = "Viewers";
            o.ManagePolicy = "Admins";
        });
        var admin = host.Client("admin");

        Assert.True(await CanManage(admin));
        var create = await admin.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "x" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task Read_only_mode_blocks_every_mutation_even_for_admins()
    {
        await using var host = await TestHost.Start("Production", o =>
        {
            o.ViewPolicy = "Viewers";
            o.ManagePolicy = "Admins";
            o.ReadOnly = true;
        });
        var admin = host.Client("admin");

        Assert.False(await CanManage(admin));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("api/jobs/DEFAULT/x/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync("api/jobs/DEFAULT/x")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("api/jobs")).StatusCode);
    }

    [Fact]
    public async Task Only_discovered_job_types_can_be_created()
    {
        await using var host = await TestHost.Start();

        var response = await host.Client().PostJson("api/jobs", new { type = "System.Diagnostics.Process", name = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Secrets_are_masked_and_survive_an_edit_that_sends_the_mask_back()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new
        {
            type = typeof(InvoiceJob).FullName,
            name = "inv",
            data = new Dictionary<string, string> { ["customerId"] = "1", ["smtpPassword"] = "hunter2", ["apiToken"] = "abc" }
        });

        var job = await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/inv");
        Assert.Equal("********", job.GetProperty("data").GetProperty("smtpPassword").GetString()); // [JobParameter] Secret
        Assert.Equal("********", job.GetProperty("data").GetProperty("apiToken").GetString());     // name pattern

        await client.PutJson("api/jobs/DEFAULT/inv", new
        {
            data = new Dictionary<string, string> { ["customerId"] = "2", ["smtpPassword"] = "********", ["apiToken"] = "********" }
        });

        var stored = await (await host.Scheduler()).GetJobDetail(new Quartz.JobKey("inv"));
        Assert.Equal("2", stored!.JobDataMap.GetString("customerId"));
        Assert.Equal("hunter2", stored.JobDataMap.GetString("smtpPassword"));
        Assert.Equal("abc", stored.JobDataMap.GetString("apiToken"));
    }

    private static async Task<bool> CanManage(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("api/scheduler")).GetProperty("canManage").GetBoolean();
}
