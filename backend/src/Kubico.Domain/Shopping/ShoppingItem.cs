namespace Kubico.Domain.Shopping;

public class ShoppingItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ListId { get; set; }
    public ShoppingList List { get; set; } = null!;
    public required string Name { get; set; }

    /// <summary>Texto livre ("2", "1 kg", "um pacote"), como as pessoas escrevem nas listas.</summary>
    public string? Quantity { get; set; }
    public string? Category { get; set; }

    /// <summary>O código de barras, quando o artigo foi lido com a câmara.</summary>
    public string? Barcode { get; set; }

    public bool IsChecked { get; set; }
    public Guid? CheckedByUserId { get; set; }
    public Guid AddedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
