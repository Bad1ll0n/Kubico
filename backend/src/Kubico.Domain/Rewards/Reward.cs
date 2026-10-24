using Kubico.Domain.Households;

namespace Kubico.Domain.Rewards;

/// <summary>
/// Uma recompensa que se troca por pontos das tarefas ("Escolher o filme de sexta", "30 minutos de consola").
/// Os adultos criam-nas; qualquer membro as pode pedir.
/// </summary>
public class Reward
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;

    public required string Title { get; set; }
    public string? Description { get; set; }

    /// <summary>Quantos pontos custa.</summary>
    public int Cost { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum RedemptionStatus
{
    /// <summary>À espera de um adulto. Os pontos ficam reservados.</summary>
    Pending = 0,
    Approved = 1,

    /// <summary>Recusado: os pontos voltam.</summary>
    Rejected = 2,
}

/// <summary>
/// Um pedido de troca de pontos por uma recompensa. Guarda o nome e o custo do momento, para o histórico não
/// mudar se a recompensa for alterada ou apagada.
/// </summary>
public class RewardRedemption : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Muda em cada gravação: aprovar e recusar (ou cancelar) ao mesmo tempo não passam os dois.</summary>
    public Guid Version { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid? RewardId { get; set; }
    public Guid UserId { get; set; }

    public required string RewardTitle { get; set; }
    public int Points { get; set; }

    public RedemptionStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Pontos que este pedido tira ao saldo: os pendentes (reservados) e os aprovados.</summary>
    public bool CountsAgainstBalance => Status is RedemptionStatus.Pending or RedemptionStatus.Approved;

    public void Decide(bool approve, Guid userId, DateTimeOffset now)
    {
        if (Status != RedemptionStatus.Pending)
        {
            throw new InvalidOperationException("Este pedido já foi decidido.");
        }

        Status = approve ? RedemptionStatus.Approved : RedemptionStatus.Rejected;
        DecidedByUserId = userId;
        DecidedAt = now;
        UpdatedAt = now;
    }
}
