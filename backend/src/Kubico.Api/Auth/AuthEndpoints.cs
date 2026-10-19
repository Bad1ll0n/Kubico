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

/// <summary>
/// A palavra-passe está certa mas a conta tem verificação em dois passos: falta o código, que se envia com o
/// <c>ChallengeToken</c> para <c>/auth/login/2fa</c> (vale 5 minutos).
/// </summary>
public record TwoFactorChallengeDto(bool TwoFactorRequired, string ChallengeToken);

public record TwoFactorLoginRequest([property: Required] string ChallengeToken, [property: Required] string Code);

/// <summary>Quem está a usar a app. Os perfis de criança não têm email.</summary>
public record UserDto(Guid Id, string? Email, string DisplayName, bool IsChildProfile);

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
            TwoFactorService twoFactor,
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

            if (user.TwoFactorEnabled)
            {
                return Results.Ok(new TwoFactorChallengeDto(true, twoFactor.IssueChallenge(user.Id, Device(http))));
            }

            return Results.Ok(await sessions.StartAsync(user, Device(http), ct));
        });

        // O segundo passo: o código da app de autenticação (ou um de recuperação).
        group.MapPost("/login/2fa", async (
            TwoFactorLoginRequest request,
            KubicoDbContext db,
            SessionService sessions,
            TwoFactorService twoFactor,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var expired = Results.Problem(
                title: "Passou demasiado tempo. Entra outra vez.", statusCode: StatusCodes.Status401Unauthorized);
            if (twoFactor.ReadChallenge(request.ChallengeToken) is not var (userId, device))
            {
                return expired;
            }

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
            if (user is not { TwoFactorEnabled: true })
            {
                return expired;
            }

            var now = clock.GetUtcNow();
            if (user.IsTwoFactorLocked(now))
            {
                return Results.Problem(
                    title: "Demasiados códigos errados. Tenta outra vez daqui a 15 minutos.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (!twoFactor.Verify(user, request.Code, now))
            {
                user.RegisterWrongTwoFactor(now);
                await db.SaveChangesAsync(ct);
                return Results.Problem(title: "O código não está certo.", statusCode: StatusCodes.Status401Unauthorized);
            }

            user.RegisterRightTwoFactor();
            await db.SaveChangesAsync(ct);
            return Results.Ok(await sessions.StartAsync(user, device, ct));
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
    internal static string? Device(HttpRequest http)
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

    public static UserDto ToDto(this User user) => new(user.Id, user.Email, user.DisplayName, user.IsChildProfile);
}
