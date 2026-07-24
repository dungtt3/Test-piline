using System.Text.Json;
using System.Text.Json.Serialization;
using Eaap.Api.Auth;
using Eaap.Api.Endpoints;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure;
using Eaap.Application;
using Eaap.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEaapInfrastructure(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(EaapAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, EaapAuthHandler>(EaapAuthHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Maintainer, p => p.RequireRole(
        nameof(UserRoleType.Maintainer), nameof(UserRoleType.Admin)))
    .AddPolicy(Policies.Admin, p => p.RequireRole(nameof(UserRoleType.Admin)));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "EAAP API",
        Version = "v1",
        Description = "Engineering Analysis Automation Platform"
    });
    // Swagger "Authorize" button: paste a JWT or an eaap_ API token.
    var scheme = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
    };
    options.AddSecurityDefinition("Bearer", scheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = [] });
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

await SeedAdminAsync(app);

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = WriteHealthResponse
});

app.MapAuthEndpoints();

// Everything under /api/v1 requires authentication; write endpoints add role policies.
var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapRepositoryEndpoints();
api.MapJobEndpoints();
api.MapUserEndpoints();

app.Run();

// Creates the seed Admin when the User table is empty and admin credentials are configured.
static async Task SeedAdminAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var options = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.AdminEmail) || string.IsNullOrWhiteSpace(options.AdminPassword))
    {
        return;
    }

    var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
    if (!await db.Database.CanConnectAsync() || await db.Users.AnyAsync())
    {
        return;
    }

    var tokens = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
    var admin = new User
    {
        Id = Guid.NewGuid(),
        Email = options.AdminEmail,
        PasswordHash = tokens.HashPassword(options.AdminPassword),
        DisplayName = "Administrator",
        CreatedAt = DateTimeOffset.UtcNow,
        Roles = [new UserRole { Id = Guid.NewGuid(), Role = UserRoleType.Admin }]
    };
    db.Users.Add(admin);
    await db.SaveChangesAsync();
}

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
