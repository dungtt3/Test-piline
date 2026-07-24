using System.Security.Claims;
using System.Text.Encodings.Web;
using Eaap.Application;
using Eaap.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Eaap.Api.Auth;

/// <summary>
/// Bearer authentication that accepts both JWTs and CI API tokens (build spec phase 4 section 5).
/// A value prefixed eaap_ is looked up as an API token; anything else is validated as a JWT.
/// </summary>
public class EaapAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "EaapBearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var bearer = header["Bearer ".Length..].Trim();
        var tokens = Context.RequestServices.GetRequiredService<IAuthTokenService>();

        return tokens.IsApiToken(bearer)
            ? await AuthenticateApiTokenAsync(bearer, tokens)
            : AuthenticateJwt(bearer, tokens);
    }

    private async Task<AuthenticateResult> AuthenticateApiTokenAsync(string bearer, IAuthTokenService tokens)
    {
        var db = Context.RequestServices.GetRequiredService<EaapDbContext>();
        var hash = tokens.HashApiToken(bearer);

        var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token is null || !token.IsInEffect(DateTimeOffset.UtcNow))
        {
            return AuthenticateResult.Fail("Invalid or expired API token.");
        }

        var roles = await db.UserRoles.Where(r => r.UserId == token.UserId).Select(r => r.Role).ToListAsync();
        token.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, token.UserId.ToString()) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r.ToString())));
        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.NameIdentifier, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private static AuthenticateResult AuthenticateJwt(string bearer, IAuthTokenService tokens)
    {
        var principal = tokens.ValidateJwt(bearer);
        return principal is null
            ? AuthenticateResult.Fail("Invalid or expired token.")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}

/// <summary>Authorization policy names for the RBAC matrix.</summary>
public static class Policies
{
    /// <summary>Maintainer or Admin: write operations (scans, gate, suppressions, repositories).</summary>
    public const string Maintainer = "RequireMaintainer";

    /// <summary>Admin only: user and token management.</summary>
    public const string Admin = "RequireAdmin";
}
