using System.Globalization;
using System.Net;
using System.Text.Json;
using Kubico.Domain.Calendar;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Calendar.Sync;

/// <summary>
/// Google Calendar (API v3). As repetições são RRULE/EXDATE como no iCalendar; as alterações chegam com o syncToken
/// (só o que mudou desde a última leitura), e um dia alterado numa série é uma "instância" com recurringEventId.
/// </summary>
public class GoogleCalendarProvider(HttpClient http, IOptions<CalendarSyncOptions> options) : ICalendarProvider
{
    private const string Service = "Google";
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string Api = "https://www.googleapis.com/calendar/v3";

    /// <summary>Só dá acesso aos calendários criados pelo Kubico: os outros calendários da pessoa ficam de fora.</summary>
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar.app.created";

    private OAuthClientOptions Client => options.Value.Google;

    public CalendarProvider Kind => CalendarProvider.Google;

    public bool IsConfigured => Client.IsConfigured;

    public string AuthorizationUrl(string redirectUri, string state, string codeChallenge) =>
        OAuth.Url(AuthorizeEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = Client.ClientId!,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = $"openid email {CalendarScope}",

            // Sem "offline" e "consent", o Google não dá o refresh token.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });

    public Task<OAuthTokens> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct) =>
        OAuth.RequestTokenAsync(http, Service, TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["client_id"] = Client.ClientId!,
            ["client_secret"] = Client.ClientSecret!,
        }, ct);

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        OAuth.RequestTokenAsync(http, Service, TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = Client.ClientId!,
            ["client_secret"] = Client.ClientSecret!,
        }, ct);

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken });
        using var _ = await http.PostAsync(RevokeEndpoint, content, ct);
    }

    public async Task<string> CreateCalendarAsync(string accessToken, string name, TimeZoneInfo zone, CancellationToken ct)
    {
        var created = await Send(HttpMethod.Post, $"{Api}/calendars", accessToken,
            new { summary = name, timeZone = TimeZoneIds.Iana(zone) }, ct);
        return created.Str("id") ?? throw new CalendarProviderException("O Google não devolveu o calendário criado.");
    }

    public Task DeleteCalendarAsync(string accessToken, string calendarId, CancellationToken ct) =>
        Send(HttpMethod.Delete, $"{Api}/calendars/{Esc(calendarId)}", accessToken, null, ct, allowMissing: true);

    public async Task<ExternalChanges> ListChangesAsync(ExternalCalendar calendar, string? cursor, CancellationToken ct)
    {
        var events = new List<ExternalEvent>();
        string? pageToken = null;
        while (true)
        {
            // Na primeira leitura vem tudo o que existe; depois, com o syncToken, só o que mudou (apagados incluídos).
            var query = "maxResults=250&singleEvents=false"
                + (cursor is null ? "&showDeleted=false" : $"&showDeleted=true&syncToken={Esc(cursor)}")
                + (pageToken is null ? "" : $"&pageToken={Esc(pageToken)}");

            JsonElement? page;
            try
            {
                page = await Send(HttpMethod.Get, $"{Api}/calendars/{Esc(calendar.CalendarId)}/events?{query}",
                    calendar.AccessToken, null, ct);
            }
            catch (CalendarProviderException e) when (e.Status == HttpStatusCode.Gone && cursor is not null)
            {
                // O syncToken expirou: lê-se tudo outra vez.
                return await ListChangesAsync(calendar, null, ct);
            }
            catch (CalendarProviderException e) when (e.Status == HttpStatusCode.NotFound)
            {
                throw new CalendarNotFoundException();
            }

            events.AddRange(page.Items("items").Select(item => ToExternal(item, calendar.Zone)).OfType<ExternalEvent>());
            pageToken = page.Str("nextPageToken");
            if (pageToken is null)
            {
                return new ExternalChanges(events, IsFullListing: cursor is null, page.Str("nextSyncToken"));
            }
        }
    }

    public async Task<ExternalRef> CreateEventAsync(ExternalCalendar calendar, ExternalEventData data, CancellationToken ct)
    {
        try
        {
            var created = await Send(HttpMethod.Post, $"{Api}/calendars/{Esc(calendar.CalendarId)}/events",
                calendar.AccessToken, ToGoogle(data, calendar.Zone, isOccurrence: false), ct);
            return Ref(created);
        }
        catch (CalendarProviderException e) when (e.Status == HttpStatusCode.NotFound)
        {
            throw new CalendarNotFoundException();
        }
    }

    public async Task<ExternalRef?> UpdateEventAsync(
        ExternalCalendar calendar, string eventId, ExternalEventData data, bool isOccurrence, CancellationToken ct)
    {
        // Um evento é substituído por inteiro (PUT); uma ocorrência só muda o que o Kubico tem (PATCH).
        var updated = await Send(isOccurrence ? HttpMethod.Patch : HttpMethod.Put,
            $"{Api}/calendars/{Esc(calendar.CalendarId)}/events/{Esc(eventId)}",
            calendar.AccessToken, ToGoogle(data, calendar.Zone, isOccurrence), ct, allowMissing: true);
        return updated is null ? null : Ref(updated);
    }

    public async Task<ExternalRef?> UpdateOccurrenceAsync(
        ExternalCalendar calendar, string seriesId, DateOnly originalDate, ExternalEventData data, CancellationToken ct)
    {
        // As ocorrências à volta desse dia (apagadas incluídas: alterar uma volta a pô-la na série).
        var from = ZonedTime.StartOfDay(originalDate.AddDays(-1), calendar.Zone);
        var to = ZonedTime.StartOfDay(originalDate.AddDays(2), calendar.Zone);
        var instances = await Send(HttpMethod.Get,
            $"{Api}/calendars/{Esc(calendar.CalendarId)}/events/{Esc(seriesId)}/instances"
            + $"?showDeleted=true&maxResults=50&timeMin={Esc(ProviderHttp.Utc(from))}&timeMax={Esc(ProviderHttp.Utc(to))}",
            calendar.AccessToken, null, ct, allowMissing: true);

        var instance = instances.Items("items")
            .FirstOrDefault(i => OriginalDate(i, calendar.Zone) == originalDate && i.Str("id") is not null);
        return instance.ValueKind == JsonValueKind.Undefined
            ? null
            : await UpdateEventAsync(calendar, instance.Str("id")!, data, isOccurrence: true, ct);
    }

    public Task DeleteEventAsync(ExternalCalendar calendar, string eventId, CancellationToken ct) =>
        Send(HttpMethod.Delete, $"{Api}/calendars/{Esc(calendar.CalendarId)}/events/{Esc(eventId)}",
            calendar.AccessToken, null, ct, allowMissing: true);

    /// <summary>O corpo de um evento. Os campos de hora vão sempre todos, para um PATCH não misturar dia inteiro e hora.</summary>
    internal static Dictionary<string, object?> ToGoogle(ExternalEventData data, TimeZoneInfo zone, bool isOccurrence)
    {
        var tzid = TimeZoneIds.Iana(zone);
        var body = new Dictionary<string, object?>
        {
            ["summary"] = data.Title,
            ["description"] = data.Description,
            ["location"] = data.Location,
            ["start"] = data.IsAllDay
                ? Time(date: ProviderHttp.Day(data.StartDate!.Value))
                : Time(dateTime: ProviderHttp.Local(data.StartsAt!.Value, zone), timeZone: tzid),
            ["end"] = data.IsAllDay
                ? Time(date: ProviderHttp.Day(data.EndDate!.Value.AddDays(1)))
                : Time(dateTime: ProviderHttp.Local(data.EndsAt!.Value, zone), timeZone: tzid),
        };

        if (isOccurrence)
        {
            // Uma ocorrência apagada lá volta a aparecer se o Kubico a alterar.
            body["status"] = "confirmed";
        }
        else
        {
            body["recurrence"] = IcsRecurrence.ToLines(data, zone, tzid);
        }

        return body;
    }

    private static Dictionary<string, string?> Time(string? date = null, string? dateTime = null, string? timeZone = null) =>
        new() { ["date"] = date, ["dateTime"] = dateTime, ["timeZone"] = timeZone };

    /// <summary>Um evento do Google no modelo do Kubico; null se não tiver datas que se percebam.</summary>
    internal static ExternalEvent? ToExternal(JsonElement item, TimeZoneInfo zone)
    {
        if (item.Str("id") is not { } id)
        {
            return null;
        }

        var version = item.Str("etag");
        var seriesId = item.Str("recurringEventId");
        var originalDate = OriginalDate(item, zone);
        DateTimeOffset? updated = DateTimeOffset.TryParse(item.Str("updated"), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : null;

        if (item.Str("status") == "cancelled")
        {
            return new ExternalEvent(id, version, updated, null, seriesId, originalDate);
        }

        var (startDate, startsAt) = ReadTime(item.Prop("start"));
        var (endDate, endsAt) = ReadTime(item.Prop("end"));
        var title = item.Str("summary") ?? "";
        var description = item.Str("description");
        var location = item.Str("location");

        ExternalEventData data;
        if (startDate is { } firstDay)
        {
            // No Google o fim de um evento de dia inteiro é o dia seguinte; no Kubico é o último dia.
            var lastDay = endDate is { } end && end > firstDay ? end.AddDays(-1) : firstDay;
            data = new ExternalEventData(title, description, location, null, null, firstDay, lastDay);
        }
        else if (startsAt is { } start)
        {
            // Eventos sem duração (lembretes) ficam com 15 minutos: no Kubico o fim é depois do início.
            var finish = endsAt is { } e && e > start ? e : start.AddMinutes(15);
            data = new ExternalEventData(title, description, location, start, finish, null, null);
        }
        else
        {
            return null;
        }

        var lines = item.Items("recurrence").Select(l => l.GetString()).OfType<string>().ToList();
        if (lines.Count > 0)
        {
            var (rule, cancelled, unsupported) = IcsRecurrence.FromLines(lines, data.FirstDate(zone), zone);
            data = data with { Recurrence = rule, CancelledDates = cancelled, UnsupportedRecurrence = unsupported };
        }

        return new ExternalEvent(id, version, updated, data, seriesId, originalDate);
    }

    /// <summary>Numa ocorrência de uma série, o dia (local) em que estava antes de ser alterada.</summary>
    private static DateOnly? OriginalDate(JsonElement item, TimeZoneInfo zone) => ReadTime(item.Prop("originalStartTime")) switch
    {
        ({ } date, _) => date,
        (_, { } instant) => ProviderHttp.LocalDate(instant, zone),
        _ => null,
    };

    private static (DateOnly? Date, DateTimeOffset? Instant) ReadTime(JsonElement? time)
    {
        if (time.Str("date") is { } text
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (date, null);
        }

        return DateTimeOffset.TryParse(time.Str("dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? (null, instant.ToUniversalTime())
            : (null, null);
    }

    private static ExternalRef Ref(JsonElement? json) =>
        new(json.Str("id") ?? throw new CalendarProviderException("O Google não devolveu o evento."), json.Str("etag"));

    private async Task<JsonElement?> Send(
        HttpMethod method, string url, string accessToken, object? body, CancellationToken ct, bool allowMissing = false) =>
        await ProviderHttp.SendAsync(http, Service, method, url, accessToken, body, ct, allowMissing);

    private static string Esc(string value) => ProviderHttp.Escape(value);
}
