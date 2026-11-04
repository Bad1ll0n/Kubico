using Kubico.Api.Common;
using Kubico.Domain.Meals;

namespace Kubico.Api.Meals;

/// <summary>
/// As receitas portuguesas de base que uma casa nova recebe com um modelo (F4-09): ingredientes com quantidade e a
/// secção do supermercado (as mesmas da lista de compras), para "pôr os ingredientes na lista" funcionar logo.
/// </summary>
public static class BaseRecipes
{
    private const string Fresh = "Frutas e legumes";
    private const string Meat = "Carne e peixe";
    private const string Dairy = "Laticínios";
    private const string Pantry = "Mercearia";
    private const string Bakery = "Padaria";

    private static readonly (string Title, string[] Tags, (string Name, string Quantity, string Category)[] Ingredients)[] All =
    [
        ("Bacalhau à Brás", ["Peixe"], [
            ("Bacalhau desfiado", "400 g", Meat), ("Batata palha", "1 pacote", Pantry), ("Ovos", "6", Dairy),
            ("Cebola", "2", Fresh), ("Azeitonas", "1 frasco", Pantry), ("Salsa", "1 molho", Fresh),
        ]),
        ("Arroz de pato", ["Carne"], [
            ("Pato", "1", Meat), ("Chouriço", "1", Meat), ("Arroz", "400 g", Pantry), ("Cebola", "1", Fresh),
            ("Laranja", "1", Fresh),
        ]),
        ("Caldo verde", ["Sopa"], [
            ("Couve galega", "300 g", Fresh), ("Batata", "1 kg", Fresh), ("Cebola", "1", Fresh), ("Alho", "2 dentes", Fresh),
            ("Chouriço", "1", Meat),
        ]),
        ("Frango assado com batatas", ["Carne"], [
            ("Frango", "1", Meat), ("Batata", "1 kg", Fresh), ("Alho", "4 dentes", Fresh), ("Limão", "1", Fresh),
        ]),
        ("Esparguete à bolonhesa", ["Massa"], [
            ("Esparguete", "500 g", Pantry), ("Carne picada", "500 g", Meat), ("Polpa de tomate", "1 lata", Pantry),
            ("Cebola", "1", Fresh), ("Queijo ralado", "1 pacote", Dairy),
        ]),
        ("Sopa de legumes", ["Sopa"], [
            ("Batata", "3", Fresh), ("Cenoura", "3", Fresh), ("Abóbora", "300 g", Fresh), ("Cebola", "1", Fresh),
            ("Courgette", "1", Fresh),
        ]),
        ("Douradas grelhadas com salada", ["Peixe"], [
            ("Douradas", "4", Meat), ("Alface", "1", Fresh), ("Tomate", "3", Fresh), ("Batata", "1 kg", Fresh),
        ]),
        ("Feijoada", ["Carne"], [
            ("Feijão encarnado", "2 latas", Pantry), ("Entrecosto", "500 g", Meat), ("Chouriço", "1", Meat),
            ("Cenoura", "2", Fresh), ("Arroz", "300 g", Pantry),
        ]),
        ("Omelete com salada", ["Rápido", "Vegetariano"], [
            ("Ovos", "6", Dairy), ("Queijo", "100 g", Dairy), ("Alface", "1", Fresh), ("Tomate", "2", Fresh),
        ]),
        ("Tostas mistas", ["Rápido"], [
            ("Pão de forma", "1", Bakery), ("Fiambre", "200 g", Meat), ("Queijo", "200 g", Dairy), ("Manteiga", "1", Dairy),
        ]),
    ];

    /// <summary>As receitas na língua de quem cria a casa.</summary>
    public static IEnumerable<Recipe> For(Guid householdId, Guid userId, DateTimeOffset now, string language)
    {
        string T(string text) => Texts.Translate(text, language);
        return All.Select(r => new Recipe
        {
            HouseholdId = householdId,
            Title = T(r.Title),
            Tags = [.. r.Tags.Select(T)],
            Ingredients = [.. r.Ingredients.Select(i => new RecipeIngredient { Name = T(i.Name), Quantity = T(i.Quantity), Category = i.Category })],
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }
}
