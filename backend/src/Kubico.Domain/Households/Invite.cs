namespace Kubico.Domain.Households;

/// <summary>
/// Convite para entrar num agregado. O código pode ser partilhado por link ou QR code
/// e é válido para várias pessoas até expirar.
/// </summary>
public class Invite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;
    public required string Code { get; set; }
    public HouseholdRole Role { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}
