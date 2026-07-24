using System.Security.Claims;
using Eaap.Domain;
using Eaap.Domain.Entities;

namespace Eaap.Application;

/// <summary>
/// Password hashing, JWT issuance/validation and API-token generation (build spec phase 4 section 5).
/// </summary>
public interface IAuthTokenService
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string hash);

    /// <summary>Issues an HS256 JWT with the user's id, email and role claims.</summary>
    string CreateJwt(User user, IReadOnlyCollection<UserRoleType> roles);

    /// <summary>Validates signature and expiry; returns the principal, or null when invalid/expired.</summary>
    ClaimsPrincipal? ValidateJwt(string token);

    /// <summary>Creates a CI token: the plaintext (shown once, prefixed eaap_) and its SHA256 hash to store.</summary>
    (string Plaintext, string Hash) GenerateApiToken();

    string HashApiToken(string plaintext);

    /// <summary>True for a value that looks like an API token (eaap_ prefix) rather than a JWT.</summary>
    bool IsApiToken(string bearer);
}
