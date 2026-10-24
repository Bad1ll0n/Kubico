namespace Kubico.Domain.Users;

/// <summary>
/// Uma pessoa que usa a app. Tem conta própria (email e palavra-passe) ou é um perfil de criança: criado por um
/// adulto numa casa, sem email, entra com o código da família e um PIN.
/// </summary>
public class User : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Muda em cada gravação: o mesmo código da 2FA (ou de recuperação) não serve em dois pedidos ao mesmo tempo.</summary>
    public Guid Version { get; set; }

    /// <summary>Null nos perfis de criança.</summary>
    public string? Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>A língua da app ("pt" ou "en"): os avisos chegam nela.</summary>
    public string Language { get; set; } = DefaultLanguage;

    public const string DefaultLanguage = "pt";
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

    /// <summary>Verificação em dois passos: o segredo das apps de autenticação (cifrado). Null quando está desligada.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>O segredo mostrado ao ligar a verificação, enquanto a pessoa não a confirma com um código.</summary>
    public string? PendingTotpSecret { get; set; }

    /// <summary>O último intervalo de 30 segundos usado: o mesmo código não serve duas vezes.</summary>
    public long? LastTotpStep { get; set; }

    /// <summary>Os códigos de recuperação ainda por usar (SHA-256), para quem perder o telemóvel.</summary>
    public List<string> RecoveryCodeHashes { get; set; } = [];

    /// <summary>Códigos errados seguidos na verificação em dois passos (bloqueia como os PINs).</summary>
    public int FailedTwoFactorAttempts { get; set; }

    public DateTimeOffset? TwoFactorLockedUntil { get; set; }

    public List<Households.Membership> Memberships { get; set; } = [];

    public bool TwoFactorEnabled => TotpSecret is not null;

    public bool IsTwoFactorLocked(DateTimeOffset now) => TwoFactorLockedUntil is { } until && now < until;

    /// <summary>Conta um código errado; ao chegar ao limite, bloqueia a entrada uns minutos.</summary>
    public void RegisterWrongTwoFactor(DateTimeOffset now)
    {
        FailedTwoFactorAttempts++;
        if (FailedTwoFactorAttempts >= MaxPinAttempts)
        {
            TwoFactorLockedUntil = now + PinLockout;
            FailedTwoFactorAttempts = 0;
        }
    }

    public void RegisterRightTwoFactor()
    {
        FailedTwoFactorAttempts = 0;
        TwoFactorLockedUntil = null;
    }

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
