using System.Net;
using Kubico.Domain.Calendar;

namespace Kubico.Api.Calendar.Sync;

/// <summary>Credenciais OAuth de uma app registada no Google Cloud ou no Microsoft Entra.</summary>
public class OAuthClientOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public class MicrosoftClientOptions : OAuthClientOptions
{
    /// <summary>"common" aceita contas pessoais (Outlook.com) e de empresa.</summary>
    public string Tenant { get; set; } = "common";
}

public class CalendarSyncOptions
{
    public const string SectionName = "CalendarSync";

    public OAuthClientOptions Google { get; set; } = new();
    public MicrosoftClientOptions Microsoft { get; set; } = new();

    /// <summary>De quanto em quanto tempo se vão buscar as alterações feitas nos calendários externos.</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Corre a sincronização em segundo plano (desligada nos testes, que a chamam à mão).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>O calendário do Kubico numa conta externa, pronto a usar: o token de acesso, o id e o fuso da casa.</summary>
public sealed record ExternalCalendar(string AccessToken, string CalendarId, TimeZoneInfo Zone);

/// <summary>A resposta do servidor OAuth. O refresh token só vem quando muda (a Microsoft troca-o a cada uso).</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, string? Email);

/// <summary>
/// Um evento no modelo do Kubico, a caminho de ou vindo de um calendário externo. <see cref="EndDate"/> é inclusive.
/// <see cref="CancelledDates"/> são dias retirados da série: a enviar, todos os que não têm exceção; vindos de
/// fora, os que o serviço indica (o Outlook não indica nenhum), que se juntam aos que o Kubico já tem.
/// </summary>
public sealed record ExternalEventData(
    string Title,
    string? Description,
    string? Location,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? Recurrence = null,
    IReadOnlyList<DateOnly>? CancelledDates = null,
    bool UnsupportedRecurrence = false)
{
    public bool IsAllDay => StartDate is not null;

    public IReadOnlyList<DateOnly> Cancelled => CancelledDates ?? [];

    /// <summary>O dia (local) em que o evento começa.</summary>
    public DateOnly FirstDate(TimeZoneInfo zone) =>
        StartDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(StartsAt!.Value, zone).DateTime);
}

/// <summary>
/// Uma alteração lida do calendário externo. Sem <see cref="Data"/>, foi apagada. Numa ocorrência de uma série
/// (uma exceção feita lá), <see cref="SeriesId"/> é a série e <see cref="OriginalDate"/> o dia que substitui.
/// </summary>
public sealed record ExternalEvent(
    string Id,
    string? Version,
    DateTimeOffset? UpdatedAt,
    ExternalEventData? Data,
    string? SeriesId = null,
    DateOnly? OriginalDate = null)
{
    public bool IsDeleted => Data is null;

    public bool IsOccurrence => SeriesId is not null;
}

/// <summary>
/// As alterações desde a última leitura. Numa leitura completa (<see cref="IsFullListing"/>), o que não vier
/// foi apagado. <see cref="Cursor"/> é onde a próxima leitura começa.
/// </summary>
public sealed record ExternalChanges(IReadOnlyList<ExternalEvent> Events, bool IsFullListing, string? Cursor);

/// <summary>A cópia de um evento no calendário externo e a sua versão.</summary>
public sealed record ExternalRef(string Id, string? Version);

/// <summary>Um serviço de calendário (Google Calendar ou Outlook) visto pelo sincronizador.</summary>
public interface ICalendarProvider
{
    CalendarProvider Kind { get; }

    /// <summary>Se o servidor tem as credenciais OAuth deste serviço.</summary>
    bool IsConfigured { get; }

    /// <summary>A página onde a pessoa autoriza o Kubico (OAuth com PKCE).</summary>
    string AuthorizationUrl(string redirectUri, string state, string codeChallenge);

    Task<OAuthTokens> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct);

    Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary>Retira o acesso dado ao Kubico, quando o serviço o permite. Falhas são ignoradas.</summary>
    Task RevokeAsync(string refreshToken, CancellationToken ct);

    Task<string> CreateCalendarAsync(string accessToken, string name, TimeZoneInfo zone, CancellationToken ct);

    Task DeleteCalendarAsync(string accessToken, string calendarId, CancellationToken ct);

    Task<ExternalChanges> ListChangesAsync(ExternalCalendar calendar, string? cursor, CancellationToken ct);

    Task<ExternalRef> CreateEventAsync(ExternalCalendar calendar, ExternalEventData data, CancellationToken ct);

    /// <summary>
    /// Substitui uma cópia (um evento, ou uma ocorrência já alterada quando <paramref name="isOccurrence"/>).
    /// Devolve null se a cópia já não existir.
    /// </summary>
    Task<ExternalRef?> UpdateEventAsync(
        ExternalCalendar calendar, string eventId, ExternalEventData data, bool isOccurrence, CancellationToken ct);

    /// <summary>Altera só a ocorrência de um dia de uma série. Devolve null se a série não tiver ocorrência nesse dia.</summary>
    Task<ExternalRef?> UpdateOccurrenceAsync(
        ExternalCalendar calendar, string seriesId, DateOnly originalDate, ExternalEventData data, CancellationToken ct);

    /// <summary>Apaga uma cópia (numa ocorrência, retira esse dia da série). Se já não existir, não faz nada.</summary>
    Task DeleteEventAsync(ExternalCalendar calendar, string eventId, CancellationToken ct);
}

/// <summary>O acesso à conta expirou ou foi retirado: é preciso voltar a ligar.</summary>
public class CalendarAuthException(string message) : Exception(message);

/// <summary>O calendário do Kubico já não existe na conta (foi apagado lá).</summary>
public class CalendarNotFoundException() : Exception("O calendário do Kubico já não existe na conta ligada.");

/// <summary>O serviço recusou um pedido ou não respondeu como esperado.</summary>
public class CalendarProviderException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}
