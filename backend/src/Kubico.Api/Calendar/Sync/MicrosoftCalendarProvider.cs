using System.Globalization;
using System.Net;
using System.Text.Json;
using Kubico.Domain.Calendar;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Calendar.Sync;

/// <summary>
/// Outlook (Microsoft Graph v1.0). As repetições são um padrão estruturado (diário, semanal, mensal, anual), e os dias
/// retirados de uma série apagam-se ocorrência a ocorrência. As alterações lêem-se de cada vez com a lista completa
/// do calendário do Kubico (que só tem os eventos da casa); dias alterados numa série no Outlook não voltam ao Kubico.
/// </summary>
public class MicrosoftCalendarProvider(HttpClient http, IOptions<CalendarSyncOptions> options) : ICalendarProvider
{
    private const string Service = "Outlook";
    private const string Api = "https://graph.microsoft.com/v1.0";
    private const string EventFields = "id,changeKey,type,subject,body,location,start,end,isAllDay,recurrence,lastModifiedDateTime,isCancelled";

    public const string Scopes = "offline_access openid email Calendars.ReadWrite";

    private MicrosoftClientOptions Client => options.Value.Microsoft;

    private string Authority => $"https://login.microsoftonline.com/{Client.Tenant}/oauth2/v2.0";

    public CalendarProvider Kind => CalendarProvider.Microsoft;

    public bool IsConfigured => Client.IsConfigured;

    public string AuthorizationUrl(string redirectUri, string state, string codeChallenge) =>
        OAuth.Url($"{Authority}/authorize", new Dictionary<string, string>
        {
            ["client_id"] = Client.ClientId!,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        });

    public Task<OAuthTokens> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct) =>
        OAuth.RequestTokenAsync(http, Service, $"{Authority}/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = Scopes,
            ["client_id"] = Client.ClientId!,
            ["client_secret"] = Client.ClientSecret!,
        }, ct);

    /// <summary>A Microsoft devolve um refresh token novo a cada renovação; o sincronizador guarda-o.</summary>
    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        OAuth.RequestTokenAsync(http, Service, $"{Authority}/token", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scopes,
            ["client_id"] = Client.ClientId!,
            ["client_secret"] = Client.ClientSecret!,
        }, ct);

    /// <summary>A Microsoft não deixa retirar um só token; a pessoa pode retirar o acesso em account.microsoft.com.</summary>
    public Task RevokeAsync(string refreshToken, CancellationToken ct) => Task.CompletedTask;

    public async Task<string> CreateCalendarAsync(string accessToken, string name, TimeZoneInfo zone, CancellationToken ct)
    {
        // Os nomes dos calendários não se podem repetir na mesma conta.
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? name : $"{name} ({attempt})";
            try
            {
                var created = await Send(HttpMethod.Post, $"{Api}/me/calendars", accessToken, new { name = candidate }, ct);
                return created.Str("id") ?? throw new CalendarProviderException("O Outlook não devolveu o calendário criado.");
            }
            catch (CalendarProviderException e) when (e.Status == HttpStatusCode.Conflict && attempt < 5)
            {
            }
        }
    }

    public Task DeleteCalendarAsync(string accessToken, string calendarId, CancellationToken ct) =>
        Send(HttpMethod.Delete, $"{Api}/me/calendars/{Esc(calendarId)}", accessToken, null, ct, allowMissing: true);

    public async Task<ExternalChanges> ListChangesAsync(ExternalCalendar calendar, string? cursor, CancellationToken ct)
    {
        var events = new List<ExternalEvent>();
        string? url = $"{Api}/me/calendars/{Esc(calendar.CalendarId)}/events?$top=100&$select={EventFields}";
        while (url is not null)
        {
            JsonElement? page;
            try
            {
                page = await Send(HttpMethod.Get, url, calendar.AccessToken, null, ct);
            }
            catch (CalendarProviderException e) when (e.Status == HttpStatusCode.NotFound)
            {
                throw new CalendarNotFoundException();
            }

            events.AddRange(page.Items("value").Select(item => ToExternal(item, calendar.Zone)).OfType<ExternalEvent>());
            url = page.Str("@odata.nextLink");
        }

        return new ExternalChanges(events, IsFullListing: true, Cursor: null);
    }

    public async Task<ExternalRef> CreateEventAsync(ExternalCalendar calendar, ExternalEventData data, CancellationToken ct)
    {
        JsonElement? created;
        try
        {
            created = await Send(HttpMethod.Post, $"{Api}/me/calendars/{Esc(calendar.CalendarId)}/events",
                calendar.AccessToken, ToGraph(data, calendar.Zone, withRecurrence: true), ct);
        }
        catch (CalendarProviderException e) when (e.Status == HttpStatusCode.NotFound)
        {
            throw new CalendarNotFoundException();
        }

        return await CancelOccurrencesAsync(calendar, Ref(created), data, ct);
    }

    public async Task<ExternalRef?> UpdateEventAsync(
        ExternalCalendar calendar, string eventId, ExternalEventData data, bool isOccurrence, CancellationToken ct)
    {
        var withRecurrence = false;
        if (!isOccurrence)
        {
            // Mexer na repetição de uma série no Outlook desfaz os dias alterados e apagados: só se envia se mudou.
            var current = await Send(HttpMethod.Get, $"{Api}/me/events/{Esc(eventId)}?$select={EventFields}",
                calendar.AccessToken, null, ct, allowMissing: true);
            if (current is null)
            {
                return null;
            }

            var before = ToExternal(current.Value, calendar.Zone)?.Data;
            withRecurrence = before is null
                || before.Recurrence != data.Recurrence
                || (data.Recurrence is not null && (before.StartsAt, before.StartDate) != (data.StartsAt, data.StartDate));
        }

        var updated = await Send(HttpMethod.Patch, $"{Api}/me/events/{Esc(eventId)}", calendar.AccessToken,
            ToGraph(data, calendar.Zone, withRecurrence), ct, allowMissing: true);
        if (updated is null)
        {
            return null;
        }

        var copy = Ref(updated);
        return isOccurrence ? copy : await CancelOccurrencesAsync(calendar, copy, data, ct);
    }

    public async Task<ExternalRef?> UpdateOccurrenceAsync(
        ExternalCalendar calendar, string seriesId, DateOnly originalDate, ExternalEventData data, CancellationToken ct)
    {
        var instance = (await InstancesAsync(calendar, seriesId, originalDate, originalDate, ct))
            .FirstOrDefault(i => i.Date == originalDate);
        return instance.Id is null ? null : await UpdateEventAsync(calendar, instance.Id, data, isOccurrence: true, ct);
    }

    public Task DeleteEventAsync(ExternalCalendar calendar, string eventId, CancellationToken ct) =>
        Send(HttpMethod.Delete, $"{Api}/me/events/{Esc(eventId)}", calendar.AccessToken, null, ct, allowMissing: true);

    /// <summary>
    /// Apaga na cópia as ocorrências dos dias retirados da série. Devolve a cópia com a versão atual (apagar
    /// ocorrências muda a versão da série).
    /// </summary>
    private async Task<ExternalRef> CancelOccurrencesAsync(
        ExternalCalendar calendar, ExternalRef series, ExternalEventData data, CancellationToken ct)
    {
        if (data.Recurrence is null || data.Cancelled.Count == 0)
        {
            return series;
        }

        var cancelled = data.Cancelled.ToHashSet();
        var instances = await InstancesAsync(calendar, series.Id, cancelled.Min(), cancelled.Max(), ct);
        var deleted = false;
        foreach (var (id, date) in instances.Where(i => cancelled.Contains(i.Date)))
        {
            await DeleteEventAsync(calendar, id!, ct);
            deleted = true;
        }

        if (!deleted)
        {
            return series;
        }

        var fresh = await Send(HttpMethod.Get, $"{Api}/me/events/{Esc(series.Id)}?$select=id,changeKey",
            calendar.AccessToken, null, ct);
        return Ref(fresh);
    }

    /// <summary>As ocorrências de uma série entre dois dias, com o dia (local) original de cada uma.</summary>
    private async Task<List<(string? Id, DateOnly Date)>> InstancesAsync(
        ExternalCalendar calendar, string seriesId, DateOnly first, DateOnly last, CancellationToken ct)
    {
        var from = ProviderHttp.Utc(ZonedTime.StartOfDay(first.AddDays(-1), calendar.Zone));
        var to = ProviderHttp.Utc(ZonedTime.StartOfDay(last.AddDays(2), calendar.Zone));
        var result = new List<(string?, DateOnly)>();
        string? url = $"{Api}/me/events/{Esc(seriesId)}/instances?startDateTime={Esc(from)}&endDateTime={Esc(to)}"
            + "&$top=100&$select=id,start,originalStart";
        while (url is not null)
        {
            var page = await Send(HttpMethod.Get, url, calendar.AccessToken, null, ct, allowMissing: true);
            foreach (var item in page.Items("value"))
            {
                // originalStart é o início antes de qualquer alteração (em UTC); sem ele, vale o início atual.
                var original = DateTimeOffset.TryParse(item.Str("originalStart"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : ReadInstant(item.Prop("start"));
                if (item.Str("id") is { } id && original is { } instant)
                {
                    result.Add((id, ProviderHttp.LocalDate(instant, calendar.Zone)));
                }
            }

            url = page.Str("@odata.nextLink");
        }

        return result;
    }

    /// <summary>Um evento no formato do Graph. A repetição só vai quando <paramref name="withRecurrence"/> (null a apaga).</summary>
    internal static Dictionary<string, object?> ToGraph(ExternalEventData data, TimeZoneInfo zone, bool withRecurrence)
    {
        var tz = TimeZoneIds.Windows(zone);
        var body = new Dictionary<string, object?>
        {
            ["subject"] = data.Title,
            ["body"] = new { contentType = "text", content = data.Description ?? "" },
            ["location"] = new { displayName = data.Location ?? "" },
            ["isAllDay"] = data.IsAllDay,

            // No Outlook um evento de dia inteiro vai da meia-noite do primeiro dia à meia-noite a seguir ao último.
            ["start"] = new
            {
                dateTime = data.IsAllDay ? Midnight(data.StartDate!.Value) : ProviderHttp.Local(data.StartsAt!.Value, zone),
                timeZone = tz,
            },
            ["end"] = new
            {
                dateTime = data.IsAllDay ? Midnight(data.EndDate!.Value.AddDays(1)) : ProviderHttp.Local(data.EndsAt!.Value, zone),
                timeZone = tz,
            },
        };

        if (withRecurrence)
        {
            body["recurrence"] = ToGraphRecurrence(data, zone, tz);
        }

        return body;
    }

    internal static object? ToGraphRecurrence(ExternalEventData data, TimeZoneInfo zone, string tz)
    {
        if (!RecurrenceRule.TryParse(data.Recurrence, out var rule))
        {
            return null;
        }

        var first = data.FirstDate(zone);
        object pattern = rule.Frequency switch
        {
            RecurrenceFrequency.Daily => new { type = "daily", interval = rule.Interval },
            RecurrenceFrequency.Weekly => new
            {
                type = "weekly",
                interval = rule.Interval,
                daysOfWeek = (rule.ByDay ?? [first.DayOfWeek]).Select(DayName).ToArray(),
                firstDayOfWeek = "monday",
            },
            RecurrenceFrequency.Monthly => new { type = "absoluteMonthly", interval = rule.Interval, dayOfMonth = first.Day },
            _ => new { type = "absoluteYearly", interval = rule.Interval, dayOfMonth = first.Day, month = first.Month },
        };

        var startDate = ProviderHttp.Day(first);
        object range = (rule.Count, rule.Until) switch
        {
            ({ } count, _) => new { type = "numbered", startDate, numberOfOccurrences = count, recurrenceTimeZone = tz },
            (_, { } until) => new { type = "endDate", startDate, endDate = ProviderHttp.Day(until), recurrenceTimeZone = tz },
            _ => new { type = "noEnd", startDate, recurrenceTimeZone = tz },
        };

        return new { pattern, range };
    }

    /// <summary>O padrão de repetição do Graph como RRULE do Kubico. Padrões relativos ("2.ª terça") não são suportados.</summary>
    internal static (string? Rule, bool Unsupported) FromGraphRecurrence(JsonElement recurrence, DateOnly first)
    {
        var pattern = recurrence.Prop("pattern");
        var range = recurrence.Prop("range");
        var interval = pattern.Int("interval") ?? 1;
        List<DayOfWeek>? days = null;
        RecurrenceFrequency frequency;
        switch (pattern.Str("type"))
        {
            case "daily":
                frequency = RecurrenceFrequency.Daily;
                break;
            case "weekly":
                frequency = RecurrenceFrequency.Weekly;
                days = [.. pattern.Items("daysOfWeek").Select(d => d.GetString()).OfType<string>()
                    .Select(d => Enum.TryParse<DayOfWeek>(d, ignoreCase: true, out var day) ? day : (DayOfWeek?)null)
                    .OfType<DayOfWeek>().Distinct()];
                if (days is [var only] && only == first.DayOfWeek)
                {
                    days = null;
                }

                break;
            case "absoluteMonthly" when pattern.Int("dayOfMonth") == first.Day:
                frequency = RecurrenceFrequency.Monthly;
                break;
            case "absoluteYearly" when pattern.Int("dayOfMonth") == first.Day && pattern.Int("month") == first.Month:
                frequency = RecurrenceFrequency.Yearly;
                break;
            default:
                return (null, true);
        }

        int? count = null;
        DateOnly? until = null;
        switch (range.Str("type"))
        {
            case "numbered":
                count = range.Int("numberOfOccurrences");
                break;
            case "endDate" when DateOnly.TryParseExact(range.Str("endDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end):
                until = end;
                break;
            case "noEnd":
                break;
            default:
                return (null, true);
        }

        // Passa pela validação do Kubico (limites de INTERVAL e COUNT, BYDAY só semanal).
        var text = new RecurrenceRule(frequency, interval, days is { Count: > 0 } ? days : null, count, until).ToString();
        return RecurrenceRule.TryParse(text, out var rule) ? (rule.ToString(), false) : (null, true);
    }

    /// <summary>Um evento do Graph no modelo do Kubico; null para ocorrências (não vêm na lista) e datas ilegíveis.</summary>
    internal static ExternalEvent? ToExternal(JsonElement item, TimeZoneInfo zone)
    {
        if (item.Str("id") is not { } id || item.Str("type") is "occurrence" or "exception")
        {
            return null;
        }

        var version = item.Str("changeKey");
        DateTimeOffset? updated = DateTimeOffset.TryParse(item.Str("lastModifiedDateTime"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        if (item.Bool("isCancelled"))
        {
            return new ExternalEvent(id, version, updated, null);
        }

        var title = item.Str("subject") ?? "";
        var description = item.Prop("body").Str("content");
        var location = item.Prop("location").Str("displayName") is { Length: > 0 } place ? place : null;

        ExternalEventData data;
        if (item.Bool("isAllDay"))
        {
            // Os eventos de dia inteiro não têm fuso: conta o dia escrito.
            if (ReadLocal(item.Prop("start")) is not { } start)
            {
                return null;
            }

            var firstDay = DateOnly.FromDateTime(start);
            var lastDay = ReadLocal(item.Prop("end")) is { } end && DateOnly.FromDateTime(end) > firstDay
                ? DateOnly.FromDateTime(end).AddDays(-1)
                : firstDay;
            data = new ExternalEventData(title, description, location, null, null, firstDay, lastDay);
        }
        else
        {
            if (ReadInstant(item.Prop("start")) is not { } start)
            {
                return null;
            }

            var finish = ReadInstant(item.Prop("end")) is { } end && end > start ? end : start.AddMinutes(15);
            data = new ExternalEventData(title, description, location, start, finish, null, null);
        }

        if (item.Prop("recurrence") is { } recurrence)
        {
            var (rule, unsupported) = FromGraphRecurrence(recurrence, data.FirstDate(zone));
            data = data with { Recurrence = rule, UnsupportedRecurrence = unsupported };
        }

        return new ExternalEvent(id, version, updated, data);
    }

    private static DateTime? ReadLocal(JsonElement? time) =>
        DateTime.TryParse(time.Str("dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local, DateTimeKind.Unspecified)
            : null;

    /// <summary>Um dateTimeTimeZone do Graph (pedido em UTC; noutro fuso, converte-se).</summary>
    private static DateTimeOffset? ReadInstant(JsonElement? time)
    {
        if (ReadLocal(time) is not { } local)
        {
            return null;
        }

        var zone = time.Str("timeZone") is { } id && id != "UTC" && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found)
            ? found
            : TimeZoneInfo.Utc;
        return ZonedTime.ToInstant(local, zone);
    }

    private static string Midnight(DateOnly date) => ProviderHttp.Day(date) + "T00:00:00";

    private static string DayName(DayOfWeek day) => day.ToString().ToLowerInvariant();

    private static ExternalRef Ref(JsonElement? json) =>
        new(json.Str("id") ?? throw new CalendarProviderException("O Outlook não devolveu o evento."), json.Str("changeKey"));

    /// <summary>Pede as horas em UTC e as descrições em texto simples (sem HTML).</summary>
    private Task<JsonElement?> Send(
        HttpMethod method, string url, string accessToken, object? body, CancellationToken ct, bool allowMissing = false) =>
        ProviderHttp.SendAsync(http, Service, method, url, accessToken, body, ct, allowMissing, request =>
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\", outlook.body-content-type=\"text\""));

    private static string Esc(string value) => ProviderHttp.Escape(value);
}
