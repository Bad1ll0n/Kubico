using Kubico.Domain.Households;

namespace Kubico.Domain.Chores;

/// <summary>
/// Uma tarefa da casa ("Tirar o lixo", "Aspirar a sala"). Pode repetir-se e rodar
/// automaticamente entre membros sempre que é concluída.
/// </summary>
public class Chore
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;

    public required string Title { get; set; }
    public string? Notes { get; set; }

    /// <summary>Pontos ganhos por quem a conclui (gamificação e recompensas).</summary>
    public int Points { get; set; }

    public DateOnly? DueDate { get; set; }
    public ChoreRecurrence Recurrence { get; set; }

    /// <summary>Responsável atual; null significa "qualquer pessoa".</summary>
    public Guid? AssigneeUserId { get; set; }

    /// <summary>
    /// Ordem da rotação. Quando a tarefa é concluída, passa para o membro seguinte desta lista.
    /// Vazia = sem rotação.
    /// </summary>
    public List<Guid> RotationUserIds { get; set; } = [];

    /// <summary>Preenchido quando uma tarefa não recorrente é concluída.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ChoreCompletion> Completions { get; set; } = [];

    public bool IsDone => CompletedAt is not null;

    /// <summary>
    /// Regista a conclusão: tarefas únicas ficam feitas; recorrentes avançam o prazo
    /// e, se houver rotação, passam ao membro seguinte.
    /// </summary>
    public ChoreCompletion Complete(Guid userId, DateTimeOffset now, DateOnly today)
    {
        var completion = new ChoreCompletion
        {
            ChoreId = Id,
            UserId = userId,
            Points = Points,
            CompletedAt = now,
        };
        Completions.Add(completion);

        if (Recurrence == ChoreRecurrence.None)
        {
            CompletedAt = now;
        }
        else
        {
            DueDate = NextDueDate(DueDate ?? today, Recurrence, today);
            if (RotationUserIds.Count > 0)
            {
                AssigneeUserId = NextInRotation(AssigneeUserId ?? userId);
            }
        }

        UpdatedAt = now;
        return completion;
    }

    /// <summary>
    /// Alguém saiu da casa: sai da rotação e, se a tarefa era sua, passa a quem vinha a seguir (ou a qualquer
    /// pessoa, se não houver rotação). Devolve true se a tarefa mudou.
    /// </summary>
    public bool RemoveMember(Guid userId, DateTimeOffset now)
    {
        var index = RotationUserIds.IndexOf(userId);
        var wasAssignee = AssigneeUserId == userId;
        if (index < 0 && !wasAssignee)
        {
            return false;
        }

        if (index >= 0)
        {
            RotationUserIds = [.. RotationUserIds.Where(id => id != userId)];
        }

        if (wasAssignee)
        {
            // Quem vinha a seguir ocupa agora a posição de quem saiu.
            AssigneeUserId = RotationUserIds.Count == 0 ? null : RotationUserIds[Math.Max(index, 0) % RotationUserIds.Count];
        }

        UpdatedAt = now;
        return true;
    }

    /// <summary>Próximo prazo depois de hoje, saltando os períodos que já passaram.</summary>
    public static DateOnly NextDueDate(DateOnly from, ChoreRecurrence recurrence, DateOnly today)
    {
        var next = Advance(from, recurrence);
        while (next <= today)
        {
            next = Advance(next, recurrence);
        }

        return next;
    }

    private static DateOnly Advance(DateOnly date, ChoreRecurrence recurrence) => recurrence switch
    {
        ChoreRecurrence.Daily => date.AddDays(1),
        ChoreRecurrence.Weekly => date.AddDays(7),
        ChoreRecurrence.Monthly => date.AddMonths(1),
        _ => throw new InvalidOperationException("Tarefa sem recorrência não tem próximo prazo."),
    };

    private Guid NextInRotation(Guid current)
    {
        var index = RotationUserIds.IndexOf(current);
        return RotationUserIds[(index + 1) % RotationUserIds.Count];
    }
}

public enum ChoreRecurrence
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
}
