using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Kubico.Domain.Users;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Auth;

/// <summary>
/// Sessões com refresh tokens: o token de acesso dura pouco e a app troca o refresh token por um par novo
/// sempre que precisa. Cada troca revoga o anterior; reutilizar um token já trocado revoga a sessão desse dispositivo.
/// </summary>
public class SessionService(KubicoDbContext db, TokenService tokens, IOptions<JwtOptions> options, TimeProvider clock)
{
    /// <summary>Início de sessão (registo ou entrada): uma família de refresh tokens nova.</summary>
    public async Task<AuthResponse> StartAsync(User user, string? device, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // Arruma as sessões que já expiraram.
        db.RefreshTokens.RemoveRange(
            await db.RefreshTokens.Where(t => t.UserId == user.Id && t.ExpiresAt < now).ToListAsync(ct));

        var response = Issue(user, Guid.NewGuid(), device, now);
        await db.SaveChangesAsync(ct);
        return response;
    }

    /// <summary>Troca um refresh token válido por um par novo; null se não servir (a app tem de pedir a palavra-passe).</summary>
    public async Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.Include(t => t.User).SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        if (token is null || now >= token.ExpiresAt)
        {
            return null;
        }

        if (token.RevokedAt is not null)
        {
            // Um token já trocado voltou a aparecer: alguém o copiou. Termina a sessão desse dispositivo.
            await RevokeAsync(t => t.FamilyId == token.FamilyId, now, ct);
            return null;
        }

        token.RevokedAt = now;
        var response = Issue(token.User, token.FamilyId, token.Device, now);
        await db.SaveChangesAsync(ct);
        return response;
    }

    /// <summary>Terminar sessão neste dispositivo.</summary>
    public async Task SignOutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var familyId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId)
            .SingleOrDefaultAsync(ct);
        if (familyId is { } family)
        {
            await RevokeAsync(t => t.FamilyId == family, clock.GetUtcNow(), ct);
        }
    }

    /// <summary>Terminar sessão em todos os dispositivos.</summary>
    public Task SignOutEverywhereAsync(Guid userId, CancellationToken ct) =>
        RevokeAsync(t => t.UserId == userId, clock.GetUtcNow(), ct);

    private AuthResponse Issue(User user, Guid familyId, string? device, DateTimeOffset now)
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(raw),
            FamilyId = familyId,
            Device = device is { Length: > 200 } ? device[..200] : device,
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.RefreshTokenLifetimeDays),
        };
        db.RefreshTokens.Add(refresh);

        var (accessToken, expiresAt) = tokens.CreateAccessToken(user);
        return new AuthResponse(accessToken, expiresAt, user.ToDto(), raw, refresh.ExpiresAt);
    }

    private async Task RevokeAsync(
        System.Linq.Expressions.Expression<Func<RefreshToken, bool>> which, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(which).Where(t => t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in active)
        {
            token.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
