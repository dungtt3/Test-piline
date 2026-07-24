using System.Security.Claims;
using Eaap.Application;
using Eaap.Api.Auth;
using Eaap.Domain.Entities;
using Eaap.Infrastructure;
using Eaap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Eaap.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/login", async (
            LoginRequest request, EaapDbContext db, IAuthTokenService tokens,
            IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            var user = await db.Users.Include(u => u.Roles)
                .FirstOrDefaultAsync(u => u.Email == request.Email, ct);
            if (user is null || !tokens.VerifyPassword(request.Password, user.PasswordHash))
            {
                return Results.Json(new { message = "Invalid credentials." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var jwt = tokens.CreateJwt(user, [.. user.Roles.Select(r => r.Role)]);
            return Results.Ok(new LoginResponse(jwt, "Bearer", options.Value.TokenTtlHours));
        })
        .AllowAnonymous()
        .WithSummary("Log in with email + password and receive a JWT")
        .Produces<LoginResponse>()
        .Produces(StatusCodes.Status401Unauthorized);

        // Issue a CI API token for the calling user (any authenticated role).
        auth.MapPost("/tokens", async (
            CreateTokenRequest request, ClaimsPrincipal principal, EaapDbContext db,
            IAuthTokenService tokens, CancellationToken ct) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var (plaintext, hash) = tokens.GenerateApiToken();
            var token = new ApiToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = hash,
                Name = string.IsNullOrWhiteSpace(request.Name) ? "ci-token" : request.Name.Trim(),
                ExpiresAt = request.ExpiresAt,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ApiTokens.Add(token);
            await db.SaveChangesAsync(ct);

            // The plaintext is shown exactly once.
            return Results.Created($"/auth/tokens/{token.Id}",
                new CreateTokenResponse(token.Id, plaintext, token.Name, token.ExpiresAt));
        })
        .RequireAuthorization()
        .WithSummary("Create a CI API token for the current user (plaintext shown once)")
        .Produces<CreateTokenResponse>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized);

        auth.MapDelete("/tokens/{id:guid}", async (
            Guid id, ClaimsPrincipal principal, EaapDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
            if (token is null)
            {
                return Results.NotFound();
            }
            db.ApiTokens.Remove(token);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithSummary("Revoke one of the current user's API tokens")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);
    }

    internal static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out userId);
    }
}
