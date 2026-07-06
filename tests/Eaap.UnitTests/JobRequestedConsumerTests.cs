using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure;
using Eaap.Infrastructure.Consumers;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Eaap.UnitTests;

public class JobRequestedConsumerTests
{
    private static async Task<(ServiceProvider Provider, Guid JobId)> BuildAsync(IArgoClient argoClient)
    {
        var databaseName = "jobs-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<EaapDbContext>(o => o.UseInMemoryDatabase(databaseName));
        services.AddSingleton(argoClient);
        services.Configure<AdapterOptions>(o =>
            o.Registry["megalinter"] = new AdapterEntry { Image = "eaap/adapter-megalinter:latest" });
        services.AddMassTransitTestHarness(bus => bus.AddConsumer<JobRequestedConsumer>());

        var provider = services.BuildServiceProvider(validateScopes: true);

        Guid jobId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var repository = new Repository
            {
                Id = Guid.NewGuid(),
                Provider = GitProvider.GenericGit,
                CloneUrl = "https://example.invalid/repo.git",
                CreatedAt = DateTimeOffset.UtcNow
            };
            var snapshot = new Snapshot
            {
                Id = Guid.NewGuid(),
                RepositoryId = repository.Id,
                Branch = "main",
                CommitSha = new string('b', 40),
                StoragePath = "snapshots/test.tar.gz",
                SizeBytes = 10,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var job = new AnalysisJob
            {
                Id = Guid.NewGuid(),
                SnapshotId = snapshot.Id,
                Status = JobStatus.Pending,
                RequestedAnalyzers = ["megalinter"],
                CreatedAt = DateTimeOffset.UtcNow,
                AnalyzerRuns =
                [
                    new AnalyzerRun { Id = Guid.NewGuid(), AnalyzerId = "megalinter", Status = AnalyzerRunStatus.Pending }
                ]
            };
            db.AddRange(repository, snapshot, job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        return (provider, jobId);
    }

    [Fact]
    public async Task JobRequested_SubmitsWorkflow_SetsRunning_PublishesJobStarted()
    {
        var argoClient = Substitute.For<IArgoClient>();
        argoClient.SubmitAnalysisWorkflowAsync(Arg.Any<ArgoSubmitRequest>(), Arg.Any<CancellationToken>())
            .Returns("eaap-megalinter-abc12");

        var (provider, jobId) = await BuildAsync(argoClient);
        await using var _ = provider;
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new JobRequested(jobId, Guid.NewGuid(), ["megalinter"]));
            Assert.True(await harness.Consumed.Any<JobRequested>());
            Assert.True(await harness.Published.Any<JobStarted>());

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var job = await db.AnalysisJobs.Include(j => j.AnalyzerRuns).SingleAsync(j => j.Id == jobId);
            Assert.Equal(JobStatus.Running, job.Status);
            Assert.Equal("eaap-megalinter-abc12", job.ArgoWorkflowName);
            Assert.NotNull(job.StartedAt);
            Assert.Equal(AnalyzerRunStatus.Running, job.AnalyzerRuns.Single().Status);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task JobRequested_SubmitFails_MarksJobFailed_PublishesJobFinished()
    {
        var argoClient = Substitute.For<IArgoClient>();
        argoClient.SubmitAnalysisWorkflowAsync(Arg.Any<ArgoSubmitRequest>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new HttpRequestException("argo unreachable"));

        var (provider, jobId) = await BuildAsync(argoClient);
        await using var _ = provider;
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new JobRequested(jobId, Guid.NewGuid(), ["megalinter"]));
            Assert.True(await harness.Consumed.Any<JobRequested>());
            Assert.True(await harness.Published.Any<JobFinished>());

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var job = await db.AnalysisJobs.Include(j => j.AnalyzerRuns).SingleAsync(j => j.Id == jobId);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.All(job.AnalyzerRuns, r => Assert.Equal(AnalyzerRunStatus.Failed, r.Status));
        }
        finally
        {
            await harness.Stop();
        }
    }
}
