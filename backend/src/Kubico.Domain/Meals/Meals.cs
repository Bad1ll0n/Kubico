namespace Kubico.Domain.Meals;

/// <summary>Uma receita da casa (F4-09): ingredientes (que vão para a lista de compras), passos e etiquetas.</summary>
public class Recipe
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public required string Title { get; set; }

    /// <summary>Para quantas pessoas.</summary>
    public int Servings { get; set; } = 4;

    public List<RecipeIngredient> Ingredients { get; set; } = [];
    public List<string> Steps { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string? Notes { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Um ingrediente: o nome (como fica na lista de compras), a quantidade em texto livre e a secção.</summary>
public class RecipeIngredient
{
    public required string Name { get; set; }
    public string? Quantity { get; set; }
    public string? Category { get; set; }
}

public enum MealSlot
{
    Lunch = 0,
    Dinner = 1,
}

/// <summary>O que se come num dia, ao almoço ou ao jantar: uma receita da casa ou só um texto ("restos", "fora").</summary>
public class MealPlanEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public DateOnly Date { get; set; }
    public MealSlot Slot { get; set; }
    public Guid? RecipeId { get; set; }
    public string? Text { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Juntar os ingredientes do plano para a lista de compras.</summary>
public static class MealShopping
{
    /// <summary>
    /// Os ingredientes das receitas planeadas, um por nome (sem repetir "Cebola" de três receitas: as quantidades
    /// juntam-se com "+"). Fora ficam os que já estão na lista por comprar.
    /// </summary>
    public static IReadOnlyList<RecipeIngredient> Ingredients(IEnumerable<Recipe> planned, ISet<string> pendingKeys)
    {
        var merged = new Dictionary<string, RecipeIngredient>();
        var order = new List<string>();
        foreach (var ingredient in planned.SelectMany(r => r.Ingredients))
        {
            var key = Shopping.ShoppingInsights.Key(ingredient.Name);
            if (key.Length == 0 || pendingKeys.Contains(key))
            {
                continue;
            }

            if (merged.TryGetValue(key, out var existing))
            {
                existing.Quantity = (existing.Quantity, ingredient.Quantity) switch
                {
                    (null, var q) => q,
                    (var q, null) => q,
                    var (a, b) => $"{a} + {b}",
                };
                existing.Category ??= ingredient.Category;
            }
            else
            {
                merged[key] = new RecipeIngredient { Name = ingredient.Name.Trim(), Quantity = ingredient.Quantity, Category = ingredient.Category };
                order.Add(key);
            }
        }

        return [.. order.Select(k => merged[k])];
    }
}
