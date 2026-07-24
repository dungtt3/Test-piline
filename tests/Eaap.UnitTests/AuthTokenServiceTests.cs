using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure;
using Eaap.Infrastructure.Auth;
using Microsoft.Extensions.Options;

namespace Eaap.UnitTests;

public class AuthTokenServiceTests
{
    private static AuthTokenService NewService(int ttlHours = 8) => new(Options.Create(new AuthOptions
    {
        JwtSecret = "eaap-unit-test-secret-key-0123456789abcdef",
        Issuer = "eaap",
        TokenTtlHours = ttlHours
    }));

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        Email = "dev@example.com",
        DisplayName = "Dev",
        PasswordHash = "x"
    };

    [Fact]
    public void Password_HashAndVerify_RoundTrips()
    {
        var svc = NewService();
        var hash = svc.HashPassword("s3cret-password");

        Assert.NotEqual("s3cret-password", hash);
        Assert.True(svc.VerifyPassword("s3cret-password", hash));
        Assert.False(svc.VerifyPassword("wrong", hash));
    }

    [Fact]
    public void VerifyPassword_MalformedHash_ReturnsFalse()
    {
        Assert.False(NewService().VerifyPassword("x", "not-a-bcrypt-hash"));
    }

    [Fact]
    public void Jwt_RoundTrips_WithRoleClaims()
    {
        var svc = NewService();
        var jwt = svc.CreateJwt(NewUser(), [UserRoleType.Maintainer, UserRoleType.Admin]);

        var principal = svc.ValidateJwt(jwt);

        Assert.NotNull(principal);
        Assert.True(principal.IsInRole("Maintainer"));
        Assert.True(principal.IsInRole("Admin"));
        Assert.False(principal.IsInRole("Viewer"));
    }

    [Fact]
    public void Jwt_Expired_FailsValidation()
    {
        // Issued with a negative TTL so it is already an hour past expiry, beyond the clock skew.
        var jwt = NewService(ttlHours: -1).CreateJwt(NewUser(), [UserRoleType.Viewer]);

        Assert.Null(NewService().ValidateJwt(jwt));
    }

    [Fact]
    public void Jwt_WrongSecret_FailsValidation()
    {
        var jwt = NewService().CreateJwt(NewUser(), [UserRoleType.Viewer]);
        var other = new AuthTokenService(Options.Create(new AuthOptions
        {
            JwtSecret = "a-totally-different-secret-key-0123456789xx",
            Issuer = "eaap"
        }));

        Assert.Null(other.ValidateJwt(jwt));
    }

    [Fact]
    public void ApiToken_HasPrefix_AndHashIsStableAndDeterministic()
    {
        var svc = NewService();
        var (plaintext, hash) = svc.GenerateApiToken();

        Assert.StartsWith("eaap_", plaintext);
        Assert.True(svc.IsApiToken(plaintext));
        Assert.False(svc.IsApiToken("some.jwt.value"));
        Assert.Equal(hash, svc.HashApiToken(plaintext));
        Assert.Equal(64, hash.Length); // SHA256 hex
    }

    [Fact]
    public void GenerateApiToken_ProducesDistinctTokens()
    {
        var svc = NewService();
        Assert.NotEqual(svc.GenerateApiToken().Plaintext, svc.GenerateApiToken().Plaintext);
    }
}
