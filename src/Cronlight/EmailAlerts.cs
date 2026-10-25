using System.Net;
using System.Net.Mail;
using System.Text;

namespace Cronlight;

/// <summary>
/// Sends alerts by email through an SMTP server. Uses .NET's SmtpClient, so the server must accept plain
/// connections or STARTTLS (usually port 587, as Office 365, SendGrid and Amazon SES do); implicit TLS on
/// port 465 is not supported.
/// </summary>
public class CronlightEmailAlertOptions
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    /// <summary>Upgrade the connection with STARTTLS. Turn off only for a relay on a trusted network.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>Sign in with these when set. Keep the password in configuration or a key vault, not code.</summary>
    public string? UserName { get; set; }

    public string? Password { get; set; }

    /// <summary>The sender address, e.g. "Cronlight &lt;cronlight@contoso.com&gt;".</summary>
    public string From { get; set; } = "";

    public List<string> To { get; } = new();
}

internal static class EmailAlerts
{
    /// <summary>What is missing or wrong in the settings, or null when email can be sent.</summary>
    public static string? Problem(CronlightEmailAlertOptions email) =>
        string.IsNullOrWhiteSpace(email.Host) ? "no SMTP Host is set"
        : email.Port is < 1 or > 65535 ? $"Port {email.Port} is not between 1 and 65535"
        : !MailAddress.TryCreate(email.From, out _) ? "no valid From address is set"
        : email.To.Count == 0 ? "no To address is set"
        : email.To.FirstOrDefault(to => !MailAddress.TryCreate(to, out _)) is { } bad ? $"To address '{bad}' is not valid"
        : null;

    /// <summary>Sends the alert, retrying a few times while the server is unreachable or busy.</summary>
    public static async Task Send(CronlightEmailAlertOptions email, CronlightAlert alert, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var message = Message(email, alert);
                using var client = new SmtpClient(email.Host, email.Port) { EnableSsl = email.UseTls };
                if (email.UserName != null)
                    client.Credentials = new NetworkCredential(email.UserName, email.Password);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await client.SendMailAsync(message, timeout.Token);
                return;
            }
            // A refused recipient will be refused again; anything else may be the server being briefly unavailable
            catch (Exception ex) when (attempt < 3 && !cancellationToken.IsCancellationRequested
                && ex is (SmtpException and not SmtpFailedRecipientException) or OperationCanceledException or IOException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
        }
    }

    public static MailMessage Message(CronlightEmailAlertOptions email, CronlightAlert alert)
    {
        var message = new MailMessage
        {
            From = new MailAddress(email.From),
            // Job names come from users; line breaks in a subject would be refused by MailMessage
            Subject = $"[Cronlight] {OneLine(alert.Title)}",
            SubjectEncoding = Encoding.UTF8,
            Body = Body(alert),
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        foreach (var to in email.To)
            message.To.Add(to);
        return message;
    }

    /// <summary>Plain text, so nothing a job name or error message contains is read as markup.</summary>
    private static string Body(CronlightAlert alert)
    {
        var body = new StringBuilder()
            .AppendLine(alert.Title)
            .AppendLine()
            .AppendLine($"Job:       {alert.JobGroup}.{alert.JobName}")
            .AppendLine($"Type:      {alert.JobType ?? "unknown"}")
            .AppendLine($"Node:      {alert.InstanceName}")
            .AppendLine($"Scheduler: {alert.SchedulerName}")
            .AppendLine($"Time:      {alert.TimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
        if (alert.Error != null)
            body.AppendLine().AppendLine($"{alert.ErrorLabel}:").AppendLine(alert.Error);
        if (alert.DashboardUrl != null)
            body.AppendLine().AppendLine($"Open Cronlight: {alert.DashboardUrl}");
        return body.ToString();
    }

    private static string OneLine(string text) =>
        new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
