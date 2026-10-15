using System.ComponentModel.DataAnnotations;
using Kubico.Api.Common;
using Kubico.Domain.Users;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Auth;

public record RegisterRequest(
    [property: Required, EmailAddress, StringLength(256)] string Email,
    [property: Required, StringLength(128, MinimumLength = 8)] string Password,
    [property: Required, StringLength(100, MinimumLength = 1)] string DisplayName);

public record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password);

public record UserDto(Guid Id, string Email, string DisplayName);

public record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, UserDto User);

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            KubicoDbContext db,
            IPasswordHasher<User> hasher,
            TokenService tokens,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var email = NormalizeEmail(request.Email);
            if (await db.Users.AnyAsync(u => u.Email == email, ct))
            {
                return Results.Problem(
                    title: "Este email já está registado.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var user = new User
            {
                Email = email,
                DisplayName = request.DisplayName.Trim(),
                CreatedAt = clock.GetUtcNow(),
            };
            user.PasswordHash = hasher.HashPassword(user, request.Password);

            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            return Results.Ok(CreateResponse(user, tokens));
        });

        group.MapPost("/login", async (
            LoginRequest request,
            KubicoDbContext db,
            IPasswordHasher<User> hasher,
            TokenService tokens,
            CancellationToken ct) =>
        {
            var email = NormalizeEmail(request.Email);
            var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);

            if (user is null
                || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            {
                return Results.Problem(
                    title: "Email ou palavra-passe incorretos.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(CreateResponse(user, tokens));
        });

        return api;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static AuthResponse CreateResponse(User user, TokenService tokens)
    {
        var (token, expiresAt) = tokens.CreateAccessToken(user);
        return new AuthResponse(token, expiresAt, user.ToDto());
    }

    public static UserDto ToDto(this User user) => new(user.Id, user.Email, user.DisplayName);
}
