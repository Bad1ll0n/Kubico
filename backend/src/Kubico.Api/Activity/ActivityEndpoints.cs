using System.Security.Claims;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Api.Rewards;
using Kubico.Api.Shopping;
using Kubico.Domain.Activity;
using Kubico.Domain.Calendar;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;
using Kubico.Domain.Shopping;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Activity;

/// <summary>
/// Uma alteração no histórico. <c>CanUndo</c>: quem pede pode desfazê-la agora (é a última alteração ao que mudou, e
/// foi quem a fez ou gere essa área). <c>IsUndo</c>: esta entrada é o desfazer de outra.
/// </summary>
public record ActivityDto(
    long Id,
    Guid UserId,
    HouseholdModule? Module,
    string Summary,
    DateTimeOffset CreatedAt,
    bool CanUndo,
    DateTimeOffset? UndoneAt,
    Guid? UndoneByUserId,
    bool IsUndo);

/// <summary>Histórico da casa (últimos 30 dias): quem mudou o quê, com a possibilidade de desfazer.</summary>
public static class ActivityEndpoints
{
    public static RouteGroupBuilder MapActivityEndpoints(this RouteGroupBuilder api)
    {
        var activity = api.MapGroup("/households/{householdId:guid}/activity")
            .WithTags("Households")
            .RequireAuthorization();

        activity.MapGet("/", GetActivity);
        activity.MapPost("/{entryId:long}/undo", Undo);
        return api;
    }

    /// <summary>As alterações das áreas que a pessoa vê (e as da casa), das mais recentes para as mais antigas.</summary>
    private static async Task<IResult> GetActivity(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (role, error) = await access.RequireMemberAsync(me, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        // Ler o histórico já não apaga nada (a limpeza dos 30 dias é do MaintenanceWorker, uma vez por dia): aqui só
        // se deixam de fora as entradas mais antigas. Também não se leem os valores de antes (UndoData, que podem ter
        // dezenas de KB por entrada): basta saber se existem.
        var oldest = clock.GetUtcNow() - ActivityEntry.KeepFor;
        var levels = await access.GetAllAccessAsync(householdId, role!.Value, ct);
        var entries = (await db.ActivityEntries.AsNoTracking()
            .Where(a => a.HouseholdId == householdId && a.CreatedAt >= oldest)
            .OrderByDescending(a => a.Id)
            .Take(300)
            .Select(a => new
            {
                a.Id, a.UserId, a.Module, a.RequiredAccess, a.PrivateToUserId, a.Summary, a.EntityKeys, a.UndoOfId,
                a.CreatedAt, a.UndoneAt, a.UndoneByUserId, HasUndoData = a.UndoData != null,
            })
            .ToListAsync(ct))
            .Select(a => new ActivityEntry
            {
                Id = a.Id, HouseholdId = householdId, UserId = a.UserId, Module = a.Module, RequiredAccess = a.RequiredAccess,
                PrivateToUserId = a.PrivateToUserId, Summary = a.Summary, EntityKeys = a.EntityKeys, UndoOfId = a.UndoOfId,
                CreatedAt = a.CreatedAt, UndoneAt = a.UndoneAt, UndoneByUserId = a.UndoneByUserId,
                UndoData = a.HasUndoData ? string.Empty : null,
            })
            .ToList();

        // Do mais recente para o mais antigo: uma entrada só se desfaz se nada mais recente mexeu nas mesmas coisas.
        var touched = new HashSet<string>();
        var result = new List<ActivityDto>();
        foreach (var entry in entries)
        {
            var latest = !ActivityLog.OwnKeys(entry).Any(touched.Contains);
            if (entry.UndoneAt is null && entry.UndoOfId is null)
            {
                touched.UnionWith(ActivityLog.AllKeys(entry.EntityKeys));
            }

            if (entry.Module is { } module && levels[module] < ModuleAccess.View)
            {
                continue;
            }

            // Alterações sobre uma pessoa (a mesada): só ela e quem gere a área as veem.
            if (entry.PrivateToUserId is { } subject && subject != me
                && !(entry.Module is { } own && levels[own] >= ModuleAccess.Manage))
            {
                continue;
            }

            var canUndo = latest && entry.CanBeUndone && entry.Module is { } area
                && levels[area] >= entry.RequiredAccess
                && (entry.UserId == me || levels[area] >= ModuleAccess.Manage);
            result.Add(ToDto(entry, canUndo, Texts.LanguageOf(http)));
        }

        return Results.Ok(result);
    }

    /// <summary>
    /// Desfaz uma alteração: o que foi criado sai, o que mudou ou foi apagado volta ao que era. É preciso ter ainda o
    /// acesso que a alteração pediu (e ser quem a fez, ou gerir a área). Se desfazer tirar pontos que já foram
    /// trocados, responde 409 (código "points-spent"); com <c>force=true</c>, quem gere a área confirma.
    /// </summary>
    private static async Task<IResult> Undo(
        Guid householdId,
        long entryId,
        bool? force,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        ActivityLog log,
        HouseholdNotifier notifier,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (role, error) = await access.RequireMemberAsync(me, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        // Mais de 30 dias: é como se já tivesse sido apagada (a limpeza corre uma vez por dia).
        var oldest = clock.GetUtcNow() - ActivityEntry.KeepFor;
        var entry = await db.ActivityEntries.SingleOrDefaultAsync(
            a => a.Id == entryId && a.HouseholdId == householdId && a.CreatedAt >= oldest, ct);
        if (entry is null)
        {
            return Results.NotFound();
        }

        var level = entry.Module is { } module ? await access.GetAccessAsync(householdId, role!.Value, module, ct) : ModuleAccess.None;
        if (level < ModuleAccess.View)
        {
            return Results.NotFound();
        }

        if (!entry.CanBeUndone)
        {
            return Results.Problem(
                title: entry.UndoneAt is null ? "Esta alteração não se pode desfazer." : "Esta alteração já foi desfeita.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (entry.UserId != me && level < ModuleAccess.Manage)
        {
            return Results.Problem(
                title: "Só quem fez esta alteração (ou quem gere esta área) a pode desfazer.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (level < entry.RequiredAccess)
        {
            return Results.Problem(
                title: "Já não tens acesso para desfazer esta alteração.", statusCode: StatusCodes.Status403Forbidden);
        }

        // Antes de desfazer: de que lista é cada artigo (para avisar a lista certa se o artigo desaparecer).
        var itemLists = new Dictionary<Guid, Guid>();
        foreach (var (type, id) in ActivityLog.OwnKeys(entry).Select(ActivityLog.Parse).Where(k => k.Type == typeof(ShoppingItem)))
        {
            if (await db.ShoppingItems.FindAsync([id], ct) is { } item)
            {
                itemLists[id] = item.ListId;
            }
        }

        var keys = await log.UndoAsync(entry, me, ct);
        if (keys is null)
        {
            return Results.Problem(
                title: "Entretanto houve outras alterações a isto. Desfaz primeiro as mais recentes.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Desfazer uma tarefa feita tira os pontos a quem a fez: não se deixam saldos negativos sem confirmação.
        if (await SpentPointsAsync(db, householdId, force == true && level >= ModuleAccess.Manage, ct) is { } spent)
        {
            return spent;
        }

        var undo = log.Capture(householdId, me, entry.Module, $"desfez: {entry.Summary}", undoable: false);
        undo.UndoOfId = entry.Id;
        await db.SaveChangesAsync(ct);

        await NotifyAsync(db, notifier, householdId, keys, itemLists, ct);
        return Results.Ok(ToDto(entry, false, Texts.LanguageOf(http)));
    }

    /// <summary>
    /// Quanto o desfazer (ainda por gravar) muda o saldo de cada pessoa: conclusões que saem ou deixam de contar tiram
    /// pontos, as que voltam dão. Tranca o saldo de quem perde pontos e devolve o 409 se algum ficasse negativo.
    /// </summary>
    private static async Task<IResult?> SpentPointsAsync(KubicoDbContext db, Guid householdId, bool force, CancellationToken ct)
    {
        var losses = new Dictionary<Guid, int>();
        foreach (var change in db.ChangeTracker.Entries<ChoreCompletion>())
        {
            var counted = change.Property(c => c.RejectedAt).OriginalValue is null;
            var counts = change.Entity.RejectedAt is null;
            var delta = change.State switch
            {
                EntityState.Deleted when counted => -change.Entity.Points,
                EntityState.Added when counts => change.Entity.Points,
                EntityState.Modified when counted != counts => counts ? change.Entity.Points : -change.Entity.Points,
                _ => 0,
            };
            losses[change.Entity.UserId] = losses.GetValueOrDefault(change.Entity.UserId) + delta;
        }

        foreach (var (userId, delta) in losses.Where(l => l.Value < 0))
        {
            if (await Points.LockAsync(db, householdId, userId, ct) is null || force)
            {
                continue;
            }

            var left = await Points.AvailableAsync(db, householdId, userId, ct) + delta;
            if (left < 0)
            {
                var name = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(ct);
                return Points.SpentProblem(name, -left);
            }
        }

        return null;
    }

    /// <summary>Avisa as apps do estado novo de cada coisa que o desfazer tocou.</summary>
    private static async Task NotifyAsync(
        KubicoDbContext db,
        HouseholdNotifier notifier,
        Guid householdId,
        IReadOnlyList<string> keys,
        Dictionary<Guid, Guid> itemLists,
        CancellationToken ct)
    {
        // Os artigos que saíram vão juntos (por lista), e cada lista tocada leva as contagens novas no fim.
        var removedItems = new Dictionary<Guid, List<Guid>>();
        var touchedLists = new HashSet<Guid>();
        foreach (var (type, id) in keys.Select(ActivityLog.Parse))
        {
            if (type == typeof(ShoppingItem))
            {
                if (await db.ShoppingItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct) is { } item)
                {
                    await notifier.ShoppingItemUpserted(householdId, ShoppingEndpoints.ToDto(item), ct);
                    touchedLists.Add(item.ListId);
                }
                else if (itemLists.TryGetValue(id, out var listId))
                {
                    (removedItems.TryGetValue(listId, out var ids) ? ids : removedItems[listId] = []).Add(id);
                    touchedLists.Add(listId);
                }
            }
            else if (type == typeof(ShoppingList))
            {
                var list = await db.ShoppingLists.AsNoTracking()
                    .Where(l => l.Id == id)
                    .Select(l => new ShoppingListDto(l.Id, l.HouseholdId, l.Name, l.Items.Count, l.Items.Count(i => i.IsChecked)))
                    .SingleOrDefaultAsync(ct);
                await (list is null ? notifier.ShoppingListDeleted(householdId, id, ct) : notifier.ShoppingListUpserted(householdId, list, ct));
            }
            else if (type == typeof(Chore))
            {
                var chore = await db.Chores.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
                await (chore is null ? notifier.ChoreDeleted(householdId, id, ct) : notifier.ChoreUpserted(householdId, ChoreEndpoints.ToDto(chore), ct));
            }
            else if (type == typeof(CalendarEvent))
            {
                var calendarEvent = await db.CalendarEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
                await (calendarEvent is null
                    ? notifier.CalendarEventDeleted(householdId, id, ct)
                    : notifier.CalendarEventUpserted(householdId, CalendarEndpoints.ToDto(calendarEvent), ct));
            }
            else if (type == typeof(Reward))
            {
                var reward = await db.Rewards.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
                await (reward is null ? notifier.RewardDeleted(householdId, id, ct) : notifier.RewardUpserted(householdId, RewardEndpoints.ToDto(reward), ct));
            }
        }

        // Desfazer uma tarefa feita (ou a tarefa toda) muda os pontos.
        if (keys.Select(ActivityLog.Parse).Any(k => k.Type == typeof(ChoreCompletion) || k.Type == typeof(Chore)))
        {
            await notifier.PointsChanged(householdId, ct);
        }

        foreach (var (listId, ids) in removedItems)
        {
            await notifier.ShoppingItemsDeleted(householdId, listId, ids, ct);
        }

        foreach (var listId in touchedLists)
        {
            await ShoppingEndpoints.NotifyCountsAsync(db, notifier, householdId, listId, ct);
        }
    }

    private static ActivityDto ToDto(ActivityEntry a, bool canUndo, string language) => new(
        a.Id, a.UserId, a.Module, Texts.Translate(a.Summary, language), a.CreatedAt, canUndo, a.UndoneAt, a.UndoneByUserId, a.UndoOfId is not null);
}
