namespace Kubico.Domain.Assistant;

/// <summary>
/// Quantos pedidos ao assistente fez a casa num dia (no fuso da casa). Há um limite por dia
/// (<c>Assistant:DailyLimit</c>); cada pedido reserva-se antes com um <c>ExecuteUpdateAsync</c> condicional.
/// </summary>
public class AssistantUsage
{
    public Guid HouseholdId { get; set; }
    public DateOnly Day { get; set; }
    public int Count { get; set; }
}
