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

    /// <summary>Em que altura do dia se faz: as rotinas da manhã e da noite aparecem nessa ordem no dia.</summary>
    public ChoreTimeOfDay TimeOfDay { get; set; }

    /// <summary>Os passos de uma rotina ("Lavar os dentes", "Fazer a cama"). Vazia = tarefa simples.</summary>
    public List<string> Steps { get; set; } = [];

    /// <summary>Os passos já feitos desta vez (posições em <see cref="Steps"/>). Recomeça quando a tarefa é concluída.</summary>
    public List<int> DoneSteps { get; set; } = [];

    /// <summary>Uma rotina com todos os passos feitos.</summary>
    public bool AllStepsDone => Steps.Count > 0 && Enumerable.Range(0, Steps.Count).All(DoneSteps.Contains);

    /// <summary>Para concluir é preciso uma foto do trabalho feito.</summary>
    public bool RequiresPhoto { get; set; }

    /// <summary>Preenchido quando uma tarefa não recorrente é concluída.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ChoreCompletion> Completions { get; set; } = [];

    public bool IsDone => CompletedAt is not null;

    /// <summary>
    /// Uma conclusão não foi aceite: a tarefa volta a estar por fazer, hoje, por quem a fez (numa tarefa que se
    /// repete, a rotação continua a partir dessa pessoa).
    /// </summary>
    public void Reopen(Guid userId, DateOnly today, DateTimeOffset now)
    {
        if (Recurrence == ChoreRecurrence.None)
        {
            CompletedAt = null;
            DueDate ??= today;
        }
        else
        {
            DueDate = today;
        }

        AssigneeUserId = userId;
        DoneSteps = [];
        UpdatedAt = now;
    }

    /// <summary>Marca ou desmarca um passo da rotina. Devolve false se o passo não existe.</summary>
    public bool SetStep(int index, bool done, DateTimeOffset now)
    {
        if (index < 0 || index >= Steps.Count)
        {
            return false;
        }

        DoneSteps = done
            ? [.. DoneSteps.Append(index).Distinct().Order()]
            : [.. DoneSteps.Where(i => i != index)];
        UpdatedAt = now;
        return true;
    }

    /// <summary>Muda os passos; se mudaram, os que estavam feitos deixam de contar.</summary>
    public void SetSteps(IEnumerable<string> steps)
    {
        var list = steps.ToList();
        if (!list.SequenceEqual(Steps))
        {
            Steps = list;
            DoneSteps = [];
        }
    }

    /// <summary>
    /// Regista a conclusão: tarefas únicas ficam feitas; recorrentes avançam o prazo
    /// e, se houver rotação, passam ao membro seguinte.
    /// </summary>
    public ChoreCompletion Complete(
        Guid userId, DateTimeOffset now, DateOnly today, Func<Guid, DateOnly, bool>? isAway = null)
    {
        var completion = new ChoreCompletion
        {
            ChoreId = Id,
            UserId = userId,
            Points = Points,
            CompletedAt = now,
        };
        Completions.Add(completion);

        // Da próxima vez, a rotina começa outra vez do primeiro passo.
        DoneSteps = [];

        if (Recurrence == ChoreRecurrence.None)
        {
            CompletedAt = now;
        }
        else
        {
            DueDate = NextDueDate(DueDate ?? today, Recurrence, today);
            if (RotationUserIds.Count > 0)
            {
                // Quem está fora no próximo prazo é saltado. Se os outros estiverem todos fora, fica com quem a fez
                // (ou, se também estiver fora, segue a ordem normal).
                var current = AssigneeUserId ?? userId;
                var due = DueDate.Value;
                AssigneeUserId = isAway is null
                    ? NextInRotation(current)
                    : NextAvailable(current, due, isAway) ?? (isAway(current, due) ? NextInRotation(current) : current);
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

    /// <summary>
    /// Se o responsável está fora no dia da tarefa, ela passa a quem vem a seguir na rotação e está cá. Sem rotação
    /// (ou com toda a gente fora), fica como está. Devolve true se a tarefa mudou.
    /// </summary>
    public bool SkipAway(Func<Guid, DateOnly, bool> isAway, DateOnly today, DateTimeOffset now)
    {
        if (IsDone || AssigneeUserId is not { } assignee || RotationUserIds.Count < 2)
        {
            return false;
        }

        var day = DueDate ?? today;
        if (!isAway(assignee, day) || NextAvailable(assignee, day, isAway) is not { } next)
        {
            return false;
        }

        AssigneeUserId = next;
        UpdatedAt = now;
        return true;
    }

    /// <summary>O próximo da rotação, a seguir a <paramref name="from"/>, que não está fora nesse dia.</summary>
    private Guid? NextAvailable(Guid from, DateOnly day, Func<Guid, DateOnly, bool> isAway)
    {
        var index = RotationUserIds.IndexOf(from);
        var count = RotationUserIds.Count;
        for (var step = 1; step <= count; step++)
        {
            var candidate = RotationUserIds[((index + step) % count + count) % count];
            if (candidate != from && !isAway(candidate, day))
            {
                return candidate;
            }
        }

        return null;
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

public enum ChoreTimeOfDay
{
    Any = 0,
    Morning = 1,
    Evening = 2,
}

public enum ChoreRecurrence
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
}
