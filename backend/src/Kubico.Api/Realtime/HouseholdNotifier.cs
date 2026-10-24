using Kubico.Api.Calendar;
using Kubico.Api.Calendar.Sync;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.SignalR;

namespace Kubico.Api.Realtime;

/// <summary>Nomes dos eventos enviados aos clientes. Mantidos iguais na app Flutter.</summary>
public static class HouseholdEvents
{
    public const string ShoppingListUpserted = "ShoppingListUpserted";
    public const string ShoppingListDeleted = "ShoppingListDeleted";
    public const string ShoppingItemUpserted = "ShoppingItemUpserted";
    public const string ShoppingItemDeleted = "ShoppingItemDeleted";

    /// <summary>Vários artigos de uma lista saíram de uma vez (limpar o carrinho): leva o id da lista e os dos artigos.</summary>
    public const string ShoppingItemsDeleted = "ShoppingItemsDeleted";
    public const string ChoreUpserted = "ChoreUpserted";
    public const string ChoreDeleted = "ChoreDeleted";
    public const string CalendarEventUpserted = "CalendarEventUpserted";
    public const string CalendarEventDeleted = "CalendarEventDeleted";

    /// <summary>Vários eventos saíram de uma vez (uma série e as suas exceções): leva os ids.</summary>
    public const string CalendarEventsDeleted = "CalendarEventsDeleted";
    public const string RewardUpserted = "RewardUpserted";
    public const string RewardDeleted = "RewardDeleted";
    public const string RedemptionUpserted = "RedemptionUpserted";
    public const string RedemptionDeleted = "RedemptionDeleted";

    /// <summary>
    /// Mudaram os pontos ganhos (uma tarefa feita, não aceite, desfeita ou apagada): os saldos, a mesada e as
    /// estatísticas voltam a ler-se. Vai para a casa toda, só com o id da casa. Assim a app não tem de os voltar a pedir
    /// a cada <c>ChoreUpserted</c> (mudar o título de uma tarefa não mexe nos pontos).
    /// </summary>
    public const string PointsChanged = "PointsChanged";

    /// <summary>Mudou a mesada de alguém (valores, ou um período pago). Leva o id da pessoa.</summary>
    public const string AllowanceChanged = "AllowanceChanged";
    public const string MemberUpserted = "MemberUpserted";
    public const string AbsenceUpserted = "AbsenceUpserted";
    public const string AbsenceDeleted = "AbsenceDeleted";

    /// <summary>Alguém saiu ou foi tirado da casa. Vai também para o canal pessoal dessa pessoa.</summary>
    public const string MemberDeleted = "MemberDeleted";

    /// <summary>A casa foi apagada (saiu o último membro com conta). Leva o id e o nome; vai para o canal de cada membro.</summary>
    public const string HouseholdDeleted = "HouseholdDeleted";

    /// <summary>Um aviso pessoal novo (vai só para o canal da pessoa, não para a casa toda).</summary>
    public const string NotificationCreated = "NotificationCreated";

    /// <summary>O administrador mudou o que cada papel pode fazer: as apps voltam a ler as permissões.</summary>
    public const string PermissionsChanged = "PermissionsChanged";

    /// <summary>Uma ligação a um calendário externo mudou (estado, última sincronização). Só para o dono.</summary>
    public const string CalendarConnectionUpserted = "CalendarConnectionUpserted";
    public const string CalendarConnectionDeleted = "CalendarConnectionDeleted";
}

/// <summary>
/// Avisa as apps das alterações em tempo real. As alterações ao calendário também pedem uma sincronização com os
/// calendários externos ligados. Para as apps não terem de voltar a pedir listas inteiras: cada alteração aos artigos
/// vai seguida do <c>ShoppingListUpserted</c> com as contagens novas (<c>ShoppingEndpoints.NotifyCountsAsync</c>), e o
/// que sai de uma vez (limpar o carrinho, uma série com exceções) vai numa só mensagem.
/// </summary>
public class HouseholdNotifier(IHubContext<HouseholdHub> hub, HouseholdGroups groups, CalendarSyncTrigger calendarSync)
{
    public Task ShoppingListUpserted(Guid householdId, ShoppingListDto list, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Shopping).SendAsync(HouseholdEvents.ShoppingListUpserted, list, ct);

    public Task ShoppingListDeleted(Guid householdId, Guid listId, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Shopping).SendAsync(HouseholdEvents.ShoppingListDeleted, listId, ct);

    public Task ShoppingItemUpserted(Guid householdId, ShoppingItemDto item, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Shopping).SendAsync(HouseholdEvents.ShoppingItemUpserted, item, ct);

    public Task ShoppingItemDeleted(Guid householdId, Guid listId, Guid itemId, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Shopping).SendAsync(HouseholdEvents.ShoppingItemDeleted, listId, itemId, ct);

    /// <summary>Uma mensagem só (em vez de uma por artigo); com um só artigo, vai o <c>ShoppingItemDeleted</c> de sempre.</summary>
    public Task ShoppingItemsDeleted(Guid householdId, Guid listId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct) =>
        itemIds.Count switch
        {
            0 => Task.CompletedTask,
            1 => ShoppingItemDeleted(householdId, listId, itemIds.Single(), ct),
            _ => Area(householdId, HouseholdModule.Shopping).SendAsync(HouseholdEvents.ShoppingItemsDeleted, listId, itemIds, ct),
        };

    public Task ChoreUpserted(Guid householdId, ChoreDto chore, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Chores).SendAsync(HouseholdEvents.ChoreUpserted, chore, ct);

    public Task ChoreDeleted(Guid householdId, Guid choreId, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Chores).SendAsync(HouseholdEvents.ChoreDeleted, choreId, ct);

    public Task CalendarEventUpserted(Guid householdId, CalendarEventDto calendarEvent, CancellationToken ct)
    {
        calendarSync.Request(householdId);
        return Area(householdId, HouseholdModule.Calendar).SendAsync(HouseholdEvents.CalendarEventUpserted, calendarEvent, ct);
    }

    public Task CalendarEventDeleted(Guid householdId, Guid eventId, CancellationToken ct)
    {
        calendarSync.Request(householdId);
        return Area(householdId, HouseholdModule.Calendar).SendAsync(HouseholdEvents.CalendarEventDeleted, eventId, ct);
    }

    /// <summary>Uma mensagem só (em vez de uma por evento); com um só, vai o <c>CalendarEventDeleted</c> de sempre.</summary>
    public Task CalendarEventsDeleted(Guid householdId, IReadOnlyCollection<Guid> eventIds, CancellationToken ct)
    {
        if (eventIds.Count <= 1)
        {
            return eventIds.Count == 0 ? Task.CompletedTask : CalendarEventDeleted(householdId, eventIds.Single(), ct);
        }

        calendarSync.Request(householdId);
        return Area(householdId, HouseholdModule.Calendar).SendAsync(HouseholdEvents.CalendarEventsDeleted, eventIds, ct);
    }

    public Task PointsChanged(Guid householdId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.PointsChanged, householdId, ct);

    public Task AbsenceUpserted(Guid householdId, AbsenceDto absence, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.AbsenceUpserted, absence, ct);

    public Task AbsenceDeleted(Guid householdId, Guid absenceId, CancellationToken ct) =>
        Group(householdId).SendAsync(HouseholdEvents.AbsenceDeleted, absenceId, ct);

    /// <summary>
    /// O papel pode ter mudado: as ligações da pessoa passam primeiro para os grupos das áreas que vê agora. Vai para
    /// a casa toda, por isso sem a data de nascimento (quem gere os perfis lê-a da casa).
    /// </summary>
    public async Task MemberUpserted(Guid householdId, MemberDto member, CancellationToken ct)
    {
        await groups.RefreshAsync(householdId, ct, member.UserId);
        await Group(householdId).SendAsync(HouseholdEvents.MemberUpserted, householdId, member with { BirthDate = null }, ct);
    }

    /// <summary>Depois de avisar, as ligações de quem saiu deixam os grupos da casa.</summary>
    public async Task MemberDeleted(Guid householdId, Guid userId, CancellationToken ct)
    {
        await Group(householdId).SendAsync(HouseholdEvents.MemberDeleted, householdId, userId, ct);
        await User(userId).SendAsync(HouseholdEvents.MemberDeleted, householdId, userId, ct);
        await groups.RefreshAsync(householdId, ct, userId);
    }

    /// <summary>A casa foi apagada: cada membro (também os perfis de criança, noutros aparelhos) fica a saber.</summary>
    public async Task HouseholdDeleted(Guid householdId, string name, IEnumerable<Guid> memberIds, CancellationToken ct)
    {
        foreach (var userId in memberIds)
        {
            await User(userId).SendAsync(HouseholdEvents.HouseholdDeleted, householdId, name, ct);
        }
    }

    public async Task PermissionsChanged(Guid householdId, CancellationToken ct)
    {
        await groups.RefreshAsync(householdId, ct);
        await Group(householdId).SendAsync(HouseholdEvents.PermissionsChanged, householdId, ct);
    }

    public Task CalendarConnectionUpserted(Guid userId, CalendarConnectionDto connection, CancellationToken ct) =>
        User(userId).SendAsync(HouseholdEvents.CalendarConnectionUpserted, connection, ct);

    public Task CalendarConnectionDeleted(Guid userId, Guid householdId, Guid connectionId, CancellationToken ct) =>
        User(userId).SendAsync(HouseholdEvents.CalendarConnectionDeleted, householdId, connectionId, ct);

    public Task RewardUpserted(Guid householdId, RewardDto reward, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Rewards).SendAsync(HouseholdEvents.RewardUpserted, reward, ct);

    public Task RewardDeleted(Guid householdId, Guid rewardId, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Rewards).SendAsync(HouseholdEvents.RewardDeleted, rewardId, ct);

    public Task RedemptionUpserted(Guid householdId, RedemptionDto redemption, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Rewards).SendAsync(HouseholdEvents.RedemptionUpserted, redemption, ct);

    public Task AllowanceChanged(Guid householdId, Guid userId, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Rewards).SendAsync(HouseholdEvents.AllowanceChanged, userId, ct);

    public Task RedemptionDeleted(Guid householdId, Guid redemptionId, CancellationToken ct) =>
        Area(householdId, HouseholdModule.Rewards).SendAsync(HouseholdEvents.RedemptionDeleted, redemptionId, ct);

    private IClientProxy Group(Guid householdId) => hub.Clients.Group(HouseholdHub.GroupName(householdId));

    private IClientProxy Area(Guid householdId, HouseholdModule module) =>
        hub.Clients.Group(HouseholdHub.ModuleGroupName(householdId, module));

    private IClientProxy User(Guid userId) => hub.Clients.Group(HouseholdHub.UserGroupName(userId));
}
