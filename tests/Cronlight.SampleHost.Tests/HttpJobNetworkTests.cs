using System.Net;
using System.Net.Sockets;
using System.Text;
using Cronlight.SampleHost;

namespace Cronlight.SampleHost.Tests;

public class HttpJobNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1")] // loopback
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("100.64.0.1")] // carrier-grade NAT
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.10")] // documentation
    [InlineData("198.18.0.1")] // benchmarking
    [InlineData("224.0.0.1")] // multicast
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")] // unique local
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")] // link-local
    [InlineData("ff02::1")] // multicast
    [InlineData("2001:db8::1")] // documentation
    [InlineData("::ffff:127.0.0.1")] // IPv4 written as IPv6
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 for 169.254.169.254
    public void Internal_addresses_are_not_public(string address) =>
        Assert.False(HttpJobNetwork.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")] // just outside 172.16.0.0/12
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")] // just outside 100.64.0.0/10
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")] // NAT64 for 8.8.8.8
    public void Public_addresses_are_public(string address) =>
        Assert.True(HttpJobNetwork.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("api.partner.com", true)]
    [InlineData("API.Partner.com", true)]
    [InlineData("eu.hooks.internal", true)]
    [InlineData("a.b.hooks.internal", true)]
    [InlineData("hooks.internal", false)] // the wildcard covers subdomains only
    [InlineData("evilhooks.internal", false)]
    [InlineData("api.partner.com.evil.com", false)]
    [InlineData("partner.com", false)]
    public void Hosts_match_exact_names_and_subdomain_wildcards(string host, bool allowed) =>
        Assert.Equal(allowed, HttpJobNetwork.IsAllowedHost(host, ["api.partner.com", "*.hooks.internal"]));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task Internal_addresses_are_refused_before_connecting(string host)
    {
        using var server = new OneResponseServer("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        using var client = new HttpClient(HttpJobNetwork.CreateHandler(new HttpJobOptions()));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://{host}:{server.Port}/"));

        Assert.Contains("private or internal addresses", error.ToString());
        Assert.False(server.WasConnected);
    }

    [Fact]
    public async Task Internal_addresses_are_called_when_private_networks_are_allowed()
    {
        using var server = new OneResponseServer("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = new HttpClient(HttpJobNetwork.CreateHandler(new HttpJobOptions { AllowPrivateNetworks = true }));

        var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        // A redirect from an allowed host could otherwise lead anywhere, such as the metadata endpoint
        using var server = new OneResponseServer(
            "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = new HttpClient(HttpJobNetwork.CreateHandler(new HttpJobOptions { AllowPrivateNetworks = true }));

        var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    /// <summary>Answers the first connection with a fixed HTTP response and records whether anyone connected.</summary>
    private sealed class OneResponseServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private volatile bool connected;

        public OneResponseServer(string response)
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    using var socket = await listener.AcceptSocketAsync();
                    connected = true;
                    var buffer = new byte[4096];
                    await socket.ReceiveAsync(buffer);
                    await socket.SendAsync(Encoding.ASCII.GetBytes(response));
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    // Stopped before anyone connected
                }
            });
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public bool WasConnected => connected;

        public void Dispose() => listener.Stop();
    }
}
