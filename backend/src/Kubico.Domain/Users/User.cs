namespace Kubico.Domain.Users;

/// <summary>
/// Uma pessoa que usa a app. Tem conta própria (email e palavra-passe) ou é um perfil de criança: criado por um
/// adulto numa casa, sem email, entra com o código da família e um PIN.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null nos perfis de criança.</summary>
    public string? Email { get; set; }

    public required string DisplayName { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Num perfil de criança, a casa a que pertence (e que o gere). Null nas contas próprias.</summary>
    public Guid? ManagedByHouseholdId { get; set; }

    /// <summary>O PIN de um perfil de criança, guardado como hash (como as palavras-passe).</summary>
    public string? PinHash { get; set; }

    public DateOnly? BirthDate { get; set; }

    /// <summary>PINs errados seguidos; ao fim de <see cref="MaxPinAttempts"/>, o perfil fica bloqueado uns minutos.</summary>
    public int FailedPinAttempts { get; set; }

    public DateTimeOffset? PinLockedUntil { get; set; }

    public List<Households.Membership> Memberships { get; set; } = [];

    public const int MaxPinAttempts = 5;

    public static readonly TimeSpan PinLockout = TimeSpan.FromMinutes(15);

    public bool IsChildProfile => ManagedByHouseholdId is not null;

    public bool IsPinLocked(DateTimeOffset now) => PinLockedUntil is { } until && now < until;

    /// <summary>Conta um PIN errado; ao chegar ao limite, bloqueia o perfil e volta a contar do zero.</summary>
    public void RegisterWrongPin(DateTimeOffset now)
    {
        FailedPinAttempts++;
        if (FailedPinAttempts >= MaxPinAttempts)
        {
            PinLockedUntil = now + PinLockout;
            FailedPinAttempts = 0;
        }
    }

    public void RegisterRightPin()
    {
        FailedPinAttempts = 0;
        PinLockedUntil = null;
    }
}
