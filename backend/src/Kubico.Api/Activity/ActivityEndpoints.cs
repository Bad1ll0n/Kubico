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
        ActivityLog log,
        HttpContext http,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (role, error) = await access.RequireMemberAsync(me, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        await log.PruneAsync(householdId, ct);
        var levels = await access.GetAllAccessAsync(householdId, role!.Value, ct);
        var entries = await db.ActivityEntries.AsNoTracking()
            .Where(a => a.HouseholdId == householdId)
            .OrderByDescending(a => a.Id)
            .Take(300)
            .ToListAsync(ct);

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

            var canUndo = latest && entry.CanBeUndone
                && (entry.UserId == me || (entry.Module is { } area && levels[area] >= ModuleAccess.Manage));
            result.Add(ToDto(entry, canUndo, Texts.LanguageOf(http)));
        }

        return Results.Ok(result);
    }

    /// <summary>Desfaz uma alteração: o que foi criado sai, o que mudou ou foi apagado volta ao que era.</summary>
    private static async Task<IResult> Undo(
        Guid householdId,
        long entryId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        ActivityLog log,
        HouseholdNotifier notifier,
        HttpContext http,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (role, error) = await access.RequireMemberAsync(me, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var entry = await db.ActivityEntries.SingleOrDefaultAsync(a => a.Id == entryId && a.HouseholdId == householdId, ct);
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

        var undo = log.Capture(householdId, me, entry.Module, $"desfez: {entry.Summary}", undoable: false);
        undo.UndoOfId = entry.Id;
        await db.SaveChangesAsync(ct);

        await NotifyAsync(db, notifier, householdId, keys, itemLists, ct);
        return Results.Ok(ToDto(entry, false, Texts.LanguageOf(http)));
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
        foreach (var (type, id) in keys.Select(ActivityLog.Parse))
        {
            if (type == typeof(ShoppingItem))
            {
                if (await db.ShoppingItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct) is { } item)
                {
                    await notifier.ShoppingItemUpserted(householdId, ShoppingEndpoints.ToDto(item), ct);
                }
                else if (itemLists.TryGetValue(id, out var listId))
                {
                    await notifier.ShoppingItemDeleted(householdId, listId, id, ct);
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
    }

    private static ActivityDto ToDto(ActivityEntry a, bool canUndo, string language) => new(
        a.Id, a.UserId, a.Module, Texts.Translate(a.Summary, language), a.CreatedAt, canUndo, a.UndoneAt, a.UndoneByUserId, a.UndoOfId is not null);
}
