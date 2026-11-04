using Kubico.Domain.Users;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Auth;

/// <summary>
/// Os bloqueios do PIN das crianças e dos códigos da verificação em dois passos (5 errados seguidos → 15 minutos),
/// à prova de pedidos em simultâneo: cada tentativa é reservada com um UPDATE atómico na base de dados antes de se
/// verificar o PIN ou o código, e só passa enquanto houver tentativas. Assim, 20 PINs enviados ao mesmo tempo não
/// são verificados os 20: no máximo 5.
/// </summary>
public static class AttemptLimits
{
    /// <summary>Reserva uma tentativa de PIN. False se o perfil está bloqueado (ou já há 5 a ser verificadas).</summary>
    public static async Task<bool> ReservePinAsync(KubicoDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.Users
            .Where(u => u.Id == userId && (u.PinLockedUntil == null || u.PinLockedUntil <= now)
                && u.FailedPinAttempts < User.MaxPinAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedPinAttempts, u => u.FailedPinAttempts + 1), ct) == 1;

    /// <summary>
    /// Um PIN errado. Se chegou ao limite, bloqueia o perfil (cada bloqueio seguido mais longo,
    /// <see cref="User.PinLockoutFor"/>) e devolve até quando e que bloqueio é; senão, null.
    /// </summary>
    public static async Task<(DateTimeOffset Until, int Lockouts)?> PinFailedAsync(
        KubicoDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        // O primeiro UPDATE decide (atómico): só um dos pedidos em simultâneo bloqueia. Depois acerta-se a duração.
        var locked = await db.Users
            .Where(u => u.Id == userId && u.FailedPinAttempts >= User.MaxPinAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.PinLockedUntil, now + User.PinLockout)
                .SetProperty(u => u.FailedPinAttempts, 0)
                .SetProperty(u => u.PinLockouts, u => u.PinLockouts + 1), ct) == 1;
        if (!locked)
        {
            return null;
        }

        var lockouts = await db.Users.Where(u => u.Id == userId).Select(u => u.PinLockouts).SingleAsync(ct);
        var until = now + User.PinLockoutFor(lockouts);
        if (lockouts > 1)
        {
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.PinLockedUntil, until), ct);
        }

        return (until, lockouts);
    }

    /// <summary>O PIN estava certo (ou foi mudado por um adulto): os contadores voltam a zero e o perfil desbloqueia.</summary>
    public static Task PinSucceededAsync(KubicoDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FailedPinAttempts, 0)
            .SetProperty(u => u.PinLockedUntil, (DateTimeOffset?)null)
            .SetProperty(u => u.PinLockouts, 0), ct);

    /// <summary>Reserva uma tentativa de código da 2FA. False se a conta está bloqueada (ou já há 5 a ser verificadas).</summary>
    public static async Task<bool> ReserveTwoFactorAsync(KubicoDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.Users
            .Where(u => u.Id == userId && (u.TwoFactorLockedUntil == null || u.TwoFactorLockedUntil <= now)
                && u.FailedTwoFactorAttempts < User.MaxPinAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedTwoFactorAttempts, u => u.FailedTwoFactorAttempts + 1), ct) == 1;

    /// <summary>Um código errado. Se chegou ao limite, bloqueia 15 minutos; devolve true nesse caso.</summary>
    public static async Task<bool> TwoFactorFailedAsync(KubicoDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.Users
            .Where(u => u.Id == userId && u.FailedTwoFactorAttempts >= User.MaxPinAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.TwoFactorLockedUntil, now + User.PinLockout)
                .SetProperty(u => u.FailedTwoFactorAttempts, 0), ct) == 1;

    /// <summary>Quantos minutos faltam para acabar um bloqueio (arredondado para cima; pelo menos 1).</summary>
    public static int MinutesLeft(DateTimeOffset? until, DateTimeOffset now) =>
        until is { } end && end > now ? Math.Max(1, (int)Math.Ceiling((end - now).TotalMinutes)) : (int)User.PinLockout.TotalMinutes;
}
