namespace Kubico.Domain.Households;

/// <summary>
/// Um agregado (casa/família). Tudo o que é partilhado pertence a um agregado:
/// é a fronteira de multi-tenancy e de permissões.
/// </summary>
public class Household
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<Membership> Members { get; set; } = [];
    public List<Invite> Invites { get; set; } = [];
}
