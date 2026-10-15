using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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

public record RefreshRequest([property: Required] string RefreshToken);

public record UserDto(Guid Id, string Email, string DisplayName);

/// <summary>
/// Sessão iniciada: o token de acesso (curto) e o refresh token, para pedir um par novo em <c>/auth/refresh</c>.
/// </summary>
public record AuthResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    UserDto User,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            HttpRequest http,
            KubicoDbContext db,
            IPasswordHasher<User> hasher,
            SessionService sessions,
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

            return Results.Ok(await sessions.StartAsync(user, Device(http), ct));
        });

        group.MapPost("/login", async (
            LoginRequest request,
            HttpRequest http,
            KubicoDbContext db,
            IPasswordHasher<User> hasher,
            SessionService sessions,
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

            return Results.Ok(await sessions.StartAsync(user, Device(http), ct));
        });

        // Troca o refresh token por um par novo (o antigo deixa de servir).
        group.MapPost("/refresh", async (RefreshRequest request, SessionService sessions, CancellationToken ct) =>
            await sessions.RefreshAsync(request.RefreshToken, ct) is { } response
                ? Results.Ok(response)
                : Results.Problem(
                    title: "A sessão terminou. Entra novamente.",
                    statusCode: StatusCodes.Status401Unauthorized));

        // Terminar sessão neste dispositivo: o refresh token identifica-o.
        group.MapPost("/logout", async (RefreshRequest request, SessionService sessions, CancellationToken ct) =>
        {
            await sessions.SignOutAsync(request.RefreshToken, ct);
            return Results.NoContent();
        });

        group.MapPost("/logout-all", async (ClaimsPrincipal principal, SessionService sessions, CancellationToken ct) =>
        {
            await sessions.SignOutEverywhereAsync(principal.GetUserId(), ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // Dispositivos com sessão iniciada; "este" é o do token de acesso usado no pedido.
        group.MapGet("/sessions", async (ClaimsPrincipal principal, SessionService sessions, CancellationToken ct) =>
        {
            var current = Guid.TryParse(principal.FindFirstValue(TokenService.SessionClaim), out var sid) ? sid : (Guid?)null;
            return Results.Ok(await sessions.ListAsync(principal.GetUserId(), current, ct));
        }).RequireAuthorization();

        group.MapDelete("/sessions/{sessionId:guid}", async (
            Guid sessionId, ClaimsPrincipal principal, SessionService sessions, CancellationToken ct) =>
            await sessions.EndAsync(principal.GetUserId(), sessionId, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization();

        return api;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Que dispositivo é: a app diz (cabeçalho X-Kubico-Device); senão, adivinha-se pelo User-Agent.</summary>
    private static string? Device(HttpRequest http)
    {
        var declared = http.Headers["X-Kubico-Device"].ToString();
        if (KnownDevices.Contains(declared))
        {
            return declared;
        }

        var agent = http.Headers.UserAgent.ToString();
        return agent switch
        {
            _ when agent.Contains("Android", StringComparison.OrdinalIgnoreCase) => "android",
            _ when agent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                || agent.Contains("iPad", StringComparison.OrdinalIgnoreCase) => "ios",
            _ when agent.Contains("Windows", StringComparison.OrdinalIgnoreCase) => "windows",
            _ when agent.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) => "macos",
            _ when agent.Contains("Linux", StringComparison.OrdinalIgnoreCase) => "linux",
            _ => null,
        };
    }

    private static readonly HashSet<string> KnownDevices = ["android", "ios", "windows", "macos", "linux", "web"];

    public static UserDto ToDto(this User user) => new(user.Id, user.Email, user.DisplayName);
}
