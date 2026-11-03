namespace Kubico.Domain.Households;

/// <summary>
/// Um agregado (casa/família). Tudo o que é partilhado pertence a um agregado:
/// é a fronteira de multi-tenancy e de permissões.
/// </summary>
public class Household : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Cadeado das mudanças aos membros e papéis (<c>MemberEndpoints.LockHouseholdAsync</c>): "há sempre um
    /// administrador" e "o último a sair apaga a casa" dependem de várias linhas, e dois pedidos em simultâneo
    /// (dois administradores despromovem-se um ao outro) passavam os dois a verificação.
    /// </summary>
    public Guid Version { get; set; }
    public required string Name { get; set; }

    /// <summary>Fuso horário IANA da casa. Define o que é "hoje" para prazos e tarefas.</summary>
    public string TimeZoneId { get; set; } = DefaultTimeZoneId;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// O código da família: com ele e o PIN, as crianças entram nos seus perfis. Null = ainda não foi criado
    /// (ou foi revogado).
    /// </summary>
    public string? ChildSignInCode { get; set; }

    public const string DefaultTimeZoneId = "Europe/Lisbon";

    /// <summary>Fuso da casa; se o sistema não o conhecer, usa UTC.</summary>
    public TimeZoneInfo TimeZone =>
        TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var found) ? found : TimeZoneInfo.Utc;

    public DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZone).DateTime);

    public List<Membership> Members { get; set; } = [];
    public List<Invite> Invites { get; set; } = [];
}
