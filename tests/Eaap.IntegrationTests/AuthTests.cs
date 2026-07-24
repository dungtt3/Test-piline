using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eaap.IntegrationTests;

[Collection("eaap")]
public class AuthTests(EaapApiFactory factory)
{
    // --- RBAC matrix (via the role-injecting test handler) ---

    [Fact]
    public async Task Read_IsAllowedForViewer()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Viewer");

        var response = await client.GetAsync("/api/v1/repositories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Write_IsForbiddenForViewer_ButAllowedForMaintainer()
    {
        var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Add("X-Test-Role", "Viewer");
        var forbidden = await viewer.PostAsJsonAsync("/api/v1/repositories",
            new { provider = "GitHub", cloneUrl = "https://example.invalid/r.git", defaultBranch = "main" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var maintainer = factory.CreateClient();
        maintainer.DefaultRequestHeaders.Add("X-Test-Role", "Maintainer");
        var created = await maintainer.PostAsJsonAsync("/api/v1/repositories",
            new { provider = "GitHub", cloneUrl = "https://example.invalid/r.git", defaultBranch = "main" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task UserManagement_IsAdminOnly()
    {
        var maintainer = factory.CreateClient();
        maintainer.DefaultRequestHeaders.Add("X-Test-Role", "Maintainer");
        Assert.Equal(HttpStatusCode.Forbidden, (await maintainer.GetAsync("/api/v1/users")).StatusCode);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_Gets401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        var response = await client.GetAsync("/api/v1/repositories");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Real login / JWT issuance ---

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsUsableJwt()
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        await SeedUserAsync(email, "correct-horse-battery", UserRoleType.Maintainer);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = "correct-horse-battery" });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jwt = body.GetProperty("token").GetString()!;
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());

        // The issued JWT validates and carries the user's role.
        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var principal = tokens.ValidateJwt(jwt);
        Assert.NotNull(principal);
        Assert.True(principal.IsInRole("Maintainer"));
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        await SeedUserAsync(email, "the-right-password", UserRoleType.Viewer);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = "the-wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task SeedUserAsync(string email, string password, UserRoleType role)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = tokens.HashPassword(password),
            DisplayName = "Test User",
            CreatedAt = DateTimeOffset.UtcNow,
            Roles = [new UserRole { Id = Guid.NewGuid(), Role = role }]
        });
        await db.SaveChangesAsync();
    }
}
