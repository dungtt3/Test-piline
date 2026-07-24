using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Eaap.IntegrationTests;

/// <summary>
/// Test authentication that stands in for the real bearer handler so the existing behavioural
/// tests need no credentials (they authenticate as Admin by default), while the RBAC matrix tests
/// pick a role with the X-Test-Role header. X-Test-Anonymous forces an unauthenticated request.
/// The real token mechanism (JWT/API token/expiry) is covered by AuthTokenService unit tests.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey("X-Test-Anonymous"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var roleHeader = Request.Headers["X-Test-Role"].ToString();
        var roles = string.IsNullOrWhiteSpace(roleHeader)
            ? ["Admin"]
            : roleHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var userId = Request.Headers["X-Test-UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = Guid.Empty.ToString();
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.NameIdentifier, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
