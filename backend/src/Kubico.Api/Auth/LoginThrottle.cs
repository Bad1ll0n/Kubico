using System.Collections.Concurrent;

namespace Kubico.Api.Auth;

/// <summary>
/// Limite de palavras-passe erradas por email: ao fim de 5 seguidas, a entrada fica bloqueada 1 minuto; se
/// continuarem a falhar, 5 minutos e depois 15. Cada tentativa é reservada antes de se verificar a palavra-passe, por
/// isso pedidos em simultâneo não passam do limite. Conta também os emails sem conta (a resposta é igual nos dois
/// casos: não se fica a saber quem tem conta). Fica em memória: com várias instâncias da API, cada uma conta as suas
/// (limitação conhecida, ver CLAUDE.md).
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int MaxFailures = 5;

    private static readonly TimeSpan[] Waits = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    /// <summary>Ao fim deste tempo sem falhas, as esperas voltam a começar no primeiro degrau.</summary>
    private static readonly TimeSpan Forget = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    private sealed class State
    {
        public int Failures;
        public int Pending;
        public int Level;
        public DateTimeOffset? LockedUntil;
        public DateTimeOffset LastFailure;
        public DateTimeOffset LastBegin;
    }

    /// <summary>Reserva uma tentativa. False (com o tempo que falta) se a entrada está bloqueada.</summary>
    public bool TryBegin(string email, out TimeSpan retryAfter)
    {
        var now = clock.GetUtcNow();
        Prune(now);
        var state = _states.GetOrAdd(email, _ => new State());
        lock (state)
        {
            if (state.LockedUntil is { } until && now < until)
            {
                retryAfter = until - now;
                return false;
            }

            if (now - state.LastFailure > Forget)
            {
                state.Level = 0;
            }

            // Uma tentativa que nunca acabou (o pedido falhou a meio) não fica a contar para sempre.
            if (now - state.LastBegin > TimeSpan.FromMinutes(1))
            {
                state.Pending = 0;
            }

            if (state.Failures + state.Pending >= MaxFailures)
            {
                // Já há tentativas suficientes a ser verificadas para chegar ao limite.
                retryAfter = Waits[Math.Min(state.Level, Waits.Length - 1)];
                return false;
            }

            state.Pending++;
            state.LastBegin = now;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>A palavra-passe estava certa: o contador volta a zero.</summary>
    public void Succeeded(string email)
    {
        if (_states.TryRemove(email, out var state))
        {
            lock (state)
            {
                state.Pending = 0;
            }
        }
    }

    /// <summary>A palavra-passe estava errada. Devolve o tempo de bloqueio se esta falha o ativou.</summary>
    public TimeSpan? Failed(string email)
    {
        var now = clock.GetUtcNow();
        var state = _states.GetOrAdd(email, _ => new State());
        lock (state)
        {
            state.Pending = Math.Max(0, state.Pending - 1);
            state.Failures++;
            state.LastFailure = now;
            if (state.Failures < MaxFailures)
            {
                return null;
            }

            var wait = Waits[Math.Min(state.Level, Waits.Length - 1)];
            state.LockedUntil = now + wait;
            state.Level++;
            state.Failures = 0;
            return wait;
        }
    }

    /// <summary>Não deixa a memória crescer: esquece quem não falha há mais de um dia e não está bloqueado.</summary>
    private void Prune(DateTimeOffset now)
    {
        if (_states.Count < 10_000)
        {
            return;
        }

        foreach (var (email, state) in _states)
        {
            if (state.Pending == 0 && (state.LockedUntil is null || state.LockedUntil < now) && now - state.LastFailure > Forget)
            {
                _states.TryRemove(email, out _);
            }
        }
    }
}
