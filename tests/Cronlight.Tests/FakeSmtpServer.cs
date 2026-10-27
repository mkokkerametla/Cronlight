using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Cronlight.Tests;

/// <summary>
/// Just enough SMTP on a loopback port to receive what SmtpClient sends: no TLS, and sign-in with AUTH LOGIN, which
/// accepts anyone. Keeps each message's recipients and decoded subject and body, and who signed in.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task accepting;

    public ConcurrentQueue<Email> Messages { get; } = new();
    public ConcurrentQueue<(string UserName, string Password)> SignIns { get; } = new();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public record Email(IReadOnlyList<string> To, string Subject, string Body);

    public FakeSmtpServer()
    {
        listener.Start();
        accepting = Accept();
    }

    /// <summary>A port nothing listens on, for a server that cannot be reached.</summary>
    public static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task Accept()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token);
                _ = Serve(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        await writer.WriteLineAsync("220 localhost ESMTP");
        var to = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            var command = line.Split(' ', 2)[0].ToUpperInvariant();
            switch (command)
            {
                case "EHLO":
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250-AUTH LOGIN");
                    await writer.WriteLineAsync("250 8BITMIME");
                    break;
                case "AUTH":
                    // The user name may come with the command; each prompt is "Username:" or "Password:" in base64
                    static string Decode(string? text) => Encoding.UTF8.GetString(Convert.FromBase64String(text ?? ""));
                    var parts = line.Split(' ');
                    if (parts.Length < 3)
                        await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                    var userName = Decode(parts.Length >= 3 ? parts[2] : await reader.ReadLineAsync());
                    await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                    SignIns.Enqueue((userName, Decode(await reader.ReadLineAsync())));
                    await writer.WriteLineAsync("235 Authentication successful");
                    break;
                case "RCPT":
                    to.Add(Regex.Match(line, "<(.*)>").Groups[1].Value);
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        data.Append(dataLine.StartsWith("..") ? dataLine[1..] : dataLine).Append("\r\n");
                    Messages.Enqueue(Parse(to.ToList(), data.ToString()));
                    to.Clear();
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 Bye");
                    return;
                default:
                    await writer.WriteLineAsync("250 OK");
                    break;
            }
        }
    }

    private static Email Parse(IReadOnlyList<string> to, string raw)
    {
        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        // Folded header lines continue with whitespace
        var headers = Regex.Replace(raw[..split], @"\r\n[ \t]+", " ");
        var body = raw[(split + 4)..];

        string Header(string name) =>
            Regex.Match(headers, $@"^{name}:\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();

        var subject = Regex.Replace(Header("Subject"), @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=\s*", m =>
        {
            var encoding = Encoding.GetEncoding(m.Groups[1].Value);
            return m.Groups[2].Value.ToUpperInvariant() == "B"
                ? encoding.GetString(Convert.FromBase64String(m.Groups[3].Value))
                : QuotedPrintable(m.Groups[3].Value.Replace('_', ' '), encoding);
        });

        body = Header("Content-Transfer-Encoding").ToLowerInvariant() switch
        {
            "base64" => Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(body, @"\s", ""))),
            "quoted-printable" => QuotedPrintable(Regex.Replace(body, "=\r\n", ""), Encoding.UTF8),
            _ => body,
        };
        return new Email(to, subject, body.Replace("\r\n", "\n"));
    }

    private static string QuotedPrintable(string text, Encoding encoding)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '=' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
                bytes.AddRange(encoding.GetBytes(text[i].ToString()));
        }
        return encoding.GetString(bytes.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        await accepting;
    }
}
