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

/// <summary>Cifra os refresh tokens guardados e o estado OAuth que viaja no endereço (ASP.NET Data Protection).</summary>
public sealed class CalendarSecrets(IDataProtectionProvider protection)
{
    private readonly IDataProtector _tokens = protection.CreateProtector("Kubico.CalendarSync.RefreshToken.v1");
    private readonly IDataProtector _state = protection.CreateProtector("Kubico.CalendarSync.OAuthState.v1");

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
/// Sincroniza em segundo plano: logo a seguir a uma alteração no Kubico (com uns segundos para juntar alterações
/// seguidas) e, para apanhar o que muda nos calendários externos, todas as ligações a cada
/// <see cref="CalendarSyncOptions.IntervalMinutes"/> minutos.
/// </summary>
public class CalendarSyncWorker(
    IServiceScopeFactory scopes,
    CalendarSyncTrigger trigger,
    IOptions<CalendarSyncOptions> options,
    TimeProvider clock,
    ILogger<CalendarSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes));
        var nextFullSync = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var households = new HashSet<Guid>();
            var wait = nextFullSync - clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(wait);
                try
                {
                    households.Add(await trigger.Requests.ReadAsync(timeout.Token));
                    await Task.Delay(Debounce, stoppingToken);
                    while (trigger.Requests.TryRead(out var more))
                    {
                        households.Add(more);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Chegou a hora da volta completa.
                }
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<CalendarSyncService>();
                if (households.Count == 0)
                {
                    nextFullSync = clock.GetUtcNow() + interval;
                    await sync.SyncAllAsync(stoppingToken);
                }
                else
                {
                    foreach (var householdId in households)
                    {
                        await sync.SyncHouseholdAsync(householdId, stoppingToken);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Falha na sincronização dos calendários externos; volta a tentar mais tarde.");
            }
        }
    }
}
