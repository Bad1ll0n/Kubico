namespace Kubico.Domain.Households;

/// <summary>
/// Um membro está fora (férias, doença, casa do outro progenitor) entre dois dias, inclusive. Enquanto está fora,
/// as rotações das tarefas saltam-no e as tarefas que eram suas passam a quem vem a seguir.
/// </summary>
public class MemberAbsence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid UserId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Reason { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool Covers(DateOnly date) => date >= StartDate && date <= EndDate;

    /// <summary>"Está fora neste dia?", a partir das ausências de uma casa.</summary>
    public static Func<Guid, DateOnly, bool> Checker(IEnumerable<MemberAbsence> absences)
    {
        var byUser = absences.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.ToList());
        return (userId, date) => byUser.TryGetValue(userId, out var list) && list.Any(a => a.Covers(date));
    }
}
