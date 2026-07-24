using Amazon.S3;
using Eaap.Application;
using Eaap.Infrastructure.Argo;
using Eaap.Infrastructure.Git;
using Eaap.Infrastructure.Ingestion;
using Eaap.Infrastructure.Opa;
using Eaap.Infrastructure.Persistence;
using Eaap.Infrastructure.Snapshots;
using Eaap.Infrastructure.Storage;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Eaap.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEaapInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.Configure<MinioOptions>(configuration.GetSection(MinioOptions.SectionName));
        services.Configure<OpaOptions>(configuration.GetSection(OpaOptions.SectionName));
        services.Configure<ArgoOptions>(configuration.GetSection(ArgoOptions.SectionName));
        services.Configure<AdapterOptions>(options =>
            configuration.GetSection(AdapterOptions.SectionName).Bind(options.Registry));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.AddSingleton<IAuthTokenService, Auth.AuthTokenService>();

        // EnableDynamicJson is required by Npgsql 8+ to map CLR collections/POCOs to jsonb columns.
        services.AddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(configuration.GetConnectionString("Postgres"));
            dataSourceBuilder.EnableDynamicJson();
            return dataSourceBuilder.Build();
        });
        services.AddDbContext<EaapDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        var minio = configuration.GetSection(MinioOptions.SectionName).Get<MinioOptions>() ?? new MinioOptions();
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
            minio.AccessKey,
            minio.SecretKey,
            new AmazonS3Config
            {
                ServiceURL = minio.Endpoint,
                ForcePathStyle = true
            }));
        services.AddScoped<IObjectStorage, MinioObjectStorage>();
        services.AddScoped<IGitClient, GitCliClient>();
        services.AddScoped<ISnapshotService, SnapshotService>();
        services.AddScoped<SarifIngestionService>();
        services.AddScoped<MetricsIngestionService>();
        services.AddScoped<Baselines.BaselineService>();
        services.AddScoped<Trends.TrendService>();
        services.AddScoped<IRepoConfigReader, RepoConfig.RepoConfigReader>();

        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.Configure<WebhookOptions>(configuration.GetSection(WebhookOptions.SectionName));
        services.AddHttpClient<Notifications.NotificationDispatcher>();

        var opa = configuration.GetSection(OpaOptions.SectionName).Get<OpaOptions>() ?? new OpaOptions();
        services.AddHttpClient<IQualityGate, OpaQualityGate>(client =>
            client.BaseAddress = new Uri(opa.BaseUrl));

        services.AddHttpClient<IArgoClient, ArgoClient>();
        services.AddHostedService<ArgoPollingService>();

        var rabbit = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();
            bus.AddConsumer<Consumers.AnalyzerRunFinishedConsumer>();
            bus.AddConsumer<Consumers.JobRequestedConsumer>();
            bus.AddConsumer<Notifications.NotificationTriggerConsumer>();
            bus.AddConsumer<Notifications.NotificationDeliveryConsumer, Notifications.NotificationDeliveryConsumerDefinition>();
            bus.AddConsumer<Notifications.NotificationFaultConsumer>();

            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(rabbit.Host, (ushort)rabbit.Port, "/", host =>
                {
                    host.Username(rabbit.Username);
                    host.Password(rabbit.Password);
                });
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
