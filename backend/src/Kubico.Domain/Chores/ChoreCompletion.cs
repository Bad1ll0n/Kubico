namespace Kubico.Domain.Chores;

/// <summary>Histórico de quem fez o quê. Alimenta os pontos e as estatísticas de distribuição.</summary>
public class ChoreCompletion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChoreId { get; set; }
    public Chore Chore { get; set; } = null!;
    public Guid UserId { get; set; }

    /// <summary>Pontos no momento da conclusão (mudar os pontos da tarefa não reescreve o passado).</summary>
    public int Points { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>A foto de prova (quando a tarefa a pede). Fica vazia quando a foto é apagada por ser antiga.</summary>
    public Guid? PhotoId { get; set; }

    /// <summary>Quem gere as tarefas não aceitou: os pontos deixam de contar.</summary>
    public DateTimeOffset? RejectedAt { get; set; }
    public Guid? RejectedByUserId { get; set; }
}
