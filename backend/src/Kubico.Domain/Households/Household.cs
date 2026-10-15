namespace Kubico.Domain.Households;

/// <summary>
/// Um agregado (casa/família). Tudo o que é partilhado pertence a um agregado:
/// é a fronteira de multi-tenancy e de permissões.
/// </summary>
public class Household
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }

    /// <summary>Fuso horário IANA da casa. Define o que é "hoje" para prazos e tarefas.</summary>
    public string TimeZoneId { get; set; } = DefaultTimeZoneId;

    public DateTimeOffset CreatedAt { get; set; }

    public const string DefaultTimeZoneId = "Europe/Lisbon";

    public DateOnly Today(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }

    public List<Membership> Members { get; set; } = [];
    public List<Invite> Invites { get; set; } = [];
}
