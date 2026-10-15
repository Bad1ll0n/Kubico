using Kubico.Domain.Households;

namespace Kubico.Domain.Shopping;

public class ShoppingList
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<ShoppingItem> Items { get; set; } = [];
}
