using System.Net.Http.Headers;
using System.Text;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Eaap.Infrastructure.Notifications;

/// <summary>Delivers a notification to one channel according to its type. Throws on failure so the
/// caller's retry policy can re-attempt.</summary>
public class NotificationDispatcher(HttpClient httpClient, IOptions<NotificationOptions> options)
{
    private readonly NotificationOptions _options = options.Value;

    public NotificationPayload BuildPayload(NotificationChannel channel, NotificationDeliveryRequested message)
    {
        _ = channel;
        var summaryUrl = $"{_options.PlatformBaseUrl.TrimEnd('/')}/api/v1/jobs/{message.JobId}";
        return new NotificationPayload(
            message.Event, message.JobId, message.RepositoryId, message.RepositoryName,
            message.Status, summaryUrl, message.OccurredAt);
    }

    public async Task DeliverAsync(NotificationChannel channel, NotificationPayload payload, CancellationToken ct)
    {
        var config = ChannelConfig.Parse(channel.ConfigJson);
        switch (channel.Type)
        {
            case NotificationType.Webhook:
                await PostWebhookAsync(config, payload, ct);
                break;
            case NotificationType.Slack:
                await PostSlackAsync(config, payload, ct);
                break;
            case NotificationType.Email:
                await SendEmailAsync(config, payload, ct);
                break;
            default:
                throw new NotSupportedException($"Unknown channel type {channel.Type}.");
        }
    }

    private async Task PostWebhookAsync(ChannelConfig config, NotificationPayload payload, CancellationToken ct)
    {
        var url = config.Url ?? throw new InvalidOperationException("Webhook channel has no url.");
        var body = NotificationFormatter.WebhookBody(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Eaap-Event", payload.Event);
        if (!string.IsNullOrEmpty(config.Secret))
        {
            request.Headers.TryAddWithoutValidation("X-Eaap-Signature", HmacSigner.Sign(config.Secret, body));
        }

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task PostSlackAsync(ChannelConfig config, NotificationPayload payload, CancellationToken ct)
    {
        var url = config.WebhookUrl ?? config.Url ?? throw new InvalidOperationException("Slack channel has no webhookUrl.");
        var body = NotificationFormatter.SlackBody(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendEmailAsync(ChannelConfig config, NotificationPayload payload, CancellationToken ct)
    {
        var to = config.To ?? throw new InvalidOperationException("Email channel has no 'to'.");
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.SmtpFrom));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = NotificationFormatter.EmailSubject(payload);
        message.Body = new TextPart("html") { Text = NotificationFormatter.EmailHtml(payload) };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_options.SmtpHost, _options.SmtpPort, MailKit.Security.SecureSocketOptions.Auto, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
