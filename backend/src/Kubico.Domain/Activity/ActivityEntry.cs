using Kubico.Domain.Households;

namespace Kubico.Domain.Activity;

/// <summary>
/// Uma alteração numa casa: quem fez o quê e quando. Quando se pode desfazer, <see cref="UndoData"/> guarda os
/// valores de antes (em JSON) das coisas que mudaram, e <see cref="EntityKeys"/> diz quais são ("ShoppingItem:id").
/// </summary>
public class ActivityEntry
{
    /// <summary>Número sequencial: diz também a ordem (o relógio pode repetir-se).</summary>
    public long Id { get; set; }

    public Guid HouseholdId { get; set; }

    /// <summary>Quem fez a alteração.</summary>
    public Guid UserId { get; set; }

    /// <summary>A área (compras, tarefas…); null nas alterações à casa e aos membros.</summary>
    public HouseholdModule? Module { get; set; }

    /// <summary>O que aconteceu, para mostrar depois do nome: "adicionou «Leite» (Supermercado)".</summary>
    public required string Summary { get; set; }

    /// <summary>Os valores de antes e o que foi criado, para desfazer. Null quando não se pode desfazer.</summary>
    public string? UndoData { get; set; }

    public List<string> EntityKeys { get; set; } = [];

    /// <summary>Esta entrada é o desfazer daquela (não conta como alteração nova ao que se repôs).</summary>
    public long? UndoOfId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
    public Guid? UndoneByUserId { get; set; }

    /// <summary>O histórico guarda este tempo (as versões pagas poderão guardar mais).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    public bool CanBeUndone => UndoData is not null && UndoneAt is null;
}
