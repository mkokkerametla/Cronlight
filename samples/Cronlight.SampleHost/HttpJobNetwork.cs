using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Cronlight.SampleHost;

/// <summary>
/// Where <see cref="HttpJob"/> may connect. Anyone who can create jobs chooses the URL, so without these
/// rules they could make the server call internal services or the cloud metadata endpoint (SSRF).
/// </summary>
public static class HttpJobNetwork
{
    /// <summary>Exact host names, or "*.example.com" for any subdomain of example.com (not example.com itself).</summary>
    public static bool IsAllowedHost(string host, IEnumerable<string> allowed) =>
        allowed.Any(entry => entry.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(entry[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, entry, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// False for loopback, private, link-local (including the cloud metadata address 169.254.169.254),
    /// carrier-grade NAT, multicast, documentation and other reserved ranges, in IPv4 and IPv6.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224 // this network, private, loopback, multicast and reserved
                || (b[0] == 100 && b[1] is >= 64 and < 128) // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254) // link-local, cloud metadata
                || (b[0] == 172 && b[1] is >= 16 and < 32)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) // IETF protocol assignments, documentation
                || (b[0] == 198 && b[1] is 18 or 19) // benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }

        var v6 = address.GetAddressBytes();
        // NAT64 (64:ff9b::/96) carries an IPv4 address: judge that one
        if (v6.AsSpan(0, 12).SequenceEqual(new byte[] { 0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0 }))
            return IsPublic(new IPAddress(v6[12..]));
        return !(v6.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 // unspecified, loopback, IPv4-compatible
            || (v6[0] & 0xfe) == 0xfc // unique local fc00::/7
            || (v6[0] == 0xfe && (v6[1] & 0xc0) is 0x80 or 0xc0) // link-local fe80::/10, site-local fec0::/10
            || v6[0] == 0xff // multicast
            || (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8)); // documentation
    }

    /// <summary>
    /// The handler for HttpJob's client. It checks the address it actually connects to, after DNS, so a
    /// public name that resolves (or later re-resolves) to an internal address is refused too. Redirects
    /// are not followed, since they could lead anywhere, and no proxy is used, since the proxy's own
    /// address is all a connection check would see.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(HttpJobOptions options) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken);
            var usable = options.AllowPrivateNetworks ? addresses : addresses.Where(IsPublic).ToArray();
            if (usable.Length == 0)
                throw new HttpRequestException(
                    $"{host} resolves only to private or internal addresses ({string.Join(", ", addresses.Select(a => a.ToString()))}), " +
                    "which HttpJob does not call unless HttpJob:AllowPrivateNetworks is on");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(usable, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

public static class HttpJobServiceCollectionExtensions
{
    /// <summary>Registers the options and the HTTP client HttpJob needs, with the connection rules above.</summary>
    public static IServiceCollection AddHttpJob(this IServiceCollection services, Action<HttpJobOptions> configure)
    {
        services.Configure(configure);
        services.AddHttpClient(HttpJob.ClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => HttpJobNetwork.CreateHandler(sp.GetRequiredService<IOptions<HttpJobOptions>>().Value));
        return services;
    }
}
