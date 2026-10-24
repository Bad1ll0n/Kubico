using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Kubico.Api.Realtime;
using Kubico.Domain.Users;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Auth;

/// <summary>Uma sessão iniciada num dispositivo (uma família de refresh tokens).</summary>
public record DeviceSessionDto(Guid Id, string? Device, DateTimeOffset StartedAt, DateTimeOffset LastActiveAt, bool IsCurrent);

/// <summary>
/// Sessões com refresh tokens: o token de acesso dura pouco e a app troca o refresh token por um par novo
/// sempre que precisa. Cada troca revoga o anterior; reutilizar um token já trocado revoga a sessão desse dispositivo
/// (a não ser que o pedido já viesse a caminho antes da troca: duas abas a renovar ao mesmo tempo).
/// Terminar uma sessão também fecha as ligações de tempo real dela e deixa de enviar avisos para os telemóveis dela.
/// </summary>
public class SessionService(
    KubicoDbContext db, TokenService tokens, IOptions<JwtOptions> options, HubConnections connections, TimeProvider clock)
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

    /// <summary>
    /// Troca um refresh token válido por um par novo; null se não servir (a app tem de pedir a palavra-passe).
    /// <paramref name="requestStartedAt"/> é quando o pedido chegou: se o token foi trocado depois disso por outro
    /// pedido igual (duas abas ao mesmo tempo), este também recebe um par novo, em vez de terminar a sessão.
    /// </summary>
    public async Task<AuthResponse?> RefreshAsync(string refreshToken, DateTimeOffset requestStartedAt, CancellationToken ct)
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
            if (token.ReplacedAt is { } replaced && requestStartedAt < replaced
                && await db.RefreshTokens.AnyAsync(t => t.FamilyId == token.FamilyId && t.RevokedAt == null && t.ExpiresAt > now, ct))
            {
                // Renovação em simultâneo com outra (a mesma sessão): mais um par, na mesma família.
                var again = Issue(token.User, token.FamilyId, token.Device, now);
                await db.SaveChangesAsync(ct);
                return again;
            }

            // Um token já trocado voltou a aparecer: alguém o copiou. Termina a sessão desse dispositivo.
            await EndFamilyAsync(token.UserId, token.FamilyId, now, ct);
            return null;
        }

        token.RevokedAt = now;
        token.ReplacedAt = now;
        var response = Issue(token.User, token.FamilyId, token.Device, now);
        await db.SaveChangesAsync(ct);
        return response;
    }

    /// <summary>Terminar sessão neste dispositivo.</summary>
    public async Task SignOutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.AsNoTracking().Where(t => t.TokenHash == hash)
            .Select(t => new { t.UserId, t.FamilyId })
            .SingleOrDefaultAsync(ct);
        if (token is not null)
        {
            await EndFamilyAsync(token.UserId, token.FamilyId, clock.GetUtcNow(), ct);
        }
    }

    /// <summary>
    /// As sessões ativas do utilizador, da usada mais recentemente para a mais antiga. Cada renovação cria um token
    /// novo na família, por isso o primeiro dá o início e o último o último uso.
    /// </summary>
    public async Task<List<DeviceSessionDto>> ListAsync(Guid userId, Guid? currentSessionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var tokens = await db.RefreshTokens.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(ct);
        return
        [
            .. tokens
                .GroupBy(t => t.FamilyId)
                .Where(family => family.Any(t => t.IsActive(now)))
                .Select(family => new DeviceSessionDto(
                    family.Key,
                    family.MaxBy(t => t.CreatedAt)!.Device,
                    family.Min(t => t.CreatedAt),
                    family.Max(t => t.CreatedAt),
                    family.Key == currentSessionId))
                .OrderByDescending(s => s.LastActiveAt),
        ];
    }

    /// <summary>Termina uma sessão do utilizador; false se não existir (ou não for dele).</summary>
    public async Task<bool> EndAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!await IsActiveAsync(userId, sessionId, ct))
        {
            return false;
        }

        await EndFamilyAsync(userId, sessionId, now, ct);
        return true;
    }

    /// <summary>
    /// Terminar sessão em todos os dispositivos (menos, se indicada, a sessão <paramref name="keep"/>: por exemplo,
    /// a de quem acabou de ligar a verificação em dois passos).
    /// </summary>
    public async Task SignOutEverywhereAsync(Guid userId, CancellationToken ct, Guid? keep = null)
    {
        var now = clock.GetUtcNow();
        await RevokeAsync(t => t.UserId == userId && (keep == null || t.FamilyId != keep), now, ct);
        await db.PushDevices.Where(d => d.UserId == userId && (keep == null || d.SessionId == null || d.SessionId != keep))
            .ExecuteDeleteAsync(ct);
        connections.Abort(c => c.UserId == userId && (keep == null || c.SessionId != keep));
    }

    /// <summary>Se a sessão (família) do token de acesso ainda está ativa: terminar a sessão corta-a já nas ações sensíveis.</summary>
    public async Task<bool> IsActiveAsync(Guid userId, Guid? sessionId, CancellationToken ct)
    {
        if (sessionId is not { } family)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        return await db.RefreshTokens.AnyAsync(
            t => t.UserId == userId && t.FamilyId == family && t.RevokedAt == null && t.ExpiresAt > now, ct);
    }

    private async Task EndFamilyAsync(Guid userId, Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        await RevokeAsync(t => t.FamilyId == familyId, now, ct);
        await db.PushDevices.Where(d => d.UserId == userId && d.SessionId == familyId).ExecuteDeleteAsync(ct);
        connections.Abort(c => c.SessionId == familyId);
    }

    private AuthResponse Issue(User user, Guid familyId, string? device, DateTimeOffset now)
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        // Na web o refresh token fica no armazenamento do browser: dura menos.
        var days = device == "web"
            ? Math.Min(options.Value.RefreshTokenLifetimeDays, options.Value.WebRefreshTokenLifetimeDays)
            : options.Value.RefreshTokenLifetimeDays;
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(raw),
            FamilyId = familyId,
            Device = device is { Length: > 200 } ? device[..200] : device,
            CreatedAt = now,
            ExpiresAt = now.AddDays(days),
        };
        db.RefreshTokens.Add(refresh);

        var (accessToken, expiresAt) = tokens.CreateAccessToken(user, familyId);
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
