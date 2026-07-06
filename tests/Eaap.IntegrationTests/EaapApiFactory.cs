using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Eaap.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Eaap.IntegrationTests;

/// <summary>
/// Boots the real API against disposable Postgres, RabbitMQ and MinIO containers.
/// Shared across tests via a collection fixture to pay the container cost once.
/// </summary>
public class EaapApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Bucket = "eaap";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder()
        .WithImage("rabbitmq:3-management")
        .WithUsername("eaap")
        .WithPassword("eaap-test")
        .Build();

    private readonly MinioContainer _minio = new MinioBuilder()
        .WithUsername("eaap")
        .WithPassword("eaap-test-secret")
        .Build();

    private readonly IContainer _opa = new ContainerBuilder()
        .WithImage("openpolicyagent/opa:latest")
        .WithResourceMapping(
            new FileInfo(Path.Combine(AppContext.BaseDirectory, "policies", "default.rego")),
            "/policies/")
        .WithCommand("run", "--server", "--addr", ":8181", "/policies")
        .WithPortBinding(8181, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(8181).ForPath("/health")))
        .Build();

    public string OpaBaseUrl => $"http://{_opa.Hostname}:{_opa.GetMappedPublicPort(8181)}";

    public IAmazonS3 CreateS3Client() => new AmazonS3Client(
        "eaap",
        "eaap-test-secret",
        new AmazonS3Config
        {
            ServiceURL = _minio.GetConnectionString(),
            ForcePathStyle = true
        });

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync(), _minio.StartAsync(), _opa.StartAsync());

        using (var s3 = CreateS3Client())
        {
            await s3.PutBucketAsync(Bucket);
        }

        // Accessing Services builds the host with the container-backed configuration below.
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EaapDbContext>().Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting flows into the host configuration before Program.cs top-level code
        // reads it (ConfigureAppConfiguration would be applied too late for minimal APIs).
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("RabbitMq:Host", _rabbitMq.Hostname);
        builder.UseSetting("RabbitMq:Port", _rabbitMq.GetMappedPublicPort(5672).ToString());
        builder.UseSetting("RabbitMq:Username", "eaap");
        builder.UseSetting("RabbitMq:Password", "eaap-test");
        builder.UseSetting("Minio:Endpoint", _minio.GetConnectionString());
        builder.UseSetting("Minio:AccessKey", "eaap");
        builder.UseSetting("Minio:SecretKey", "eaap-test-secret");
        builder.UseSetting("Minio:Bucket", Bucket);
        builder.UseSetting("Opa:BaseUrl", OpaBaseUrl);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _rabbitMq.DisposeAsync().AsTask(),
            _minio.DisposeAsync().AsTask(),
            _opa.DisposeAsync().AsTask());
    }
}

[CollectionDefinition("eaap")]
public class EaapCollection : ICollectionFixture<EaapApiFactory>;
