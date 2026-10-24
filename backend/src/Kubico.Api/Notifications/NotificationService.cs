using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

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
    /// <summary>
    /// Um aviso para cada destinatário, menos para quem fez a alteração (esse já sabe). Os textos escrevem-se em
    /// português; cada um recebe-os na língua da sua app.
    /// </summary>
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
        var ids = recipients.Distinct().Where(userId => userId != actorUserId).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var languages = await db.Users.Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Language })
            .ToDictionaryAsync(u => u.Id, u => u.Language, ct);
        var created = ids
            .Select(userId =>
            {
                var language = languages.GetValueOrDefault(userId, Texts.Portuguese);
                return new Notification
                {
                    UserId = userId,
                    HouseholdId = householdId,
                    Kind = kind,
                    Title = Trim(Texts.Translate(title, language), 200),
                    Body = body is null ? null : Trim(Texts.Translate(body, language), 500),
                    EntityId = entityId,
                    CreatedAt = now,
                };
            })
            .ToList();

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
