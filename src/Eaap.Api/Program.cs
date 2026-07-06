using System.Text.Json;
using System.Text.Json.Serialization;
using Eaap.Api.Endpoints;
using Eaap.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEaapInfrastructure(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "EAAP API",
        Version = "v1",
        Description = "Engineering Analysis Automation Platform — Phase 1"
    });
});

var rabbit = builder.Configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
var minio = builder.Configuration.GetSection(MinioOptions.SectionName).Get<MinioOptions>() ?? new MinioOptions();
var opa = builder.Configuration.GetSection(OpaOptions.SectionName).Get<OpaOptions>() ?? new OpaOptions();

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Postgres") ?? string.Empty, name: "postgres")
    .AddRabbitMQ(rabbit.ToUri(), name: "rabbitmq")
    .AddUrlGroup(new Uri(new Uri(minio.Endpoint), "/minio/health/live"), name: "minio")
    .AddUrlGroup(new Uri(new Uri(opa.BaseUrl), "/health"), name: "opa");

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = WriteHealthResponse
});

var api = app.MapGroup("/api/v1");
api.MapRepositoryEndpoints();
api.MapJobEndpoints();

app.Run();

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    var payload = JsonSerializer.Serialize(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.ToDictionary(
            e => e.Key,
            e => new { status = e.Value.Status.ToString(), error = e.Value.Exception?.Message })
    });
    return context.Response.WriteAsync(payload);
}

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
