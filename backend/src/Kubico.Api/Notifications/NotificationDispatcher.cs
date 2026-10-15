using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Notifications;

/// <summary>
/// Leva para os telemóveis os avisos por enviar, segundo as preferências de cada membro: já, num resumo diário,
/// ou nunca (só na app); nunca durante as horas de silêncio. Os avisos já lidos na app não se enviam.
/// </summary>
public class NotificationDispatcher(KubicoDbContext db, IPushSender sender, TimeProvider clock)
{
    /// <summary>Quantos títulos entram no texto do resumo.</summary>
    private const int DigestTitles = 3;

    /// <summary>Trata os avisos por enviar; devolve quantas mensagens saíram.</summary>
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pending = await db.Notifications
            .Where(n => n.PushedAt == null)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var group in pending.GroupBy(n => n.UserId))
        {
            var userId = group.Key;
            var preferences = await db.NotificationPreferences.FindAsync([userId], ct);
            if (preferences is null)
            {
                preferences = new NotificationPreferences { UserId = userId };
                db.NotificationPreferences.Add(preferences);
            }

            var unread = group.Where(n => n.ReadAt is null).ToList();
            if (preferences.Delivery != NotificationDelivery.InAppOnly && unread.Count > 0 && !preferences.ShouldPushNow(now))
            {
                continue; // Horas de silêncio, ou ainda não é a hora do resumo.
            }

            var messages = preferences.Delivery switch
            {
                NotificationDelivery.InAppOnly => [],
                NotificationDelivery.DailyDigest when unread.Count > 0 => [Digest(unread)],
                _ => unread.Select(ToMessage).ToList(),
            };

            var devices = await db.PushDevices.Where(d => d.UserId == userId).ToListAsync(ct);
            var failed = false;
            foreach (var device in devices)
            {
                foreach (var message in messages)
                {
                    switch (await sender.SendAsync(device.Token, message, ct))
                    {
                        case PushResult.Sent:
                            sent++;
                            break;
                        case PushResult.InvalidToken:
                            db.PushDevices.Remove(device);
                            break;
                        case PushResult.Failed:
                            failed = true;
                            break;
                    }
                }
            }

            // Falhas passageiras ficam para a próxima volta; o resto fica tratado (mesmo sem telemóveis).
            if (!failed)
            {
                foreach (var notification in group)
                {
                    notification.PushedAt = now;
                }

                if (preferences.Delivery == NotificationDelivery.DailyDigest && messages.Count > 0)
                {
                    preferences.LastDigestAt = now;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        return sent;
    }

    private static PushMessage ToMessage(Notification n) => new(n.Title, n.Body, Data(n));

    private static PushMessage Digest(IReadOnlyList<Notification> unread)
    {
        if (unread.Count == 1)
        {
            return ToMessage(unread[0]);
        }

        var titles = string.Join(" · ", unread.Take(DigestTitles).Select(n => n.Title));
        var more = unread.Count > DigestTitles ? $" e mais {unread.Count - DigestTitles}" : "";
        return new PushMessage(
            $"Kubico: {unread.Count} novidades",
            titles + more,
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
