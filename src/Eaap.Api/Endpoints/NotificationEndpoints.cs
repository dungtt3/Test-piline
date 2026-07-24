using System.Text.Json;
using Eaap.Api.Auth;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Notifications;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Eaap.Api.Endpoints;

public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder group)
    {
        var repositories = group.MapGroup("/repositories").WithTags("Notifications");

        repositories.MapGet("/{id:guid}/notifications", async (Guid id, EaapDbContext db, CancellationToken ct) =>
        {
            if (!await db.Repositories.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }
            var channels = await db.NotificationChannels.AsNoTracking()
                .Where(c => c.RepositoryId == id)
                .Select(c => new NotificationChannelDto(
                    c.Id, c.RepositoryId, c.Type.ToString(), c.Triggers.ToArray(), c.Enabled, c.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(channels);
        })
        .WithSummary("List a repository's notification channels");

        repositories.MapPost("/{id:guid}/notifications", async (
            Guid id, CreateNotificationRequest request, EaapDbContext db, CancellationToken ct) =>
        {
            if (!await db.Repositories.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }
            if (!Enum.TryParse<NotificationType>(request.Type, ignoreCase: true, out var type))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Type)] = ["type must be Webhook, Slack or Email."]
                });
            }

            var channel = new NotificationChannel
            {
                Id = Guid.NewGuid(),
                RepositoryId = id,
                Type = type,
                ConfigJson = JsonSerializer.Serialize(request.Config ?? []),
                Triggers = [.. request.Triggers ?? []],
                Enabled = request.Enabled,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.NotificationChannels.Add(channel);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/repositories/{id}/notifications/{channel.Id}",
                new NotificationChannelDto(channel.Id, channel.RepositoryId, channel.Type.ToString(),
                    channel.Triggers.ToArray(), channel.Enabled, channel.CreatedAt));
        })
        .RequireAuthorization(Policies.Maintainer)
        .WithSummary("Create a notification channel for a repository")
        .Produces<NotificationChannelDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status404NotFound)
        .ProducesValidationProblem();

        repositories.MapDelete("/{id:guid}/notifications/{channelId:guid}", async (
            Guid id, Guid channelId, EaapDbContext db, CancellationToken ct) =>
        {
            var channel = await db.NotificationChannels.FirstOrDefaultAsync(
                c => c.Id == channelId && c.RepositoryId == id, ct);
            if (channel is null)
            {
                return Results.NotFound();
            }
            db.NotificationChannels.Remove(channel);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Policies.Maintainer)
        .WithSummary("Delete a notification channel")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        // Send a sample payload to verify a channel is wired correctly.
        var notifications = group.MapGroup("/notifications").WithTags("Notifications");
        notifications.MapPost("/{channelId:guid}/test", async (
            Guid channelId, EaapDbContext db, IPublishEndpoint publish, CancellationToken ct) =>
        {
            var channel = await db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == channelId, ct);
            if (channel is null)
            {
                return Results.NotFound();
            }

            var repositoryName = "test-repository";
            if (channel.RepositoryId is { } repoId)
            {
                var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == repoId, ct);
                if (repo is not null)
                {
                    repositoryName = NotificationTriggerConsumer.RepositoryName(repo);
                }
            }

            await publish.Publish(new NotificationDeliveryRequested(
                channel.Id, "Test", Guid.Empty, channel.RepositoryId ?? Guid.Empty,
                repositoryName, "Test", DateTimeOffset.UtcNow), ct);
            return Results.Accepted();
        })
        .RequireAuthorization(Policies.Maintainer)
        .WithSummary("Send a sample payload to a channel")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status404NotFound);

        return group;
    }
}
