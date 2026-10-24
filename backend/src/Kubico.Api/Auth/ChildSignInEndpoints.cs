using System.ComponentModel.DataAnnotations;
using Kubico.Api.Common;
using Kubico.Domain.Users;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Auth;

public record FamilyCodeRequest([property: Required] string FamilyCode);

public record ChildLoginRequest([property: Required] string FamilyCode, Guid UserId, [property: Required] string Pin);

/// <summary>Um perfil de criança para escolher no ecrã de entrada.</summary>
public record ChildProfileOptionDto(Guid UserId, string DisplayName, string Color);

/// <summary>
/// Entrada das crianças sem email: o código da família diz que casa é, a criança escolhe o seu perfil e escreve o
/// PIN. Ao fim de 5 PINs errados seguidos, o perfil fica bloqueado 15 minutos, mesmo com PINs enviados em
/// simultâneo (ver <see cref="AttemptLimits"/>). Há também um limite de pedidos por IP.
/// </summary>
public static class ChildSignInEndpoints
{
    public static RouteGroupBuilder MapChildSignInEndpoints(this RouteGroupBuilder api)
    {
        var child = api.MapGroup("/auth/child").WithTags("Auth").AllowAnonymous();
        child.MapPost("/profiles", ListProfiles).RequireRateLimiting(RateLimits.ChildProfiles);
        child.MapPost("/login", Login).RequireRateLimiting(RateLimits.ChildLogin);
        return api;
    }

    private static async Task<IResult> ListProfiles(FamilyCodeRequest request, KubicoDbContext db, CancellationToken ct)
    {
        var code = Normalize(request.FamilyCode);
        var householdId = await db.Households
            .Where(h => h.ChildSignInCode == code)
            .Select(h => (Guid?)h.Id)
            .SingleOrDefaultAsync(ct);
        if (householdId is null)
        {
            return InvalidCode();
        }

        var profiles = await db.Memberships.AsNoTracking()
            .Where(m => m.HouseholdId == householdId && m.User.ManagedByHouseholdId == householdId)
            .OrderBy(m => m.User.DisplayName)
            .Select(m => new ChildProfileOptionDto(m.UserId, m.User.DisplayName, m.Color))
            .ToListAsync(ct);
        return Results.Ok(profiles);
    }

    private static async Task<IResult> Login(
        ChildLoginRequest request,
        HttpRequest http,
        KubicoDbContext db,
        IPasswordHasher<User> hasher,
        SessionService sessions,
        TimeProvider clock,
        CancellationToken ct)
    {
        var code = Normalize(request.FamilyCode);
        var householdId = await db.Households
            .Where(h => h.ChildSignInCode == code)
            .Select(h => (Guid?)h.Id)
            .SingleOrDefaultAsync(ct);
        var child = householdId is null
            ? null
            : await db.Users.SingleOrDefaultAsync(u => u.Id == request.UserId && u.ManagedByHouseholdId == householdId, ct);
        if (child is null)
        {
            return InvalidCode();
        }

        // A tentativa é reservada antes de verificar o PIN: com pedidos em simultâneo, no máximo 5 são verificados.
        var now = clock.GetUtcNow();
        if (!await AttemptLimits.ReservePinAsync(db, child.Id, now, ct))
        {
            await db.Entry(child).ReloadAsync(ct);
            return Locked(AttemptLimits.MinutesLeft(child.PinLockedUntil, now));
        }

        if (child.PinHash is null
            || hasher.VerifyHashedPassword(child, child.PinHash, request.Pin) == PasswordVerificationResult.Failed)
        {
            return await AttemptLimits.PinFailedAsync(db, child.Id, now, ct)
                ? Locked(AttemptLimits.MinutesLeft(now + User.PinLockout, now))
                : Results.Problem(title: "PIN errado.", statusCode: StatusCodes.Status401Unauthorized);
        }

        await AttemptLimits.PinSucceededAsync(db, child.Id, ct);
        return Results.Ok(await sessions.StartAsync(child, AuthEndpoints.Device(http), ct));
    }

    /// <summary>O código escreve-se como se quiser: com espaços, hífenes ou minúsculas.</summary>
    private static string Normalize(string code) =>
        new([.. code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);

    private static IResult InvalidCode() =>
        Results.Problem(title: "Código da família inválido. Pede-o a um adulto da casa.", statusCode: StatusCodes.Status404NotFound);

    private static IResult Locked(int minutes) => Results.Problem(
        title: $"Demasiados PINs errados. Tenta outra vez daqui a {minutes} minutos.",
        statusCode: StatusCodes.Status429TooManyRequests);
}
