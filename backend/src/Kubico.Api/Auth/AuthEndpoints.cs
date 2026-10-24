using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Calendar;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
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
/// Terminar sessão em todos os dispositivos. Com <c>ResetCalendarFeeds</c>, os endereços de calendário subscritos
/// (feed ICS) desta pessoa também deixam de servir (por exemplo, num telemóvel perdido).
/// </summary>
public record LogoutAllRequest(bool ResetCalendarFeeds = false);

/// <summary>
/// A palavra-passe está certa mas a conta tem verificação em dois passos: falta o código, que se envia com o
/// <c>ChallengeToken</c> para <c>/auth/login/2fa</c> (vale 5 minutos).
/// </summary>
public record TwoFactorChallengeDto(bool TwoFactorRequired, string ChallengeToken);

public record TwoFactorLoginRequest([property: Required] string ChallengeToken, [property: Required] string Code);

/// <summary>Quem tem a sessão. Os perfis de criança não têm email. <c>Language</c> é a língua em que recebe os avisos ("pt" ou "en").</summary>
public record UserDto(Guid Id, string? Email, string DisplayName, bool IsChildProfile, string Language);

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
    /// <summary>
    /// Quando o email não tem conta, verifica-se a palavra-passe contra este hash: a resposta demora o mesmo que com
    /// uma conta a sério (não se fica a saber quem usa o Kubico pelo tempo de resposta).
    /// </summary>
    private static readonly Lazy<(User User, string Hash)> Dummy = new(() =>
    {
        var user = new User { DisplayName = "-" };
        return (user, new PasswordHasher<User>().HashPassword(user, Guid.NewGuid().ToString()));
    });

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
                Language = Texts.LanguageOf(http.HttpContext),
                CreatedAt = clock.GetUtcNow(),
            };
            user.PasswordHash = hasher.HashPassword(user, request.Password);

            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            return Results.Ok(await sessions.StartAsync(user, Device(http), ct));
        }).RequireRateLimiting(RateLimits.Register);

        group.MapPost("/login", async (
            LoginRequest request,
            HttpRequest http,
            KubicoDbContext db,
            IPasswordHasher<User> hasher,
            SessionService sessions,
            TwoFactorService twoFactor,
            LoginThrottle throttle,
            CancellationToken ct) =>
        {
            var email = NormalizeEmail(request.Email);
            if (!throttle.TryBegin(email, out var wait))
            {
                return TooManyPasswords(http.HttpContext, wait);
            }

            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, ct);
            var (hashUser, hash) = user is null ? Dummy.Value : (user, user.PasswordHash);
            if (hasher.VerifyHashedPassword(hashUser, hash, request.Password) == PasswordVerificationResult.Failed || user is null)
            {
                return throttle.Failed(email) is { } locked
                    ? TooManyPasswords(http.HttpContext, locked)
                    : Results.Problem(title: "Email ou palavra-passe incorretos.", statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.Succeeded(email);
            if (user.TwoFactorEnabled)
            {
                return Results.Ok(new TwoFactorChallengeDto(true, twoFactor.IssueChallenge(user.Id, Device(http))));
            }

            return Results.Ok(await sessions.StartAsync(user, Device(http), ct));
        }).RequireRateLimiting(RateLimits.Login);

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
            if (await CheckTwoFactorAsync(db, twoFactor, user, request.Code, now, ct) is { } refused)
            {
                return refused;
            }

            return Results.Ok(await sessions.StartAsync(user, device, ct));
        }).RequireRateLimiting(RateLimits.Login);

        // Troca o refresh token por um par novo (o antigo deixa de servir).
        group.MapPost("/refresh", async (RefreshRequest request, SessionService sessions, TimeProvider clock, CancellationToken ct) =>
        {
            var startedAt = clock.GetUtcNow();
            return await sessions.RefreshAsync(request.RefreshToken, startedAt, ct) is { } response
                ? Results.Ok(response)
                : Results.Problem(title: "A sessão terminou. Entra novamente.", statusCode: StatusCodes.Status401Unauthorized);
        }).RequireRateLimiting(RateLimits.Refresh);

        // Terminar sessão neste dispositivo: o refresh token identifica-o.
        group.MapPost("/logout", async (RefreshRequest request, SessionService sessions, CancellationToken ct) =>
        {
            await sessions.SignOutAsync(request.RefreshToken, ct);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimits.Refresh);

        group.MapPost("/logout-all", async (
            LogoutAllRequest? request, ClaimsPrincipal principal, SessionService sessions, KubicoDbContext db, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            await sessions.SignOutEverywhereAsync(userId, ct);
            if (request?.ResetCalendarFeeds == true)
            {
                await CalendarFeedEndpoints.RevokeFeedsAsync(db, m => m.UserId == userId, ct);
            }

            return Results.NoContent();
        }).RequireAuthorization();

        // Dispositivos com sessão iniciada; "este" é o do token de acesso usado no pedido.
        group.MapGet("/sessions", async (ClaimsPrincipal principal, SessionService sessions, CancellationToken ct) =>
            Results.Ok(await sessions.ListAsync(principal.GetUserId(), HouseholdHub.SessionOf(principal), ct)))
            .RequireAuthorization();

        group.MapDelete("/sessions/{sessionId:guid}", async (
            Guid sessionId, ClaimsPrincipal principal, SessionService sessions, CancellationToken ct) =>
            await sessions.EndAsync(principal.GetUserId(), sessionId, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization()
            .RequireActiveSession();

        return api;
    }

    /// <summary>
    /// Confere um código da 2FA (da app ou de recuperação), com o bloqueio à prova de pedidos em simultâneo: a
    /// tentativa é reservada antes de se verificar, e o mesmo código não serve em dois pedidos ao mesmo tempo (a
    /// versão do utilizador muda ao gravar). Null se o código serviu (e ficou gravado como usado).
    /// </summary>
    internal static async Task<IResult?> CheckTwoFactorAsync(
        KubicoDbContext db, TwoFactorService twoFactor, User user, string? code, DateTimeOffset now, CancellationToken ct,
        int wrongStatus = StatusCodes.Status401Unauthorized)
    {
        if (!await AttemptLimits.ReserveTwoFactorAsync(db, user.Id, now, ct))
        {
            await db.Entry(user).ReloadAsync(ct);
            return TooManyCodes(AttemptLimits.MinutesLeft(user.TwoFactorLockedUntil, now));
        }

        await db.Entry(user).ReloadAsync(ct);
        if (!twoFactor.Verify(user, code, now))
        {
            // Ao 5.º errado bloqueia; este ainda diz só que o código não está certo.
            await AttemptLimits.TwoFactorFailedAsync(db, user.Id, now, ct);
            return Results.Problem(title: "O código não está certo.", statusCode: wrongStatus);
        }

        user.RegisterRightTwoFactor();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // O mesmo código (ou outro) foi usado ao mesmo tempo noutro pedido: este não conta.
            return Results.Problem(title: "O código não está certo.", statusCode: wrongStatus);
        }

        return null;
    }

    /// <summary>
    /// Confere a palavra-passe numa ação sensível (com o mesmo limite de tentativas da entrada). Null se estiver certa.
    /// </summary>
    internal static IResult? CheckPassword(
        LoginThrottle throttle, IPasswordHasher<User> hasher, HttpContext http, User user, string? password)
    {
        var key = user.Email ?? user.Id.ToString();
        if (!throttle.TryBegin(key, out var wait))
        {
            return TooManyPasswords(http, wait);
        }

        if (password is null || hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            return throttle.Failed(key) is { } locked
                ? TooManyPasswords(http, locked)
                : Results.Problem(title: "A palavra-passe não está certa.", statusCode: StatusCodes.Status400BadRequest);
        }

        throttle.Succeeded(key);
        return null;
    }

    private static IResult TooManyCodes(int minutes) => Results.Problem(
        title: $"Demasiados códigos errados. Tenta outra vez daqui a {minutes} minutos.",
        statusCode: StatusCodes.Status429TooManyRequests);

    private static IResult TooManyPasswords(HttpContext http, TimeSpan wait)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(
            title: minutes == 1
                ? "Demasiadas tentativas erradas. Tenta outra vez daqui a um minuto."
                : $"Demasiadas tentativas erradas. Tenta outra vez daqui a {minutes} minutos.",
            statusCode: StatusCodes.Status429TooManyRequests);
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

    public static UserDto ToDto(this User user) =>
        new(user.Id, user.Email, user.DisplayName, user.IsChildProfile, user.Language);
}
