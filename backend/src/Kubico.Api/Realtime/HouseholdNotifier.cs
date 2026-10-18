using Kubico.Api.Calendar;
using Kubico.Api.Calendar.Sync;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Api.Shopping;
using Microsoft.AspNetCore.SignalR;

namespace Kubico.Api.Realtime;

/// <summary>Nomes dos eventos enviados aos clientes. Mantidos iguais na app Flutter.</summary>
public static class HouseholdEvents
{
    public const string ShoppingListUpserted = "ShoppingListUpserted";
    public const string ShoppingListDeleted = "ShoppingListDeleted";
    public const string ShoppingItemUpserted = "ShoppingItemUpserted";
    public const string ShoppingItemDeleted = "ShoppingItemDeleted";
    public const string ChoreUpserted = "ChoreUpserted";
    public const string ChoreDeleted = "ChoreDeleted";
    public const string CalendarEventUpserted = "CalendarEventUpserted";
    public const string CalendarEventDeleted = "CalendarEventDeleted";
    public const string RewardUpserted = "RewardUpserted";
    public const string RewardDeleted = "RewardDeleted";
    public const string RedemptionUpserted = "RedemptionUpserted";
    public const string RedemptionDeleted = "RedemptionDeleted";
    public const string MemberUpserted = "MemberUpserted";
    public const string AbsenceUpserted = "AbsenceUpserted";
    public const string AbsenceDeleted = "AbsenceDeleted";

    /// <summary>Alguém saiu ou foi tirado da casa. Vai também para o canal pessoal dessa pessoa.</summary>
    public const string MemberDeleted = "MemberDeleted";

    /// <summary>Um aviso pessoal novo (vai só para o canal da pessoa, não para a casa toda).</summary>
    public const string NotificationCreated = "NotificationCreated";

    /// <summary>Uma ligação a um calendário externo mudou (estado, última sincronização). Só para o dono.</summary>
    public const string CalendarConnectionUpserted = "CalendarConnectionUpserted";
    public const string CalendarConnectionDeleted = "CalendarConnectionDeleted";
}

/// <summary>
/// Avisa as apps das alterações em tempo real. As alterações ao calendário também pedem uma sincronização com os
/// calendários externos ligados.
/// </summary>
public class HouseholdNotifier(IHubContext<HouseholdHub> hub, CalendarSyncTrigger calendarSync)
{
    public Task ShoppingListUpserted(Guid householdId, ShoppingListDto list, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.ShoppingListUpserted, list, ct);

    public Task ShoppingListDeleted(Guid householdId, Guid listId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.ShoppingListDeleted, listId, ct);

    public Task ShoppingItemUpserted(Guid householdId, ShoppingItemDto item, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.ShoppingItemUpserted, item, ct);

    public Task ShoppingItemDeleted(Guid householdId, Guid listId, Guid itemId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.ShoppingItemDeleted, listId, itemId, ct);

    public Task ChoreUpserted(Guid householdId, ChoreDto chore, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.ChoreUpserted, chore, ct);

    public Task ChoreDeleted(Guid householdId, Guid choreId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.ChoreDeleted, choreId, ct);

    public Task CalendarEventUpserted(Guid householdId, CalendarEventDto calendarEvent, CancellationToken ct)
    {
        calendarSync.Request(householdId);
        return Group(householdId).SendAsync(HouseholdEvents.CalendarEventUpserted, calendarEvent, ct);
    }

    public Task CalendarEventDeleted(Guid householdId, Guid eventId, CancellationToken ct)
    {
        calendarSync.Request(householdId);
        return Group(householdId).SendAsync(HouseholdEvents.CalendarEventDeleted, eventId, ct);
    }

    public Task AbsenceUpserted(Guid householdId, AbsenceDto absence, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.AbsenceUpserted, absence, ct);

    public Task AbsenceDeleted(Guid householdId, Guid absenceId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.AbsenceDeleted, absenceId, ct);

    public Task MemberUpserted(Guid householdId, MemberDto member, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.MemberUpserted, householdId, member, ct);

    public async Task MemberDeleted(Guid householdId, Guid userId, CancellationToken ct)
    {
        await Group(householdId).SendAsync(HouseholdEvents.MemberDeleted, householdId, userId, ct);
        await User(userId).SendAsync(HouseholdEvents.MemberDeleted, householdId, userId, ct);
    }

    public Task CalendarConnectionUpserted(Guid userId, CalendarConnectionDto connection, CancellationToken ct) =>
        User(userId).SendAsync(HouseholdEvents.CalendarConnectionUpserted, connection, ct);

    public Task CalendarConnectionDeleted(Guid userId, Guid householdId, Guid connectionId, CancellationToken ct) =>
        User(userId).SendAsync(HouseholdEvents.CalendarConnectionDeleted, householdId, connectionId, ct);

    public Task RewardUpserted(Guid householdId, RewardDto reward, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.RewardUpserted, reward, ct);

    public Task RewardDeleted(Guid householdId, Guid rewardId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.RewardDeleted, rewardId, ct);

    public Task RedemptionUpserted(Guid householdId, RedemptionDto redemption, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.RedemptionUpserted, redemption, ct);

    public Task RedemptionDeleted(Guid householdId, Guid redemptionId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.RedemptionDeleted, redemptionId, ct);

    private IClientProxy Group(Guid householdId) => hub.Clients.Group(HouseholdHub.GroupName(householdId));

    private IClientProxy User(Guid userId) => hub.Clients.Group(HouseholdHub.UserGroupName(userId));
}
