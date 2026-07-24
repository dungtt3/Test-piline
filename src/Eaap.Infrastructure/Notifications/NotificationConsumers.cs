using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Eaap.Infrastructure.Notifications;

/// <summary>
/// Turns platform events into per-channel delivery requests (build spec phase 4 section 6). Each
/// matching, enabled channel gets its own message so deliveries retry independently.
/// </summary>
public class NotificationTriggerConsumer(EaapDbContext db, ILogger<NotificationTriggerConsumer> logger)
    : IConsumer<GateEvaluated>, IConsumer<JobFinished>, IConsumer<NewCriticalSecurityFound>
{
    public Task Consume(ConsumeContext<GateEvaluated> context) =>
        context.Message.Passed
            ? Task.CompletedTask
            : FanOutAsync(context, context.Message.JobId, "GateFailed", "GateFailed");

    public Task Consume(ConsumeContext<JobFinished> context) =>
        context.Message.Status == "Failed"
            ? FanOutAsync(context, context.Message.JobId, "JobFailed", "JobFailed")
            : Task.CompletedTask;

    public Task Consume(ConsumeContext<NewCriticalSecurityFound> context) =>
        FanOutAsync(context, context.Message.JobId, "NewCriticalSecurity",
            $"{context.Message.Count} new critical");

    private async Task FanOutAsync(ConsumeContext context, Guid jobId, string trigger, string status)
    {
        var job = await db.AnalysisJobs
            .Include(j => j.Snapshot)!.ThenInclude(s => s!.Repository)
            .FirstOrDefaultAsync(j => j.Id == jobId, context.CancellationToken);
        if (job?.Snapshot?.Repository is not { } repository)
        {
            return;
        }

        var channels = await db.NotificationChannels
            .Where(c => c.Enabled && (c.RepositoryId == null || c.RepositoryId == repository.Id))
            .ToListAsync(context.CancellationToken);

        var occurredAt = DateTimeOffset.UtcNow;
        var repoName = RepositoryName(repository);
        foreach (var channel in channels.Where(c => c.Triggers.Contains(trigger)))
        {
            await context.Publish(new NotificationDeliveryRequested(
                channel.Id, trigger, jobId, repository.Id, repoName, status, occurredAt),
                context.CancellationToken);
        }

        logger.LogInformation("Fanned out {Trigger} for job {JobId} to matching channels", trigger, jobId);
    }

    public static string RepositoryName(Repository repository)
    {
        var url = repository.CloneUrl.TrimEnd('/');
        var slash = url.LastIndexOf('/');
        var name = slash >= 0 && slash < url.Length - 1 ? url[(slash + 1)..] : url;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

/// <summary>Delivers one notification; throwing lets the endpoint's retry policy re-attempt.</summary>
public class NotificationDeliveryConsumer(
    EaapDbContext db,
    NotificationDispatcher dispatcher,
    ILogger<NotificationDeliveryConsumer> logger) : IConsumer<NotificationDeliveryRequested>
{
    public async Task Consume(ConsumeContext<NotificationDeliveryRequested> context)
    {
        var message = context.Message;
        var channel = await db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == message.ChannelId, context.CancellationToken);
        if (channel is null || !channel.Enabled)
        {
            return; // channel deleted or disabled since fan-out
        }

        var payload = dispatcher.BuildPayload(channel, message);
        await dispatcher.DeliverAsync(channel, payload, context.CancellationToken);
        logger.LogInformation("Delivered {Event} to channel {ChannelId} (attempt {Attempt})",
            message.Event, channel.Id, context.GetRetryAttempt() + 1);
    }
}

/// <summary>
/// Runs when a delivery has exhausted all retries (MassTransit publishes a Fault): records the
/// failure in NotificationDeliveryLog for troubleshooting.
/// </summary>
public class NotificationFaultConsumer(EaapDbContext db, ILogger<NotificationFaultConsumer> logger)
    : IConsumer<Fault<NotificationDeliveryRequested>>
{
    public async Task Consume(ConsumeContext<Fault<NotificationDeliveryRequested>> context)
    {
        var message = context.Message.Message;
        db.NotificationDeliveryLogs.Add(new NotificationDeliveryLog
        {
            Id = Guid.NewGuid(),
            ChannelId = message.ChannelId,
            Event = message.Event,
            JobId = message.JobId,
            Success = false,
            Attempts = 1 + (context.Message.Exceptions?.Length ?? 1),
            Error = context.Message.Exceptions?.FirstOrDefault()?.Message ?? "delivery failed",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(context.CancellationToken);
        logger.LogError("Notification to channel {ChannelId} failed after all retries", message.ChannelId);
    }
}
