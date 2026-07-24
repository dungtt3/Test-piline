using Eaap.Api.Auth;
using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Eaap.Api.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users").WithTags("Users").RequireAuthorization(Policies.Admin);

        users.MapGet("/", async (EaapDbContext db, CancellationToken ct) =>
        {
            var list = await db.Users.AsNoTracking().Include(u => u.Roles)
                .OrderBy(u => u.CreatedAt)
                .Select(u => new UserDto(
                    u.Id, u.Email, u.DisplayName,
                    u.Roles.Select(r => r.Role.ToString()).ToArray(), u.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(list);
        })
        .WithSummary("List users (Admin)");

        users.MapPost("/", async (CreateUserRequest request, EaapDbContext db, IAuthTokenService tokens, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || (request.Password?.Length ?? 0) < 8)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["password"] = ["password must be at least 8 characters and email is required."]
                });
            }
            if (!TryParseRoles(request.Roles, out var roles, out var badRole))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["roles"] = [$"unknown role '{badRole}'."]
                });
            }
            if (await db.Users.AnyAsync(u => u.Email == request.Email, ct))
            {
                return Results.Conflict(new { message = "A user with this email already exists." });
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = request.Email.Trim(),
                PasswordHash = tokens.HashPassword(request.Password!),
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Email.Trim() : request.DisplayName.Trim(),
                CreatedAt = DateTimeOffset.UtcNow,
                Roles = [.. roles.Select(r => new UserRole { Id = Guid.NewGuid(), Role = r })]
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/users/{user.Id}",
                new UserDto(user.Id, user.Email, user.DisplayName, roles.Select(r => r.ToString()).ToArray(), user.CreatedAt));
        })
        .WithSummary("Create a user (Admin)")
        .Produces<UserDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem();

        users.MapPut("/{id:guid}/role", async (Guid id, SetRoleRequest request, EaapDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
            {
                return Results.NotFound();
            }
            if (!TryParseRoles(request.Roles, out var roles, out var badRole))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["roles"] = [$"unknown role '{badRole}'."]
                });
            }

            user.Roles.Clear();
            foreach (var role in roles)
            {
                user.Roles.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, Role = role });
            }
            await db.SaveChangesAsync(ct);

            return Results.Ok(new UserDto(
                user.Id, user.Email, user.DisplayName, roles.Select(r => r.ToString()).ToArray(), user.CreatedAt));
        })
        .WithSummary("Replace a user's roles (Admin)")
        .Produces<UserDto>()
        .Produces(StatusCodes.Status404NotFound)
        .ProducesValidationProblem();

        return group;
    }

    private static bool TryParseRoles(string[]? raw, out List<UserRoleType> roles, out string? badRole)
    {
        roles = [];
        badRole = null;
        foreach (var name in raw ?? [])
        {
            if (!Enum.TryParse<UserRoleType>(name, ignoreCase: true, out var role))
            {
                badRole = name;
                return false;
            }
            if (!roles.Contains(role))
            {
                roles.Add(role);
            }
        }
        if (roles.Count == 0)
        {
            roles.Add(UserRoleType.Viewer);
        }
        return true;
    }
}
