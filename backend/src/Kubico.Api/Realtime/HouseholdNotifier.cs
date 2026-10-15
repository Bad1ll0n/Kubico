using Kubico.Api.Calendar;
using Kubico.Api.Chores;
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
}

public class HouseholdNotifier(IHubContext<HouseholdHub> hub)
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

    public Task CalendarEventUpserted(Guid householdId, CalendarEventDto calendarEvent, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.CalendarEventUpserted, calendarEvent, ct);

    public Task CalendarEventDeleted(Guid householdId, Guid eventId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.CalendarEventDeleted, eventId, ct);

    public Task RewardUpserted(Guid householdId, RewardDto reward, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.RewardUpserted, reward, ct);

    public Task RewardDeleted(Guid householdId, Guid rewardId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.RewardDeleted, rewardId, ct);

    public Task RedemptionUpserted(Guid householdId, RedemptionDto redemption, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.RedemptionUpserted, redemption, ct);

    public Task RedemptionDeleted(Guid householdId, Guid redemptionId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.RedemptionDeleted, redemptionId, ct);

    private IClientProxy Group(Guid householdId) => hub.Clients.Group(HouseholdHub.GroupName(householdId));
}
