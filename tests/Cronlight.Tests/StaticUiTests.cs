using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace Cronlight.Tests;

public class StaticUiTests
{
    /// <summary>The dashboard's main script, as its page names it.</summary>
    private static async Task<string> MainScript(HttpClient client)
    {
        var html = await client.GetStringAsync("");
        var script = Regex.Match(html, "src=\"\\./(assets/index-[^\"]+\\.js)\"").Groups[1].Value;
        Assert.NotEmpty(script);
        return script;
    }

    [Fact]
    public async Task Assets_go_compressed_to_browsers_that_take_it_and_are_cached_for_good()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var script = await MainScript(client);
        var plain = await (await client.GetAsync(script)).Content.ReadAsByteArrayAsync();

        foreach (var (encoding, decompress) in new (string, Func<Stream, Stream>)[]
        {
            ("br", s => new BrotliStream(s, CompressionMode.Decompress)),
            ("gzip", s => new GZipStream(s, CompressionMode.Decompress)),
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, script);
            request.Headers.AcceptEncoding.ParseAdd($"{encoding}, deflate");
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(encoding, Assert.Single(response.Content.Headers.ContentEncoding));
            Assert.Contains("Accept-Encoding", response.Headers.Vary);
            Assert.Equal("text/javascript", response.Content.Headers.ContentType!.MediaType);
            // Its name changes whenever its content does
            Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
            using var unpacked = new MemoryStream();
            await decompress(await response.Content.ReadAsStreamAsync()).CopyToAsync(unpacked);
            Assert.Equal(plain, unpacked.ToArray());
        }
    }

    [Fact]
    public async Task The_page_is_checked_again_on_each_visit_and_answers_not_modified_while_unchanged()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        var page = await client.GetAsync("");
        Assert.True(page.Headers.CacheControl!.NoCache);
        Assert.NotNull(page.Content.Headers.LastModified);

        using var again = new HttpRequestMessage(HttpMethod.Get, "");
        again.Headers.IfModifiedSince = page.Content.Headers.LastModified;
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }
}
