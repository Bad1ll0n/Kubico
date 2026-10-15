namespace Kubico.Domain.Users;

/// <summary>
/// Sessão de um dispositivo. O token em si nunca é guardado, só o hash. Cada utilização troca-o por um novo da
/// mesma família; se um token já trocado voltar a aparecer (sinal de roubo), a família inteira é revogada.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 do token, em hexadecimal.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Todos os tokens que nasceram do mesmo início de sessão (o mesmo dispositivo).</summary>
    public Guid FamilyId { get; set; }

    /// <summary>De onde veio o início de sessão (o User-Agent), para uma futura lista de dispositivos.</summary>
    public string? Device { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Quando foi trocado por outro ou revogado (terminar sessão). Um token revogado não serve.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}
