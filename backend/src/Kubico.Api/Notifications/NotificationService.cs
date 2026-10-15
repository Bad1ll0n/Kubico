using Kubico.Api.Realtime;
using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace Kubico.Api.Notifications;

public record NotificationDto(
    Guid Id,
    Guid HouseholdId,
    NotificationKind Kind,
    string Title,
    string? Body,
    Guid? EntityId,
    DateTimeOffset CreatedAt,
    bool IsRead);

/// <summary>
/// Cria avisos para os membros e mostra-os logo na app (pelo canal pessoal de cada um). O envio para o
/// telemóvel fica para o <see cref="NotificationDispatcher"/>, que respeita as preferências.
/// </summary>
public class NotificationService(KubicoDbContext db, IHubContext<HouseholdHub> hub, TimeProvider clock)
{
    /// <summary>Um aviso para cada destinatário, menos para quem fez a alteração (esse já sabe).</summary>
    public async Task NotifyAsync(
        Guid householdId,
        IEnumerable<Guid> recipients,
        Guid actorUserId,
        NotificationKind kind,
        string title,
        string? body,
        Guid? entityId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var created = recipients
            .Distinct()
            .Where(userId => userId != actorUserId)
            .Select(userId => new Notification
            {
                UserId = userId,
                HouseholdId = householdId,
                Kind = kind,
                Title = Trim(title, 200),
                Body = body is null ? null : Trim(body, 500),
                EntityId = entityId,
                CreatedAt = now,
            })
            .ToList();
        if (created.Count == 0)
        {
            return;
        }

        db.Notifications.AddRange(created);
        await db.SaveChangesAsync(ct);

        foreach (var notification in created)
        {
            await hub.Clients.Group(HouseholdHub.UserGroupName(notification.UserId))
                .SendAsync(HouseholdEvents.NotificationCreated, ToDto(notification), ct);
        }
    }

    public static NotificationDto ToDto(Notification n) =>
        new(n.Id, n.HouseholdId, n.Kind, n.Title, n.Body, n.EntityId, n.CreatedAt, n.ReadAt is not null);

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
