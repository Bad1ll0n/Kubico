namespace Kubico.Domain.Home;

/// <summary>
/// A ligação da casa ao Home Assistant (F4-16c): o endereço e um token de longa duração (cifrado com o Data
/// Protection). Uma por casa.
/// </summary>
public class SmartHomeConnection
{
    public Guid HouseholdId { get; set; }
    public required string BaseUrl { get; set; }
    public required string ProtectedToken { get; set; }
    public Guid ConnectedByUserId { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>O último erro ao falar com o Home Assistant (null quando correu bem), pronto a mostrar.</summary>
    public string? LastError { get; set; }
}

/// <summary>
/// Uma regra: quando a entidade do Home Assistant passa para o estado escolhido (a máquina de lavar acabou), cria-se
/// uma tarefa ("estender a roupa"). <see cref="LastState"/> guarda o último estado visto, para só contar mudanças.
/// </summary>
public class SmartHomeRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public required string EntityId { get; set; }
    public string? EntityName { get; set; }
    public required string ToState { get; set; }
    public required string ChoreTitle { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public int Points { get; set; }
    public bool Enabled { get; set; } = true;
    public string? LastState { get; set; }
    public DateTimeOffset? LastTriggeredAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Uma regra não dispara outra vez antes disto (um sensor que oscila não enche a lista de tarefas).</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);
}
