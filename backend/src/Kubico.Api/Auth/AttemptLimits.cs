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

    /// <summary>Um PIN errado. Se chegou ao limite, bloqueia o perfil 15 minutos; devolve true nesse caso.</summary>
    public static async Task<bool> PinFailedAsync(KubicoDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.Users
            .Where(u => u.Id == userId && u.FailedPinAttempts >= User.MaxPinAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.PinLockedUntil, now + User.PinLockout)
                .SetProperty(u => u.FailedPinAttempts, 0), ct) == 1;

    /// <summary>O PIN estava certo (ou foi mudado por um adulto): o contador volta a zero e o perfil desbloqueia.</summary>
    public static Task PinSucceededAsync(KubicoDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FailedPinAttempts, 0)
            .SetProperty(u => u.PinLockedUntil, (DateTimeOffset?)null), ct);

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
