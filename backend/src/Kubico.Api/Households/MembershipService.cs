using Kubico.Api.Calendar;
using Kubico.Api.Calendar.Sync;
using Kubico.Api.Chores;
using Kubico.Api.Realtime;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Households;

/// <summary>Tirar alguém de uma casa, ou apagar a casa quando sai o último membro.</summary>
public class MembershipService(
    KubicoDbContext db,
    HouseholdNotifier notifier,
    CalendarSyncService calendarSync,
    CalendarSecrets secrets,
    ILogger<MembershipService> logger,
    TimeProvider clock)
{
    /// <summary>
    /// Tira o membro da casa e arruma o que estava preso a ele: sai das rotações e as tarefas que eram suas passam a
    /// quem vem a seguir; deixa de participar nos eventos e de levar ou ir buscar; os pedidos de recompensa à espera
    /// são cancelados; a ligação ao Google ou Outlook e os avisos desta casa desaparecem. O histórico (tarefas feitas,
    /// pontos) fica. Um perfil de criança é apagado, porque só existe nesta casa. Nas contas Google, Outlook ou iCloud
    /// ligadas por ele a esta casa, o calendário do Kubico é apagado e o acesso retirado (como no "Desligar").
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
        var connections = await db.CalendarConnections.Where(c => c.HouseholdId == householdId && c.UserId == userId).ToListAsync(ct);
        await ForgetRemoteCalendarsAsync(connections, ct);
        db.CalendarConnections.RemoveRange(connections);
        db.Notifications.RemoveRange(
            await db.Notifications.Where(n => n.HouseholdId == householdId && n.UserId == userId).ToListAsync(ct));
        db.MemberAbsences.RemoveRange(
            await db.MemberAbsences.Where(a => a.HouseholdId == householdId && a.UserId == userId).ToListAsync(ct));
        db.Allowances.RemoveRange(
            await db.Allowances.Where(a => a.HouseholdId == householdId && a.UserId == userId).ToListAsync(ct));
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

    /// <summary>
    /// Saiu o último membro: a casa e tudo o que tinha (perfis de criança incluídos) são apagados. Os calendários
    /// ligados são desligados lá fora, e os aparelhos de quem lá estava (os perfis de criança, por exemplo) sabem logo.
    /// </summary>
    public async Task DeleteHouseholdAsync(Guid householdId, CancellationToken ct)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        var members = await db.Memberships.Where(m => m.HouseholdId == householdId).Select(m => m.UserId).ToListAsync(ct);
        await ForgetRemoteCalendarsAsync(
            await db.CalendarConnections.Where(c => c.HouseholdId == householdId).ToListAsync(ct), ct);
        db.Households.Remove(household);
        await db.SaveChangesAsync(ct);
        await notifier.HouseholdDeleted(householdId, household.Name, members, ct);
    }

    /// <summary>
    /// Apaga o calendário do Kubico nas contas ligadas e retira o acesso (como o "Desligar"). Se o serviço não
    /// responder (em 10 segundos), segue na mesma: a cópia fica lá e pode ser apagada à mão.
    /// </summary>
    private async Task ForgetRemoteCalendarsAsync(IEnumerable<CalendarConnection> connections, CancellationToken ct)
    {
        foreach (var connection in connections)
        {
            if (calendarSync.Provider(connection.Provider) is not { IsConfigured: true } service)
            {
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await CalendarSyncEndpoints.ForgetRemoteAsync(service, secrets, connection, logger, timeout.Token);
        }
    }
}
