using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Eaap.Infrastructure.Auth;

public class AuthTokenService(IOptions<AuthOptions> options) : IAuthTokenService
{
    public const string TokenPrefix = "eaap_";
    private readonly AuthOptions _options = options.Value;

    private SymmetricSecurityKey SigningKey =>
        new(Encoding.UTF8.GetBytes(_options.JwtSecret));

    public string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public bool VerifyPassword(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch
        {
            return false;
        }
    }

    public string CreateJwt(User user, IReadOnlyCollection<UserRoleType> roles)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.DisplayName)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r.ToString())));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Issuer,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_options.TokenTtlHours),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public ClaimsPrincipal? ValidateJwt(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Issuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = SigningKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(5)
        };

        try
        {
            return new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
        }
        catch
        {
            return null;
        }
    }

    public (string Plaintext, string Hash) GenerateApiToken()
    {
        var random = RandomNumberGenerator.GetBytes(24);
        var body = Convert.ToBase64String(random)
            .Replace('+', 'A').Replace('/', 'B').Replace("=", "")[..32];
        var plaintext = TokenPrefix + body;
        return (plaintext, HashApiToken(plaintext));
    }

    public string HashApiToken(string plaintext)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool IsApiToken(string bearer) =>
        bearer.StartsWith(TokenPrefix, StringComparison.Ordinal);
}
