using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kubico.Domain.Calendar;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Calendar.Sync;

/// <summary>O que o Kubico guarda entre o pedido de autorização e o regresso da página do Google ou da Microsoft.</summary>
public sealed record OAuthState(
    Guid UserId, Guid HouseholdId, CalendarProvider Provider, string CodeVerifier, DateTimeOffset ExpiresAt);

/// <summary>
/// Uma ligação autorizada na página do Google ou da Microsoft, à espera de que a pessoa confirme (no navegador) que é
/// ela que quer ligar a conta a esta casa. Viaja cifrada no formulário da página de confirmação.
/// </summary>
public sealed record PendingConnection(
    Guid UserId,
    Guid HouseholdId,
    CalendarProvider Provider,
    string AccessToken,
    string RefreshToken,
    string Email,
    DateTimeOffset ExpiresAt);

/// <summary>Cifra os refresh tokens guardados e o estado OAuth que viaja no endereço (ASP.NET Data Protection).</summary>
public sealed class CalendarSecrets(IDataProtectionProvider protection)
{
    private readonly IDataProtector _tokens = protection.CreateProtector("Kubico.CalendarSync.RefreshToken.v1");
    private readonly IDataProtector _state = protection.CreateProtector("Kubico.CalendarSync.OAuthState.v1");
    private readonly IDataProtector _pending = protection.CreateProtector("Kubico.CalendarSync.PendingConnection.v1");

    public string ProtectPending(PendingConnection pending) => _pending.Protect(JsonSerializer.Serialize(pending));

    /// <summary>A ligação por confirmar, ou null se tiver sido alterada ou não tiver sido o Kubico a criá-la.</summary>
    public PendingConnection? UnprotectPending(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingConnection>(_pending.Unprotect(text));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    public string ProtectToken(string refreshToken) => _tokens.Protect(refreshToken);

    /// <summary>Falha com <see cref="CryptographicException"/> se as chaves se perderam.</summary>
    public string UnprotectToken(string protectedToken) => _tokens.Unprotect(protectedToken);

    public string ProtectState(OAuthState state) => _state.Protect(JsonSerializer.Serialize(state));

    /// <summary>O estado, ou null se tiver sido alterado ou não tiver sido o Kubico a criá-lo.</summary>
    public OAuthState? UnprotectState(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OAuthState>(_state.Unprotect(text));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>PKCE: um segredo aleatório e o seu SHA-256, que vai no pedido de autorização.</summary>
    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }
}

/// <summary>Uma sincronização de cada vez por ligação (o worker, o "sincronizar agora" e a ligação nova podem coincidir).</summary>
public sealed class CalendarSyncLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid connectionId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

/// <summary>Pede ao worker que sincronize já uma casa (quando um evento muda no Kubico).</summary>
public sealed class CalendarSyncTrigger
{
    private readonly Channel<Guid> _requests = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public ChannelReader<Guid> Requests => _requests.Reader;

    public void Request(Guid householdId) => _requests.Writer.TryWrite(householdId);
}

/// <summary>
/// Os tokens de acesso das ligações, só em memória e até perto de expirarem (P7): uma volta já não renova o token se o
/// anterior ainda serve (servem cerca de uma hora). Ficam presos ao refresh token com que foram pedidos (um hash), por
/// isso uma ligação refeita não usa o token antigo. Sem "expires_in" (ou com menos de 5 minutos), não se guardam.
/// </summary>
public sealed class CalendarAccessTokens(TimeProvider clock)
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, (string RefreshHash, string AccessToken, DateTimeOffset ExpiresAt)> _tokens = new();

    public string? Get(Guid connectionId, string refreshToken) =>
        _tokens.TryGetValue(connectionId, out var cached) && cached.RefreshHash == Hash(refreshToken)
            && clock.GetUtcNow() < cached.ExpiresAt - Margin
            ? cached.AccessToken
            : null;

    public void Set(Guid connectionId, string refreshToken, string accessToken, TimeSpan? lifetime)
    {
        if (lifetime is not { } value || value < TimeSpan.FromMinutes(5))
        {
            _tokens.TryRemove(connectionId, out _);
            return;
        }

        _tokens[connectionId] = (Hash(refreshToken), accessToken, clock.GetUtcNow() + value);
    }

    /// <summary>Esquece o token guardado; devolve true se havia um.</summary>
    public bool Forget(Guid connectionId) => _tokens.TryRemove(connectionId, out _);

    private static string Hash(string refreshToken) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(refreshToken)));
}

/// <summary>
/// Sincroniza em segundo plano, com dois ciclos independentes (P7): as casas onde algo mudou no Kubico, logo a seguir
/// (com uns segundos para juntar alterações seguidas), e, para apanhar o que muda nos calendários externos, todas as
/// ligações a cada <see cref="CalendarSyncOptions.IntervalMinutes"/> minutos, várias ao mesmo tempo
/// (<see cref="Parallelism"/>, cada uma com o seu DbContext). Uma alteração já não espera pelo fim da volta completa;
/// a mesma ligação nunca sincroniza duas vezes ao mesmo tempo (<see cref="CalendarSyncLocks"/>).
/// </summary>
public class CalendarSyncWorker(
    IServiceScopeFactory scopes,
    CalendarSyncTrigger trigger,
    IOptions<CalendarSyncOptions> options,
    TimeProvider clock,
    ILogger<CalendarSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    /// <summary>Ligações sincronizadas ao mesmo tempo na volta completa.</summary>
    public const int Parallelism = 8;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(FullSyncLoopAsync(stoppingToken), RequestedLoopAsync(stoppingToken));

    private async Task FullSyncLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes)), clock);
        do
        {
            try
            {
                List<Guid> ids;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    ids = await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().ActiveConnectionsAsync(ct);
                }

                await Parallel.ForEachAsync(
                    ids,
                    new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                    async (id, token) =>
                    {
                        try
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().SyncAsync(id, token);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            logger.LogError(e, "Falha a sincronizar a ligação {Connection}; volta a tentar mais tarde.", id);
                        }
                    });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Falha na sincronização dos calendários externos; volta a tentar mais tarde.");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task RequestedLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var households = new HashSet<Guid> { await trigger.Requests.ReadAsync(ct) };
            await Task.Delay(Debounce, clock, ct);
            while (trigger.Requests.TryRead(out var more))
            {
                households.Add(more);
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<CalendarSyncService>();
                foreach (var householdId in households)
                {
                    await sync.SyncHouseholdAsync(householdId, ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Falha na sincronização dos calendários externos; volta a tentar mais tarde.");
            }
        }
    }
}
