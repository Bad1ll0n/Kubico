using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Households;

/// <summary>Tirar alguém de uma casa, ou apagar a casa quando sai o último membro.</summary>
public class MembershipService(KubicoDbContext db, HouseholdNotifier notifier, TimeProvider clock)
{
    /// <summary>
    /// Tira o membro da casa e arruma o que estava preso a ele: sai das rotações e as tarefas que eram suas passam a
    /// quem vem a seguir; deixa de participar nos eventos e de levar ou ir buscar; os pedidos de recompensa à espera
    /// são cancelados; a ligação ao Google ou Outlook e os avisos desta casa desaparecem. O histórico (tarefas feitas,
    /// pontos) fica. Um perfil de criança é apagado, porque só existe nesta casa.
    /// </summary>
    public async Task RemoveAsync(Membership membership, CancellationToken ct)
    {
        var (householdId, userId) = (membership.HouseholdId, membership.UserId);
        var now = clock.GetUtcNow();

        var chores = (await db.Chores.Where(c => c.HouseholdId == householdId).ToListAsync(ct))
            .Where(c => c.RemoveMember(userId, now))
            .ToList();
        var events = (await db.CalendarEvents.Where(e => e.HouseholdId == householdId).ToListAsync(ct))
            .Where(e => e.RemoveMember(userId, now))
            .ToList();
        var redemptions = await db.RewardRedemptions
            .Where(r => r.HouseholdId == householdId && r.UserId == userId && r.Status == RedemptionStatus.Pending)
            .ToListAsync(ct);

        db.RewardRedemptions.RemoveRange(redemptions);
        db.CalendarConnections.RemoveRange(
            await db.CalendarConnections.Where(c => c.HouseholdId == householdId && c.UserId == userId).ToListAsync(ct));
        db.Notifications.RemoveRange(
            await db.Notifications.Where(n => n.HouseholdId == householdId && n.UserId == userId).ToListAsync(ct));
        db.Memberships.Remove(membership);

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.ManagedByHouseholdId == householdId)
        {
            db.Users.Remove(user);
        }

        await db.SaveChangesAsync(ct);

        foreach (var chore in chores)
        {
            await notifier.ChoreUpserted(householdId, ChoreEndpoints.ToDto(chore), ct);
        }

        foreach (var calendarEvent in events)
        {
            await notifier.CalendarEventUpserted(householdId, CalendarEndpoints.ToDto(calendarEvent), ct);
        }

        foreach (var redemption in redemptions)
        {
            await notifier.RedemptionDeleted(householdId, redemption.Id, ct);
        }

        await notifier.MemberDeleted(householdId, userId, ct);
    }

    /// <summary>Saiu o último membro: a casa e tudo o que tinha (perfis de criança incluídos) são apagados.</summary>
    public async Task DeleteHouseholdAsync(Guid householdId, CancellationToken ct)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        db.Households.Remove(household);
        await db.SaveChangesAsync(ct);
    }
}
