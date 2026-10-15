using Kubico.Domain.Users;

namespace Kubico.Domain.Households;

public class Membership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public HouseholdRole Role { get; set; }

    /// <summary>Cor do membro em hexadecimal (#RRGGBB), usada no calendário, tarefas, etc.</summary>
    public string Color { get; set; } = "#4F7CAC";
    public DateTimeOffset JoinedAt { get; set; }
}
