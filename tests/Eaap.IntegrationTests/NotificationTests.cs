using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Server;
using WmRequest = WireMock.RequestBuilders.Request;
using WmResponse = WireMock.ResponseBuilders.Response;

namespace Eaap.IntegrationTests;

[Collection("eaap")]
public class NotificationTests(EaapApiFactory factory)
{
    private const string Secret = "channel-signing-secret";

    [Fact]
    public async Task GateFailed_DeliversSignedWebhook_ToMatchingChannel()
    {
        using var receiver = WireMockServer.Start();
        receiver.Given(WmRequest.Create().WithPath("/hook").UsingPost())
            .RespondWith(WmResponse.Create().WithStatusCode(200));

        var (_, jobId) = await SeedJobAsync();
        await CreateWebhookChannelAsync(jobId, receiver.Url + "/hook", Secret, enabled: true, trigger: "GateFailed");

        await Publish(new GateEvaluated(jobId, Passed: false, "quality-gate/default"));

        var entry = await WaitForRequestAsync(receiver);
        var body = entry.RequestMessage.Body!;

        // Payload schema.
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("GateFailed", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(jobId, doc.RootElement.GetProperty("jobId").GetGuid());

        // HMAC signature verifies with the channel secret.
        var signature = entry.RequestMessage.Headers!["X-Eaap-Signature"][0];
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var expected = "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal(expected, signature);
    }

    [Fact]
    public async Task DisabledChannel_ReceivesNothing()
    {
        using var receiver = WireMockServer.Start();
        receiver.Given(WmRequest.Create().WithPath("/hook").UsingPost())
            .RespondWith(WmResponse.Create().WithStatusCode(200));

        var (_, jobId) = await SeedJobAsync();
        await CreateWebhookChannelAsync(jobId, receiver.Url + "/hook", Secret, enabled: false, trigger: "GateFailed");

        await Publish(new GateEvaluated(jobId, Passed: false, "quality-gate/default"));

        // Give the pipeline time; the disabled channel must stay silent.
        await Task.Delay(2000);
        Assert.Empty(receiver.LogEntries);
    }

    [Fact]
    public async Task Delivery_RetriesAfterTwo500s_ThenSucceeds()
    {
        using var receiver = WireMockServer.Start();
        // First mapping omits WhenStateIs so it matches the scenario's initial state.
        receiver.Given(WmRequest.Create().WithPath("/hook").UsingPost())
            .InScenario("retry").WillSetStateTo("s1")
            .RespondWith(WmResponse.Create().WithStatusCode(500));
        receiver.Given(WmRequest.Create().WithPath("/hook").UsingPost())
            .InScenario("retry").WhenStateIs("s1").WillSetStateTo("s2")
            .RespondWith(WmResponse.Create().WithStatusCode(500));
        receiver.Given(WmRequest.Create().WithPath("/hook").UsingPost())
            .InScenario("retry").WhenStateIs("s2")
            .RespondWith(WmResponse.Create().WithStatusCode(200));

        var (_, jobId) = await SeedJobAsync();
        var channelId = await CreateWebhookChannelAsync(jobId, receiver.Url + "/hook", Secret, enabled: true, trigger: "GateFailed");

        await Publish(new GateEvaluated(jobId, Passed: false, "quality-gate/default"));

        // Three attempts: two 500s then a 200.
        await WaitUntilAsync(() => receiver.LogEntries.Count() >= 3, timeoutSeconds: 30);

        // Succeeded, so no failure log row.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        Assert.False(await db.NotificationDeliveryLogs.AnyAsync(l => l.ChannelId == channelId));
    }

    private static async Task<WireMock.Logging.ILogEntry> WaitForRequestAsync(WireMockServer server, int timeoutSeconds = 20)
    {
        await WaitUntilAsync(() => server.LogEntries.Any(), timeoutSeconds);
        return server.LogEntries.First();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }

    private Task Publish(object message) => factory.Services.GetRequiredService<IBus>().Publish(message);

    private async Task<Guid> CreateWebhookChannelAsync(Guid jobId, string url, string secret, bool enabled, string trigger)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        var repositoryId = await db.AnalysisJobs.Where(j => j.Id == jobId)
            .Select(j => j.Snapshot!.RepositoryId).FirstAsync();

        var channel = new NotificationChannel
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            Type = NotificationType.Webhook,
            ConfigJson = JsonSerializer.Serialize(new { url, secret }),
            Triggers = [trigger],
            Enabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private async Task<(Guid RepositoryId, Guid JobId)> SeedJobAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://github.com/acme/notify.git",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            Branch = "main",
            CommitSha = Guid.NewGuid().ToString("N") + "55555555",
            StoragePath = "snapshots/notify.tar.gz",
            SizeBytes = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SnapshotId = snapshot.Id,
            Status = JobStatus.GateFailed,
            RequestedAnalyzers = ["megalinter"],
            CreatedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(repository, snapshot, job);
        await db.SaveChangesAsync();
        return (repository.Id, job.Id);
    }
}
