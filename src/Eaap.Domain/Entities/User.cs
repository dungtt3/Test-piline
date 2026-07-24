namespace Eaap.Domain.Entities;

/// <summary>A platform user with a BCrypt password hash (phase 4 minimal auth).</summary>
public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public List<UserRole> Roles { get; set; } = [];
}

/// <summary>A global role grant (no per-repository scoping in phase 4).</summary>
public class UserRole
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public UserRoleType Role { get; set; }
}

/// <summary>A long-lived API token (eaap_...) for CI, stored only as a SHA256 hash.</summary>
public class ApiToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public bool IsInEffect(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}
