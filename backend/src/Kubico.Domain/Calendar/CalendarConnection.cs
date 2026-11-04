namespace Kubico.Domain.Calendar;

public enum CalendarProvider
{
    Google,
    Microsoft,

    /// <summary>iCloud (CalDAV), com o Apple ID e uma palavra-passe específica da app.</summary>
    Apple,
}

public enum CalendarConnectionStatus
{
    Active,

    /// <summary>A conta deixou de dar acesso (palavra-passe mudada, acesso retirado): é preciso voltar a ligar.</summary>
    NeedsReconnect,
}

/// <summary>
/// A ligação de um membro a uma conta Google ou Microsoft. O Kubico cria nessa conta um calendário próprio
/// ("Kubico · Casa") com os eventos da casa, e o que se mudar lá volta para o Kubico. Os outros calendários da
/// pessoa nunca são lidos nem alterados.
/// </summary>
public class CalendarConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid UserId { get; set; }
    public CalendarProvider Provider { get; set; }

    /// <summary>O email da conta ligada, só para mostrar.</summary>
    public required string AccountEmail { get; set; }

    /// <summary>O calendário criado pelo Kubico na conta externa.</summary>
    public required string ExternalCalendarId { get; set; }

    /// <summary>O refresh token OAuth, cifrado (ASP.NET Data Protection).</summary>
    public required string ProtectedRefreshToken { get; set; }

    /// <summary>Onde ficou a última leitura de alterações (o syncToken do Google). Null = ler tudo.</summary>
    public string? SyncCursor { get; set; }

    public CalendarConnectionStatus Status { get; set; } = CalendarConnectionStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>O que correu mal (ou ficou de fora) na última sincronização, pronto a mostrar.</summary>
    public string? LastError { get; set; }
}

/// <summary>
/// Liga um evento do Kubico à sua cópia num calendário externo. Não tem chave estrangeira para o evento: quando
/// este é apagado no Kubico, a ligação ainda diz que cópia apagar.
/// </summary>
public class CalendarEventLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public Guid EventId { get; set; }

    public required string ExternalId { get; set; }

    /// <summary>A versão da cópia externa quando foi sincronizada (etag no Google, changeKey no Outlook).</summary>
    public string? ExternalVersion { get; set; }

    /// <summary>O <see cref="CalendarEvent.UpdatedAt"/> do evento quando foi sincronizado.</summary>
    public DateTimeOffset SyncedUpdatedAt { get; set; }

    /// <summary>A cópia é uma ocorrência de uma série externa (uma exceção), e não um evento próprio.</summary>
    public bool IsOccurrence { get; set; }
}
