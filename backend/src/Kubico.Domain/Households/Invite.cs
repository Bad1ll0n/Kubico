namespace Kubico.Domain.Households;

/// <summary>
/// Convite para entrar num agregado. O código pode ser partilhado por link ou QR code. Serve até expirar, ser anulado
/// ou chegar ao número de utilizações (<see cref="MaxUses"/>; por omissão uma só nos convites de adulto).
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

    /// <summary>Quantas pessoas podem entrar com ele; null = sem limite (até expirar).</summary>
    public int? MaxUses { get; set; }

    /// <summary>Quantas já entraram com ele.</summary>
    public int UsedCount { get; set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsUsedUp => MaxUses is { } max && UsedCount >= max;

    /// <summary>Ainda serve para entrar.</summary>
    public bool IsValid(DateTimeOffset now) => !IsExpired(now) && !IsUsedUp;
}
