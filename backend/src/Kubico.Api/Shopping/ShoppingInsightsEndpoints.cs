using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Domain.Shopping;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Shopping;

/// <summary>"Costumas comprar" e a ordem das categorias aprendida nesta lista.</summary>
public record ShoppingInsightsDto(IReadOnlyList<PurchaseSuggestion> Suggestions, IReadOnlyList<string> CategoryOrder);

public record RecurringItemDto(
    Guid Id, Guid ListId, string Name, string? Quantity, string? Category, int IntervalDays, DateOnly NextDate);

public record SaveRecurringItemRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: Range(1, 90)] int IntervalDays,
    [property: StringLength(50)] string? Quantity = null,
    [property: StringLength(50)] string? Category = null,
    DateOnly? StartDate = null);

/// <summary>
/// Compras inteligentes (F4-22): o que se marca no carrinho fica registado (<see cref="PurchaseRecord"/>) e daí saem as
/// sugestões e a ordem do supermercado; os artigos recorrentes voltam à lista sozinhos (ver
/// <see cref="RecurringItemsService"/>).
/// </summary>
public static class ShoppingInsightsEndpoints
{
    /// <summary>Quanto tempo se guarda o que se comprou.</summary>
    public static readonly TimeSpan History = TimeSpan.FromDays(365);

    public static RouteGroupBuilder MapShoppingInsightsEndpoints(this RouteGroupBuilder api)
    {
        var lists = api.MapGroup("/households/{householdId:guid}/lists/{listId:guid}")
            .WithTags("Shopping")
            .RequireAuthorization();

        lists.MapGet("/insights", GetInsights);
        lists.MapGet("/recurring", GetRecurring);
        lists.MapPost("/recurring", CreateRecurring);
        lists.MapDelete("/recurring/{recurringId:guid}", DeleteRecurring);
        return api;
    }

    private static async Task<IResult> GetInsights(
        Guid householdId, Guid listId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access,
        TimeProvider clock, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Shopping, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        if (!await db.ShoppingLists.AnyAsync(l => l.Id == listId && l.HouseholdId == householdId, ct))
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var since = now - History;
        var records = await db.PurchaseRecords.AsNoTracking()
            .Where(p => p.ListId == listId && p.PurchasedAt >= since)
            .ToListAsync(ct);
        var purchases = records.Select(r => (r, household.Today(r.PurchasedAt))).ToList();
        var pending = (await db.ShoppingItems.AsNoTracking()
                .Where(i => i.ListId == listId && !i.IsChecked)
                .Select(i => i.Name)
                .ToListAsync(ct))
            .Select(ShoppingInsights.Key)
            .ToHashSet();

        return Results.Ok(new ShoppingInsightsDto(
            ShoppingInsights.Suggestions(purchases, pending, household.Today(now)),
            ShoppingInsights.CategoryOrder(purchases)));
    }

    private static async Task<IResult> GetRecurring(
        Guid householdId, Guid listId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Shopping, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var items = await db.RecurringItems.AsNoTracking()
            .Where(r => r.ListId == listId && r.HouseholdId == householdId)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
        return Results.Ok(items.Select(ToDto));
    }

    private static async Task<IResult> CreateRecurring(
        Guid householdId,
        Guid listId,
        SaveRecurringItemRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Shopping, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var list = await db.ShoppingLists.AsNoTracking().SingleOrDefaultAsync(l => l.Id == listId && l.HouseholdId == householdId, ct);
        if (list is null)
        {
            return Results.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Name)
            || request.StartDate is { } start && !SupportedDates.IsSupported(start))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = ["O nome é obrigatório."] });
        }

        var now = clock.GetUtcNow();
        var today = (await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct)).Today(now);
        var item = new RecurringItem
        {
            HouseholdId = householdId,
            ListId = listId,
            Name = request.Name.Trim(),
            Quantity = string.IsNullOrWhiteSpace(request.Quantity) ? null : request.Quantity.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            IntervalDays = request.IntervalDays,
            // Por omissão a primeira vez é daqui a um intervalo (o artigo normalmente acabou de se pôr na lista).
            NextDate = request.StartDate ?? today.AddDays(request.IntervalDays),
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        db.RecurringItems.Add(item);
        activity.Capture(householdId, userId, HouseholdModule.Shopping,
            $"pôs «{item.Name}» a repetir na lista «{list.Name}»", undoable: false, access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/households/{householdId}/lists/{listId}/recurring/{item.Id}", ToDto(item));
    }

    private static async Task<IResult> DeleteRecurring(
        Guid householdId,
        Guid listId,
        Guid recurringId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Shopping, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var item = await db.RecurringItems
            .SingleOrDefaultAsync(r => r.Id == recurringId && r.ListId == listId && r.HouseholdId == householdId, ct);
        if (item is null)
        {
            return Results.NotFound();
        }

        db.RecurringItems.Remove(item);
        activity.Capture(householdId, userId, HouseholdModule.Shopping, $"deixou de repetir «{item.Name}»",
            undoable: false, access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Guarda (ou esquece, se se desmarcou) a compra de um artigo. Grava à parte e ignora uma corrida com outro pedido
    /// para o mesmo artigo: o registo é só para as sugestões, nunca pode fazer falhar o marcar.
    /// </summary>
    internal static async Task RecordPurchaseAsync(
        KubicoDbContext db, Guid householdId, ShoppingItem item, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            if (!item.IsChecked)
            {
                await db.PurchaseRecords.Where(p => p.ItemId == item.Id).ExecuteDeleteAsync(ct);
                return;
            }

            var existing = await db.PurchaseRecords.SingleOrDefaultAsync(p => p.ItemId == item.Id, ct);
            if (existing is null)
            {
                db.PurchaseRecords.Add(new PurchaseRecord
                {
                    ItemId = item.Id,
                    HouseholdId = householdId,
                    ListId = item.ListId,
                    Name = item.Name,
                    NameKey = ShoppingInsights.Key(item.Name),
                    Category = item.Category,
                    UserId = userId,
                    PurchasedAt = now,
                });
            }
            else
            {
                existing.PurchasedAt = now;
                existing.UserId = userId;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (ApiExceptionHandler.IsUniqueViolation(e))
        {
            db.ChangeTracker.Clear();
        }
    }

    internal static RecurringItemDto ToDto(RecurringItem r) =>
        new(r.Id, r.ListId, r.Name, r.Quantity, r.Category, r.IntervalDays, r.NextDate);
}

/// <summary>Põe na lista os artigos recorrentes cujo dia chegou (chamado de minuto a minuto pelo ReminderWorker).</summary>
public class RecurringItemsService(KubicoDbContext db, HouseholdNotifier notifier, TimeProvider clock)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Um dia de folga cobre os fusos à frente de UTC; cada casa confirma no seu fuso.
        var horizon = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        var due = await db.RecurringItems.AsNoTracking().Where(r => r.NextDate <= horizon).ToListAsync(ct);
        var added = 0;
        foreach (var recurring in due)
        {
            var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == recurring.HouseholdId, ct);
            var today = household.Today(now);
            if (recurring.NextDate > today)
            {
                continue;
            }

            // Só um processo avança a data (e põe o artigo): o outro vê a data já mudada e não faz nada.
            var next = recurring.NextAfter(today);
            var claimed = await db.RecurringItems
                .Where(r => r.Id == recurring.Id && r.NextDate == recurring.NextDate)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextDate, next), ct);
            if (claimed == 0)
            {
                continue;
            }

            // Se já está na lista por comprar, não se repete.
            var key = ShoppingInsights.Key(recurring.Name);
            var pending = await db.ShoppingItems.AsNoTracking()
                .Where(i => i.ListId == recurring.ListId && !i.IsChecked)
                .Select(i => i.Name)
                .ToListAsync(ct);
            if (pending.Any(name => ShoppingInsights.Key(name) == key))
            {
                continue;
            }

            var item = new ShoppingItem
            {
                ListId = recurring.ListId,
                Name = recurring.Name,
                Quantity = recurring.Quantity,
                Category = recurring.Category,
                AddedByUserId = recurring.CreatedByUserId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ShoppingItems.Add(item);
            await db.SaveChangesAsync(ct);
            await notifier.ShoppingItemUpserted(recurring.HouseholdId, ShoppingEndpoints.ToDto(item), ct);
            await ShoppingEndpoints.NotifyCountsAsync(db, notifier, recurring.HouseholdId, recurring.ListId, ct);
            added++;
        }

        return added;
    }
}
