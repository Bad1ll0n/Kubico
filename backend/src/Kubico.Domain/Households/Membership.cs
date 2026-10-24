using Kubico.Domain.Users;

namespace Kubico.Domain.Households;

public class Membership : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Muda em cada gravação. Serve também de cadeado do saldo de pontos deste membro nesta casa: quem gasta ou tira
    /// pontos (pedir uma recompensa, desfazer ou não aceitar uma tarefa) grava-a, e dois pedidos em simultâneo não
    /// passam os dois.
    /// </summary>
    public Guid Version { get; set; }
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public HouseholdRole Role { get; set; }

    /// <summary>Cor do membro em hexadecimal (#RRGGBB), usada no calendário, tarefas, etc.</summary>
    public string Color { get; set; } = "#4F7CAC";
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>
    /// Segredo do endereço pessoal para subscrever o calendário da casa (Google, Outlook, Apple). Null = sem endereço.
    /// </summary>
    public string? CalendarFeedToken { get; set; }
}
