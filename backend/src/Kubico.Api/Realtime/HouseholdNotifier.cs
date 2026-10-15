using Kubico.Api.Calendar;
using Kubico.Api.Chores;
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

    private IClientProxy Group(Guid householdId) => hub.Clients.Group(HouseholdHub.GroupName(householdId));
}
