using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quartz;
using Quartz.Impl.Matchers;

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
    public async Task Changes_without_the_dashboard_header_are_refused()
    {
        // What a form on another site, or a script ignoring the dashboard's rules, would send
        await using var host = await TestHost.Start();
        await host.Client().PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "p", triggers = new[] { new { cronExpression = "0 0 6 * * ?" } } });
        var other = host.Client(asDashboard: false);

        var create = await other.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "forged" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("X-Cronlight-Request", await create.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync("api/jobs/DEFAULT/p/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync("api/jobs/DEFAULT/p/trigger", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutJson("api/jobs/DEFAULT/p", new { description = "forged" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync("api/jobs/DEFAULT/p")).StatusCode);

        var scheduler = await host.Scheduler();
        Assert.Equal(new[] { "p" }, (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup())).Select(k => k.Name));
        Assert.Null((await scheduler.GetJobDetail(new JobKey("p")))!.Description);
        Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState((await scheduler.GetTriggersOfJob(new JobKey("p"))).Single().Key));
    }

    [Fact]
    public async Task Changes_a_browser_marks_as_cross_site_are_refused_even_with_the_header()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");

        var response = await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "forged" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("another site", await response.Content.ReadAsStringAsync());
        Assert.Empty(await (await host.Scheduler()).GetJobGroupNames());
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("same-site")]
    [InlineData("none")]
    public async Task Changes_from_the_dashboard_or_the_same_site_are_accepted(string fetchSite)
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", fetchSite);

        var response = await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "ok" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("api/jobs")]
    [InlineData("api/scheduler")]
    [InlineData("api/cron/preview?expression=0+0+6+*+*+%3F")]
    public async Task Reads_do_not_need_the_dashboard_header(string path)
    {
        // Links, monitoring and scripts can keep reading without it
        await using var host = await TestHost.Start();

        Assert.Equal(HttpStatusCode.OK, (await host.Client(asDashboard: false).GetAsync(path)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllRouteKinds))]
    public async Task Every_route_sends_the_security_headers(string path)
    {
        await using var host = await TestHost.Start();

        var response = await host.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CronlightOptions.DefaultContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
    }

    [Fact]
    public async Task Refusals_send_the_security_headers_too()
    {
        await using var host = await TestHost.Start("Production");

        var response = await host.Client().GetAsync("");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task A_changed_policy_is_sent_as_is_and_can_allow_embedding()
    {
        var policy = CronlightOptions.DefaultContentSecurityPolicy.Replace("frame-ancestors 'none'", "frame-ancestors https://portal.contoso.com");
        await using var host = await TestHost.Start(configure: o => o.ContentSecurityPolicy = policy);

        var response = await host.Client().GetAsync("");

        Assert.Equal(policy, Header(response, "Content-Security-Policy"));
        // X-Frame-Options cannot name a site, and would contradict the policy
        Assert.Null(Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task Without_a_policy_hosts_can_set_their_own()
    {
        await using var host = await TestHost.Start(configure: o => o.ContentSecurityPolicy = null);

        var response = await host.Client().GetAsync("");

        Assert.Null(Header(response, "Content-Security-Policy"));
        Assert.Null(Header(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : null;

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

        var stored = await (await host.Scheduler()).GetJobDetail(new JobKey("inv"));
        Assert.Equal("2", stored!.JobDataMap.GetString("customerId"));
        Assert.Equal("hunter2", stored.JobDataMap.GetString("smtpPassword"));
        Assert.Equal("abc", stored.JobDataMap.GetString("apiToken"));
    }

    private static async Task<bool> CanManage(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("api/scheduler")).GetProperty("canManage").GetBoolean();
}
