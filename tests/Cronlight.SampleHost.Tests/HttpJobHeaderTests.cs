using Cronlight.SampleHost;
using Quartz;

namespace Cronlight.SampleHost.Tests;

public class HttpJobHeaderTests
{
    [Fact]
    public void Headers_are_added_from_name_value_lines()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example/") { Content = new StringContent("{}") };

        HttpJob.AddHeaders(request, "X-Tenant: acme\nAccept: application/json\n Content-Language : de ");

        Assert.Equal("acme", Assert.Single(request.Headers.GetValues("X-Tenant")));
        Assert.Equal("application/json", request.Headers.Accept.ToString());
        Assert.Equal("de", request.Content.Headers.ContentLanguage.ToString());
    }

    [Theory]
    // Host would send the request to another site behind the same address, such as a shared proxy
    [InlineData("Host: internal-admin.contoso.local")]
    [InlineData("host: internal-admin.contoso.local")]
    [InlineData("Connection: Upgrade")]
    [InlineData("Transfer-Encoding: chunked")]
    [InlineData("Proxy-Authorization: Basic eDp5")]
    [InlineData("Content-Length: 0")]
    public void Headers_that_decide_where_or_how_the_request_is_sent_are_refused(string line)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example/");

        var refused = Assert.Throws<JobExecutionException>(() => HttpJob.AddHeaders(request, line));

        Assert.Contains("cannot be set", refused.Message);
        Assert.Empty(request.Headers);
    }

    [Fact]
    public void An_authorization_value_that_is_not_one_is_refused_without_quoting_it()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example/");

        var refused = Assert.Throws<JobExecutionException>(() => HttpJob.SetAuthorization(request, "Server=db;Password=hunter2secret"));

        Assert.DoesNotContain("hunter2secret", refused.Message);
        HttpJob.SetAuthorization(request, "Bearer abc.def");
        Assert.Equal("Bearer abc.def", request.Headers.Authorization!.ToString());
    }
}
