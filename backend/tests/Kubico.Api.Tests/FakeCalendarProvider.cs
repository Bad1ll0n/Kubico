using Kubico.Api.Calendar.Sync;
using Kubico.Domain.Calendar;

namespace Kubico.Api.Tests;

/// <summary>
/// Um Google Calendar (ou Outlook) em memória. Como o Google, devolve só o que mudou desde o último cursor e diz os
/// dias alterados ou apagados de uma série; com <paramref name="fullListing"/>, como o Outlook, devolve sempre a
/// lista completa, sem ocorrências. Os métodos "Person…" são o que a pessoa faz lá, sem o Kubico saber.
/// </summary>
public sealed class FakeCalendarProvider(CalendarProvider kind, bool fullListing, Func<DateTimeOffset> now) : ICalendarProvider
{
    private int _version;
    private int _ids;

    public Dictionary<string, FakeCalendar> Calendars { get; } = [];

    /// <summary>Contas cujo acesso foi retirado (a renovação do token falha).</summary>
    public HashSet<string> RevokedAccounts { get; } = [];

    /// <summary>Os refresh tokens que o Kubico pediu para retirar ao desligar.</summary>
    public List<string> RevokedTokens { get; } = [];

    /// <summary>Os refresh tokens usados, pela ordem (para ver que a Microsoft os troca e o Kubico guarda o novo).</summary>
    public List<string> RefreshTokensUsed { get; } = [];

    public CalendarProvider Kind => kind;

    public bool IsConfigured => true;

    public string AuthorizationUrl(string redirectUri, string state, string codeChallenge) =>
        $"https://login.example/{kind}?redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&state={Uri.EscapeDataString(state)}&code_challenge={codeChallenge}";

    /// <summary>O código "ok:email" autoriza essa conta; qualquer outro é recusado.</summary>
    public Task<OAuthTokens> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct) =>
        code.StartsWith("ok:", StringComparison.Ordinal)
            ? Task.FromResult(new OAuthTokens("access", Token(code[3..]), code[3..]))
            : throw new CalendarAuthException("Código inválido.");

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var account = refreshToken.Split('|')[1];
        if (RevokedAccounts.Contains(account))
        {
            throw new CalendarAuthException("Acesso retirado.");
        }

        RefreshTokensUsed.Add(refreshToken);

        // A Microsoft troca o refresh token a cada renovação; o Google não.
        return Task.FromResult(new OAuthTokens("access", kind == CalendarProvider.Microsoft ? Token(account) : null, null));
    }

    public Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        RevokedTokens.Add(refreshToken);
        return Task.CompletedTask;
    }

    public Task<string> CreateCalendarAsync(string accessToken, string name, TimeZoneInfo zone, CancellationToken ct)
    {
        var id = $"cal-{++_ids}";
        Calendars[id] = new FakeCalendar(name);
        return Task.FromResult(id);
    }

    public Task DeleteCalendarAsync(string accessToken, string calendarId, CancellationToken ct)
    {
        Calendars.Remove(calendarId);
        return Task.CompletedTask;
    }

    public Task<ExternalChanges> ListChangesAsync(ExternalCalendar calendar, string? cursor, CancellationToken ct)
    {
        var events = Get(calendar).Events.Values;
        if (fullListing)
        {
            return Task.FromResult(new ExternalChanges(
                [.. events.Where(e => !e.Deleted && e.SeriesId is null).Select(ToExternal)], true, null));
        }

        var changed = cursor is null
            ? events.Where(e => !e.Deleted)
            : events.Where(e => e.Version > int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture));
        return Task.FromResult(new ExternalChanges(
            [.. changed.OrderBy(e => e.Version).Select(ToExternal)], cursor is null, $"{_version}"));
    }

    public Task<ExternalRef> CreateEventAsync(ExternalCalendar calendar, ExternalEventData data, CancellationToken ct)
    {
        var e = Store(Get(calendar), new FakeEvent($"ev-{++_ids}") { Data = data });
        return Task.FromResult(Ref(e));
    }

    public Task<ExternalRef?> UpdateEventAsync(
        ExternalCalendar calendar, string eventId, ExternalEventData data, bool isOccurrence, CancellationToken ct)
    {
        if (!Get(calendar).Events.TryGetValue(eventId, out var e) || (e.Deleted && !isOccurrence))
        {
            return Task.FromResult<ExternalRef?>(null);
        }

        e.Data = isOccurrence ? data with { Recurrence = null, CancelledDates = null } : data;
        e.Deleted = false;
        Store(Get(calendar), e);
        return Task.FromResult<ExternalRef?>(Ref(e));
    }

    public Task<ExternalRef?> UpdateOccurrenceAsync(
        ExternalCalendar calendar, string seriesId, DateOnly originalDate, ExternalEventData data, CancellationToken ct)
    {
        var target = Get(calendar);
        if (!target.Events.TryGetValue(seriesId, out var series) || series.Deleted || series.Data.Recurrence is null)
        {
            return Task.FromResult<ExternalRef?>(null);
        }

        var e = Occurrence(target, seriesId, originalDate);
        e.Data = data with { Recurrence = null, CancelledDates = null };
        e.Deleted = false;
        Store(target, e);
        return Task.FromResult<ExternalRef?>(Ref(e));
    }

    public Task DeleteEventAsync(ExternalCalendar calendar, string eventId, CancellationToken ct)
    {
        var target = Get(calendar);
        if (target.Events.TryGetValue(eventId, out var e) && !e.Deleted)
        {
            e.Deleted = true;
            Store(target, e);
        }

        return Task.CompletedTask;
    }

    // --- O que a pessoa faz no calendário externo -------------------------------

    public FakeCalendar CalendarNamed(string name) => Calendars.Values.Single(c => c.Name == name);

    public string CalendarIdNamed(string name) => Calendars.Single(c => c.Value.Name == name).Key;

    public string PersonAdds(string calendarName, ExternalEventData data) =>
        Store(CalendarNamed(calendarName), new FakeEvent($"ext-{++_ids}") { Data = data }).Id;

    public void PersonChanges(string calendarName, string eventId, Func<ExternalEventData, ExternalEventData> change)
    {
        var target = CalendarNamed(calendarName);
        var e = target.Events[eventId];
        e.Data = change(e.Data);
        Store(target, e);
    }

    public void PersonDeletes(string calendarName, string eventId)
    {
        var target = CalendarNamed(calendarName);
        target.Events[eventId].Deleted = true;
        Store(target, target.Events[eventId]);
    }

    /// <summary>Altera só um dia de uma série (fica uma exceção, com o id da ocorrência).</summary>
    public string PersonChangesOccurrence(string calendarName, string seriesId, DateOnly date, ExternalEventData data)
    {
        var target = CalendarNamed(calendarName);
        var e = Occurrence(target, seriesId, date);
        e.Data = data;
        e.Deleted = false;
        return Store(target, e).Id;
    }

    /// <summary>Apaga só um dia de uma série.</summary>
    public void PersonCancelsOccurrence(string calendarName, string seriesId, DateOnly date)
    {
        var target = CalendarNamed(calendarName);
        var e = Occurrence(target, seriesId, date);
        e.Deleted = true;
        Store(target, e);
    }

    private static FakeEvent Occurrence(FakeCalendar calendar, string seriesId, DateOnly date)
    {
        var id = $"{seriesId}_{date:yyyyMMdd}";
        if (!calendar.Events.TryGetValue(id, out var e))
        {
            e = new FakeEvent(id) { SeriesId = seriesId, OriginalDate = date, Data = calendar.Events[seriesId].Data };
            calendar.Events[id] = e;
        }

        return e;
    }

    private FakeEvent Store(FakeCalendar calendar, FakeEvent e)
    {
        e.Version = ++_version;
        e.Updated = now();
        calendar.Events[e.Id] = e;
        return e;
    }

    private FakeCalendar Get(ExternalCalendar calendar) =>
        Calendars.GetValueOrDefault(calendar.CalendarId) ?? throw new CalendarNotFoundException();

    private static ExternalEvent ToExternal(FakeEvent e) =>
        new(e.Id, $"v{e.Version}", e.Updated, e.Deleted ? null : e.Data, e.SeriesId, e.OriginalDate);

    private static ExternalRef Ref(FakeEvent e) => new(e.Id, $"v{e.Version}");

    private string Token(string account) => $"refresh|{account}|{++_ids}";
}

public sealed class FakeCalendar(string name)
{
    public string Name => name;

    public Dictionary<string, FakeEvent> Events { get; } = [];

    /// <summary>Os eventos (séries e eventos únicos) que lá estão.</summary>
    public IEnumerable<FakeEvent> Live => Events.Values.Where(e => !e.Deleted && e.SeriesId is null);
}

public sealed class FakeEvent(string id)
{
    public string Id => id;

    public required ExternalEventData Data { get; set; }

    public int Version { get; set; }

    public DateTimeOffset Updated { get; set; }

    public bool Deleted { get; set; }

    public string? SeriesId { get; init; }

    public DateOnly? OriginalDate { get; init; }
}
