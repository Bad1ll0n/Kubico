using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Domain.Shopping;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Shopping;

public record CreateShoppingListRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name);

/// <summary>
/// Novo artigo. A app pode mandar o <c>Id</c> (gerado no telemóvel): assim, reenviar o mesmo pedido depois de
/// estar sem rede não duplica o artigo.
/// </summary>
public record AddShoppingItemRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(50)] string? Quantity = null,
    [property: StringLength(50)] string? Category = null,
    Guid? Id = null);

/// <summary>Atualização parcial: só os campos enviados (não nulos) são alterados.</summary>
public record UpdateShoppingItemRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Name = null,
    [property: StringLength(50)] string? Quantity = null,
    [property: StringLength(50)] string? Category = null,
    bool? IsChecked = null);

public record ShoppingListDto(Guid Id, Guid HouseholdId, string Name, int ItemCount, int CheckedCount);

public record ShoppingItemDto(
    Guid Id,
    Guid ListId,
    string Name,
    string? Quantity,
    string? Category,
    bool IsChecked,
    Guid AddedByUserId,
    Guid? CheckedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class ShoppingEndpoints
{
    public static RouteGroupBuilder MapShoppingEndpoints(this RouteGroupBuilder api)
    {
        var lists = api.MapGroup("/households/{householdId:guid}/lists")
            .WithTags("Shopping")
            .RequireAuthorization();

        lists.MapGet("/", GetLists);
        lists.MapPost("/", CreateList);
        lists.MapDelete("/{listId:guid}", DeleteList);

        lists.MapGet("/{listId:guid}/items", GetItems);
        lists.MapPost("/{listId:guid}/items", AddItem);
        lists.MapPatch("/{listId:guid}/items/{itemId:guid}", UpdateItem);
        lists.MapDelete("/{listId:guid}/items/{itemId:guid}", DeleteItem);
        lists.MapPost("/{listId:guid}/items/clear-checked", ClearChecked);

        return api;
    }

    private static async Task<IResult> GetLists(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Shopping, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var lists = await db.ShoppingLists.AsNoTracking()
            .Where(l => l.HouseholdId == householdId)
            .OrderBy(l => l.Name)
            .Select(l => new ShoppingListDto(
                l.Id, l.HouseholdId, l.Name, l.Items.Count, l.Items.Count(i => i.IsChecked)))
            .ToListAsync(ct);

        return Results.Ok(lists);
    }

    private static async Task<IResult> CreateList(
        Guid householdId,
        CreateShoppingListRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Shopping, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var list = new ShoppingList
        {
            HouseholdId = householdId,
            Name = request.Name.Trim(),
            CreatedAt = clock.GetUtcNow(),
        };
        db.ShoppingLists.Add(list);
        await db.SaveChangesAsync(ct);

        var dto = new ShoppingListDto(list.Id, householdId, list.Name, 0, 0);
        await notifier.ShoppingListUpserted(householdId, dto, ct);

        return Results.Created($"/api/households/{householdId}/lists/{list.Id}", dto);
    }

    private static async Task<IResult> DeleteList(
        Guid householdId,
        Guid listId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Shopping, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var list = await db.ShoppingLists.SingleOrDefaultAsync(l => l.Id == listId && l.HouseholdId == householdId, ct);
        if (list is null)
        {
            return Results.NotFound();
        }

        db.ShoppingLists.Remove(list);
        await db.SaveChangesAsync(ct);
        await notifier.ShoppingListDeleted(householdId, listId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> GetItems(
        Guid householdId, Guid listId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Shopping, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await ListBelongsTo(db, listId, householdId, ct))
        {
            return Results.NotFound();
        }

        var items = await db.ShoppingItems.AsNoTracking()
            .Where(i => i.ListId == listId)
            .ToListAsync(ct);

        // Por comprar primeiro, agrupado por categoria; os comprados vão para o fim.
        var ordered = items
            .OrderBy(i => i.IsChecked)
            .ThenBy(i => i.Category ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.CreatedAt)
            .Select(ToDto)
            .ToList();

        return Results.Ok(ordered);
    }

    private static async Task<IResult> AddItem(
        Guid householdId,
        Guid listId,
        AddShoppingItemRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Shopping, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await ListBelongsTo(db, listId, householdId, ct))
        {
            return Results.NotFound();
        }

        if (request.Id is { } id && await db.ShoppingItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct) is { } existing)
        {
            // Pedido repetido (a app voltou a enviá-lo): devolve o artigo que já existe.
            return existing.ListId == listId
                ? Results.Ok(ToDto(existing))
                : Results.Problem(title: "Este artigo já existe noutra lista.", statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetUtcNow();
        var item = new ShoppingItem
        {
            Id = request.Id ?? Guid.NewGuid(),
            ListId = listId,
            Name = request.Name.Trim(),
            Quantity = Clean(request.Quantity),
            Category = Clean(request.Category),
            AddedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ShoppingItems.Add(item);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(item);
        await notifier.ShoppingItemUpserted(householdId, dto, ct);

        return Results.Created($"/api/households/{householdId}/lists/{listId}/items/{item.Id}", dto);
    }

    private static async Task<IResult> UpdateItem(
        Guid householdId,
        Guid listId,
        Guid itemId,
        UpdateShoppingItemRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Shopping, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var item = await FindItem(db, householdId, listId, itemId, ct);
        if (item is null)
        {
            return Results.NotFound();
        }

        if (request.Name is not null)
        {
            item.Name = request.Name.Trim();
        }

        if (request.Quantity is not null)
        {
            item.Quantity = Clean(request.Quantity);
        }

        if (request.Category is not null)
        {
            item.Category = Clean(request.Category);
        }

        if (request.IsChecked is { } isChecked && isChecked != item.IsChecked)
        {
            item.IsChecked = isChecked;
            item.CheckedByUserId = isChecked ? userId : null;
        }

        item.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var dto = ToDto(item);
        await notifier.ShoppingItemUpserted(householdId, dto, ct);

        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteItem(
        Guid householdId,
        Guid listId,
        Guid itemId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Shopping, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var item = await FindItem(db, householdId, listId, itemId, ct);
        if (item is null)
        {
            return Results.NotFound();
        }

        db.ShoppingItems.Remove(item);
        await db.SaveChangesAsync(ct);
        await notifier.ShoppingItemDeleted(householdId, listId, itemId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ClearChecked(
        Guid householdId,
        Guid listId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Shopping, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await ListBelongsTo(db, listId, householdId, ct))
        {
            return Results.NotFound();
        }

        var checkedItems = await db.ShoppingItems.Where(i => i.ListId == listId && i.IsChecked).ToListAsync(ct);
        db.ShoppingItems.RemoveRange(checkedItems);
        await db.SaveChangesAsync(ct);

        foreach (var item in checkedItems)
        {
            await notifier.ShoppingItemDeleted(householdId, listId, item.Id, ct);
        }

        return Results.Ok(new { Removed = checkedItems.Count });
    }

    private static Task<bool> ListBelongsTo(KubicoDbContext db, Guid listId, Guid householdId, CancellationToken ct) =>
        db.ShoppingLists.AnyAsync(l => l.Id == listId && l.HouseholdId == householdId, ct);

    private static Task<ShoppingItem?> FindItem(
        KubicoDbContext db, Guid householdId, Guid listId, Guid itemId, CancellationToken ct) =>
        db.ShoppingItems.SingleOrDefaultAsync(
            i => i.Id == itemId && i.ListId == listId && i.List.HouseholdId == householdId, ct);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ShoppingItemDto ToDto(ShoppingItem i) => new(
        i.Id, i.ListId, i.Name, i.Quantity, i.Category, i.IsChecked,
        i.AddedByUserId, i.CheckedByUserId, i.CreatedAt, i.UpdatedAt);
}
