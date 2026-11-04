using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;
using Kubico.Domain.Meals;
using Kubico.Domain.Shopping;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Meals;

public record IngredientDto(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(50)] string? Quantity = null,
    [property: StringLength(50)] string? Category = null);

public record SaveRecipeRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Title,
    [property: Range(1, 50)] int Servings = 4,
    IReadOnlyList<IngredientDto>? Ingredients = null,
    IReadOnlyList<string>? Steps = null,
    IReadOnlyList<string>? Tags = null,
    [property: StringLength(2000)] string? Notes = null);

public record RecipeDto(
    Guid Id,
    Guid HouseholdId,
    string Title,
    int Servings,
    IReadOnlyList<IngredientDto> Ingredients,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Tags,
    string? Notes,
    DateTimeOffset UpdatedAt);

/// <summary>Um dia do plano. Sem <c>RecipeId</c> nem <c>Text</c>, o dia está vazio.</summary>
public record MealPlanEntryDto(DateOnly Date, MealSlot Slot, Guid? RecipeId, string? Text, DateTimeOffset UpdatedAt);

public record SaveMealRequest(Guid? RecipeId = null, [property: StringLength(200)] string? Text = null);

public record MealsToListRequest(DateOnly From, DateOnly To, Guid ListId);

public record MealsToListDto(int Added, IReadOnlyList<string> Names);

/// <summary>
/// Refeições (F4-09): as receitas da casa e o plano da semana (almoço e jantar). Ver é <c>View</c>; planear é
/// <c>Participate</c>; criar, mudar e apagar receitas é <c>Manage</c>. "Pôr na lista" junta os ingredientes do plano
/// à lista de compras (pede também usar as Compras).
/// </summary>
public static class MealEndpoints
{
    public const int MaxRangeDays = 31;
    private const int MaxIngredients = 60;
    private const int MaxSteps = 40;
    private const int MaxTags = 10;

    public static RouteGroupBuilder MapMealEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup("/households/{householdId:guid}")
            .WithTags("Meals")
            .RequireAuthorization();

        household.MapGet("/recipes", GetRecipes);
        household.MapPost("/recipes", CreateRecipe);
        household.MapPut("/recipes/{recipeId:guid}", UpdateRecipe);
        household.MapDelete("/recipes/{recipeId:guid}", DeleteRecipe);
        household.MapGet("/meals", GetPlan);
        household.MapPut("/meals/{date}/{slot}", SaveMeal);
        household.MapPost("/meals/to-list", ToList);
        return api;
    }

    private static async Task<IResult> GetRecipes(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Meals, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var recipes = await db.Recipes.AsNoTracking().Where(r => r.HouseholdId == householdId).ToListAsync(ct);
        return Results.Ok(recipes.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).Select(ToDto));
    }

    private static async Task<IResult> CreateRecipe(
        Guid householdId,
        SaveRecipeRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Meals, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var recipe = new Recipe { HouseholdId = householdId, Title = request.Title.Trim(), CreatedByUserId = userId, CreatedAt = now };
        Apply(recipe, request, now);
        db.Recipes.Add(recipe);
        activity.Capture(householdId, userId, HouseholdModule.Meals, $"criou a receita «{recipe.Title}»", undoable: false);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(recipe);
        await notifier.RecipeUpserted(householdId, dto, ct);
        return Results.Created($"/api/households/{householdId}/recipes/{recipe.Id}", dto);
    }

    private static async Task<IResult> UpdateRecipe(
        Guid householdId,
        Guid recipeId,
        SaveRecipeRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Meals, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var recipe = await db.Recipes.SingleOrDefaultAsync(r => r.Id == recipeId && r.HouseholdId == householdId, ct);
        if (recipe is null)
        {
            return Results.NotFound();
        }

        Apply(recipe, request, clock.GetUtcNow());
        activity.Capture(householdId, userId, HouseholdModule.Meals, $"mudou a receita «{recipe.Title}»", undoable: false);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(recipe);
        await notifier.RecipeUpserted(householdId, dto, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteRecipe(
        Guid householdId,
        Guid recipeId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Meals, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var recipe = await db.Recipes.SingleOrDefaultAsync(r => r.Id == recipeId && r.HouseholdId == householdId, ct);
        if (recipe is null)
        {
            return Results.NotFound();
        }

        // Os dias do plano com esta receita ficam vazios (a chave estrangeira põe-nos a null).
        var planned = await db.MealPlan.Where(m => m.RecipeId == recipeId).ToListAsync(ct);
        db.Recipes.Remove(recipe);
        activity.Capture(householdId, userId, HouseholdModule.Meals, $"apagou a receita «{recipe.Title}»", undoable: false);
        await db.SaveChangesAsync(ct);

        await notifier.RecipeDeleted(householdId, recipeId, ct);
        foreach (var entry in planned)
        {
            await notifier.MealPlanChanged(householdId, ToDto(entry), ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> GetPlan(
        Guid householdId,
        DateOnly? from,
        DateOnly? to,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Meals, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        if (InvalidRange(from, to) is { } invalid)
        {
            return invalid;
        }

        var entries = await db.MealPlan.AsNoTracking()
            .Where(m => m.HouseholdId == householdId && m.Date >= from!.Value && m.Date <= to!.Value)
            .OrderBy(m => m.Date).ThenBy(m => m.Slot)
            .ToListAsync(ct);
        return Results.Ok(entries.Select(ToDto));
    }

    private static async Task<IResult> SaveMeal(
        Guid householdId,
        DateOnly date,
        MealSlot slot,
        SaveMealRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Meals, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        if (!SupportedDates.IsSupported(date) || !Enum.IsDefined(slot))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["date"] = ["Escolhe um dia válido."] });
        }

        if (request.RecipeId is { } recipeId
            && !await db.Recipes.AnyAsync(r => r.Id == recipeId && r.HouseholdId == householdId, ct))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["RecipeId"] = ["Essa receita não existe nesta casa."] });
        }

        var now = clock.GetUtcNow();
        var text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim();
        var entry = await db.MealPlan.SingleOrDefaultAsync(m => m.HouseholdId == householdId && m.Date == date && m.Slot == slot, ct);
        if (request.RecipeId is null && text is null)
        {
            if (entry is not null)
            {
                db.MealPlan.Remove(entry);
            }
        }
        else
        {
            if (entry is null)
            {
                entry = new MealPlanEntry { HouseholdId = householdId, Date = date, Slot = slot };
                db.MealPlan.Add(entry);
            }

            entry.RecipeId = request.RecipeId;
            entry.Text = request.RecipeId is null ? text : null;
            entry.UpdatedByUserId = userId;
            entry.UpdatedAt = now;
        }

        activity.Capture(householdId, userId, HouseholdModule.Meals, $"mudou as refeições de {date:dd/MM}", undoable: false,
            access: ModuleAccess.Participate);
        // Dois pedidos para o mesmo dia ao mesmo tempo: o índice único recusa um (409), nunca fica repetido.
        await db.SaveChangesAsync(ct);

        var dto = entry is null || db.Entry(entry).State == EntityState.Detached
            ? new MealPlanEntryDto(date, slot, null, null, now)
            : ToDto(entry);
        await notifier.MealPlanChanged(householdId, dto, ct);
        return Results.Ok(dto);
    }

    /// <summary>Os ingredientes das receitas planeadas entre dois dias vão para a lista (sem os que já lá estão).</summary>
    private static async Task<IResult> ToList(
        Guid householdId,
        MealsToListRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Meals, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var (_, _, shoppingError) = await access.RequireAsync(userId, householdId, HouseholdModule.Shopping, ModuleAccess.Participate, ct);
        if (shoppingError is not null)
        {
            return shoppingError;
        }

        if (InvalidRange(request.From, request.To) is { } invalid)
        {
            return invalid;
        }

        var list = await db.ShoppingLists.AsNoTracking().SingleOrDefaultAsync(l => l.Id == request.ListId && l.HouseholdId == householdId, ct);
        if (list is null)
        {
            return Results.NotFound();
        }

        var recipeIds = await db.MealPlan.AsNoTracking()
            .Where(m => m.HouseholdId == householdId && m.Date >= request.From && m.Date <= request.To && m.RecipeId != null)
            .Select(m => m.RecipeId!.Value)
            .ToListAsync(ct);
        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var pending = (await db.ShoppingItems.AsNoTracking().Where(i => i.ListId == list.Id && !i.IsChecked).Select(i => i.Name).ToListAsync(ct))
            .Select(ShoppingInsights.Key)
            .ToHashSet();
        var ingredients = MealShopping.Ingredients(recipeIds.Select(id => recipes[id]), pending);
        if (ingredients.Count == 0)
        {
            return Results.Ok(new MealsToListDto(0, []));
        }

        var now = clock.GetUtcNow();
        var items = ingredients.Select(i => new ShoppingItem
        {
            ListId = list.Id,
            Name = i.Name,
            Quantity = i.Quantity is { Length: > 50 } q ? q[..50] : i.Quantity,
            Category = i.Category,
            AddedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();
        db.ShoppingItems.AddRange(items);
        activity.Capture(householdId, userId, HouseholdModule.Shopping,
            $"pôs {items.Count} ingredientes das refeições na lista «{list.Name}»", undoable: false, access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);

        foreach (var item in items)
        {
            await notifier.ShoppingItemUpserted(householdId, ShoppingEndpoints.ToDto(item), ct);
        }

        await ShoppingEndpoints.NotifyCountsAsync(db, notifier, householdId, list.Id, ct);
        return Results.Ok(new MealsToListDto(items.Count, [.. items.Select(i => i.Name)]));
    }

    private static IResult? InvalidRange(DateOnly? from, DateOnly? to) =>
        from is not { } first || to is not { } last || last < first || last.DayNumber - first.DayNumber >= MaxRangeDays
            || !SupportedDates.IsSupported(first) || !SupportedDates.IsSupported(last)
            ? Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["to"] = [$"Indica um intervalo de datas válido, com {MaxRangeDays} dias no máximo."],
            })
            : null;

    private static IResult? Validate(SaveRecipeRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors["Title"] = ["O nome é obrigatório."];
        }

        if ((request.Ingredients?.Count ?? 0) > MaxIngredients || (request.Steps?.Count ?? 0) > MaxSteps
            || (request.Tags?.Count ?? 0) > MaxTags
            || (request.Ingredients ?? []).Any(i => string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > 200
                || i.Quantity?.Length > 50 || i.Category?.Length > 50)
            || (request.Steps ?? []).Any(s => s.Length > 1000) || (request.Tags ?? []).Any(t => t.Length > 40))
        {
            errors["Ingredients"] = ["A receita tem ingredientes, passos ou etiquetas a mais ou demasiado compridos."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static void Apply(Recipe recipe, SaveRecipeRequest request, DateTimeOffset now)
    {
        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        recipe.Title = request.Title.Trim();
        recipe.Servings = request.Servings;
        recipe.Ingredients = [.. (request.Ingredients ?? []).Select(i => new RecipeIngredient
        {
            Name = i.Name.Trim(), Quantity = Clean(i.Quantity), Category = Clean(i.Category),
        })];
        recipe.Steps = [.. (request.Steps ?? []).Select(s => s.Trim()).Where(s => s.Length > 0)];
        recipe.Tags = [.. (request.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        recipe.Notes = Clean(request.Notes);
        recipe.UpdatedAt = now;
    }

    internal static RecipeDto ToDto(Recipe r) => new(
        r.Id, r.HouseholdId, r.Title, r.Servings,
        [.. r.Ingredients.Select(i => new IngredientDto(i.Name, i.Quantity, i.Category))],
        r.Steps, r.Tags, r.Notes, r.UpdatedAt);

    private static MealPlanEntryDto ToDto(MealPlanEntry m) => new(m.Date, m.Slot, m.RecipeId, m.Text, m.UpdatedAt);
}
