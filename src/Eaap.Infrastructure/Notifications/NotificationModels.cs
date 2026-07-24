using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eaap.Infrastructure.Notifications;

/// <summary>Canonical webhook payload (build spec phase 4 section 6).</summary>
public record NotificationPayload(
    string Event,
    Guid JobId,
    Guid RepositoryId,
    string RepositoryName,
    string Status,
    string SummaryUrl,
    DateTimeOffset OccurredAt);

/// <summary>HMAC-SHA256 request signing, as verified by webhook receivers.</summary>
public static class HmacSigner
{
    /// <summary>Returns "sha256=&lt;hex&gt;" over the exact body bytes with the channel secret.</summary>
    public static string Sign(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>Per-channel config parsed from NotificationChannel.ConfigJson.</summary>
public record ChannelConfig
{
    public string? Url { get; init; }
    public string? WebhookUrl { get; init; }
    public string? Secret { get; init; }
    public string? To { get; init; }

    public static ChannelConfig Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ChannelConfig>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new ChannelConfig();
        }
        catch
        {
            return new ChannelConfig();
        }
    }
}

/// <summary>Renders channel-specific bodies. Pure, so formatting is unit-testable.</summary>
public static class NotificationFormatter
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string WebhookBody(NotificationPayload payload) => JsonSerializer.Serialize(payload, Web);

    /// <summary>Minimal Slack Block Kit message: a title plus three fields.</summary>
    public static string SlackBody(NotificationPayload payload)
    {
        var message = new
        {
            text = $"EAAP: {payload.Event} — {payload.RepositoryName}",
            blocks = new object[]
            {
                new { type = "header", text = new { type = "plain_text", text = $"EAAP: {payload.Event}" } },
                new
                {
                    type = "section",
                    fields = new object[]
                    {
                        new { type = "mrkdwn", text = $"*Repository:*\n{payload.RepositoryName}" },
                        new { type = "mrkdwn", text = $"*Status:*\n{payload.Status}" },
                        new { type = "mrkdwn", text = $"*Job:*\n<{payload.SummaryUrl}|{payload.JobId}>" }
                    }
                }
            }
        };
        return JsonSerializer.Serialize(message, Web);
    }

    public static string EmailSubject(NotificationPayload payload) =>
        $"[EAAP] {payload.Event}: {payload.RepositoryName} ({payload.Status})";

    public static string EmailHtml(NotificationPayload payload) =>
        $"""
        <html><body style="font-family:sans-serif">
          <h2>EAAP: {payload.Event}</h2>
          <p><b>Repository:</b> {payload.RepositoryName}<br/>
             <b>Status:</b> {payload.Status}<br/>
             <b>Job:</b> <a href="{payload.SummaryUrl}">{payload.JobId}</a><br/>
             <b>When:</b> {payload.OccurredAt:u}</p>
        </body></html>
        """;
}
