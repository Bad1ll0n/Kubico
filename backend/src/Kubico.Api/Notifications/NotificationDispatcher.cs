using System.Collections.Concurrent;
using Kubico.Api.Common;
using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Notifications;

/// <summary>
/// Leva para os telemóveis os avisos por enviar, segundo as preferências de cada membro: já, num resumo diário,
/// ou nunca (só na app); nunca durante as horas de silêncio. Os avisos já lidos na app não se enviam.
/// <para>
/// Trabalha por lotes de pessoas (<see cref="BatchSize"/>): as preferências, as línguas e os telemóveis de um lote
/// leem-se com uma consulta cada, os envios correm em paralelo (no máximo <see cref="Parallelism"/> ao mesmo tempo) e
/// grava-se uma vez por lote. Quem está em horas de silêncio ou à espera do resumo fica com
/// <see cref="NotificationPreferences.NextPushAt"/> e só volta a ser lido a essa hora (antes era lido e saltado a cada
/// minuto, com uma consulta por pessoa).
/// </para>
/// </summary>
public class NotificationDispatcher(KubicoDbContext db, IPushSender sender, TimeProvider clock)
{
    /// <summary>Quantos títulos entram no texto do resumo.</summary>
    private const int DigestTitles = 3;

    /// <summary>Pessoas por lote.</summary>
    public const int BatchSize = 500;

    /// <summary>Envios ao mesmo tempo (o FCM demora ~100 ms por mensagem).</summary>
    public const int Parallelism = 8;

    /// <summary>Lotes por volta: o resto fica para o minuto seguinte.</summary>
    private const int MaxBatches = 20;

    /// <summary>Trata os avisos por enviar; devolve quantas mensagens saíram.</summary>
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sent = 0;
        var seen = new HashSet<Guid>();
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            // Quem tem avisos por enviar e não está à espera (silêncio, resumo).
            var users = await db.Notifications.AsNoTracking()
                .Where(n => n.PushedAt == null)
                .Where(n => !db.NotificationPreferences.Any(p => p.UserId == n.UserId && p.NextPushAt > now))
                .Select(n => n.UserId)
                .Distinct()
                .Where(id => !seen.Contains(id))
                .Take(BatchSize)
                .ToListAsync(ct);
            if (users.Count == 0)
            {
                break;
            }

            seen.UnionWith(users);
            sent += await DispatchBatchAsync(users, now, ct);
            db.ChangeTracker.Clear();
        }

        return sent;
    }

    private async Task<int> DispatchBatchAsync(List<Guid> users, DateTimeOffset now, CancellationToken ct)
    {
        var pending = (await db.Notifications
                .Where(n => n.PushedAt == null && users.Contains(n.UserId))
                .OrderBy(n => n.CreatedAt)
                .ToListAsync(ct))
            .ToLookup(n => n.UserId);
        var preferencesOf = await db.NotificationPreferences.Where(p => users.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, ct);
        var languages = await db.Users.Where(u => users.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Language, ct);
        var devicesOf = (await db.PushDevices.Where(d => users.Contains(d.UserId)).ToListAsync(ct)).ToLookup(d => d.UserId);

        var jobs = new List<Job>();
        foreach (var userId in users)
        {
            var group = pending[userId].ToList();
            if (group.Count == 0)
            {
                continue;
            }

            if (!preferencesOf.TryGetValue(userId, out var preferences))
            {
                preferences = new NotificationPreferences { UserId = userId };
                db.NotificationPreferences.Add(preferences);
            }

            var unread = group.Where(n => n.ReadAt is null).ToList();
            if (preferences.Delivery != NotificationDelivery.InAppOnly && unread.Count > 0 && !preferences.ShouldPushNow(now))
            {
                // Horas de silêncio, ou ainda não é a hora do resumo: até lá, esta pessoa não volta a ser lida.
                preferences.NextPushAt = preferences.NextPushAfter(now);
                continue;
            }

            preferences.NextPushAt = null;
            List<PushMessage> messages = preferences.Delivery switch
            {
                NotificationDelivery.InAppOnly => [],
                NotificationDelivery.DailyDigest when unread.Count > 0 =>
                    [Digest(unread, Texts.Normalize(languages.GetValueOrDefault(userId)))],
                _ => [.. unread.Select(ToMessage)],
            };
            jobs.Add(new Job(group, preferences, [.. devicesOf[userId]], messages));
        }

        // Os envios em paralelo (com limite); o resultado de cada um aplica-se depois, já fora do paralelo, porque o
        // DbContext não pode ser usado por várias threads.
        var results = new ConcurrentBag<(Job Job, PushDevice Device, PushResult Result)>();
        await Parallel.ForEachAsync(
            jobs.SelectMany(job => job.Devices.SelectMany(device => job.Messages.Select(message => (job, device, message)))),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (send, token) => results.Add((send.job, send.device, await sender.SendAsync(send.device.Token, send.message, token))));

        var sent = results.Count(r => r.Result == PushResult.Sent);
        foreach (var device in results.Where(r => r.Result == PushResult.InvalidToken).Select(r => r.Device).Distinct())
        {
            db.PushDevices.Remove(device);
        }

        var failed = results.Where(r => r.Result == PushResult.Failed).Select(r => r.Job).ToHashSet();
        foreach (var job in jobs.Where(j => !failed.Contains(j)))
        {
            // Falhas passageiras ficam para a próxima volta; o resto fica tratado (mesmo sem telemóveis).
            foreach (var notification in job.Notifications)
            {
                notification.PushedAt = now;
            }

            if (job.Preferences.Delivery == NotificationDelivery.DailyDigest && job.Messages.Count > 0)
            {
                job.Preferences.LastDigestAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    private sealed record Job(
        IReadOnlyList<Notification> Notifications, NotificationPreferences Preferences, IReadOnlyList<PushDevice> Devices,
        IReadOnlyList<PushMessage> Messages);

    private static PushMessage ToMessage(Notification n) => new(n.Title, n.Body, Data(n));

    private static PushMessage Digest(IReadOnlyList<Notification> unread, string language)
    {
        if (unread.Count == 1)
        {
            return ToMessage(unread[0]);
        }

        var titles = string.Join(" · ", unread.Take(DigestTitles).Select(n => n.Title));
        return new PushMessage(
            Texts.Format(language, "Kubico: {0} novidades", unread.Count),
            unread.Count > DigestTitles ? Texts.Format(language, "{0} e mais {1}", titles, unread.Count - DigestTitles) : titles,
            new Dictionary<string, string> { ["kind"] = "Digest" });
    }

    private static Dictionary<string, string> Data(Notification n) => new()
    {
        ["notificationId"] = n.Id.ToString(),
        ["householdId"] = n.HouseholdId.ToString(),
        ["kind"] = n.Kind.ToString(),
        ["entityId"] = n.EntityId?.ToString() ?? "",
    };
}

/// <summary>Corre o <see cref="NotificationDispatcher"/> a cada minuto (desligável com Notifications:DispatchEnabled).</summary>
public class NotificationWorker(IServiceScopeFactory scopes, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Falha ao enviar os avisos; volta a tentar daqui a um minuto.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
