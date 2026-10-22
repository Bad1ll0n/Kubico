using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Kubico.Domain.Calendar;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Calendar.Sync;

/// <summary>Onde está o servidor CalDAV do iCloud (nos testes, um servidor falso).</summary>
public class AppleCalendarOptions
{
    public string ServerUrl { get; set; } = "https://caldav.icloud.com/";
}

/// <summary>
/// Um serviço em que a pessoa entra com a conta e uma palavra-passe, em vez de OAuth (o iCloud não tem OAuth para
/// calendários). As credenciais guardadas (cifradas) fazem o papel do refresh token.
/// </summary>
public interface IPasswordCalendarProvider : ICalendarProvider
{
    /// <summary>Confirma a conta e a palavra-passe. Falha com <see cref="CalendarAuthException"/> se não servirem.</summary>
    Task<OAuthTokens> SignInAsync(string account, string password, CancellationToken ct);
}

/// <summary>
/// iCloud por CalDAV (RFC 4791), com o Apple ID e uma palavra-passe específica da app. Cada evento do Kubico é um
/// recurso .ics no calendário do Kubico; os dias alterados de uma série ficam no mesmo recurso (RECURRENCE-ID),
/// por isso a cópia de um dia tem o id "recurso#aaaammdd". Lê-se sempre a lista completa (como no Outlook).
/// </summary>
public sealed class AppleCalendarProvider(HttpClient http, IOptions<CalendarSyncOptions> options, TimeProvider clock)
    : IPasswordCalendarProvider
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    public CalendarProvider Kind => CalendarProvider.Apple;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.Apple.ServerUrl);

    public string AuthorizationUrl(string redirectUri, string state, string codeChallenge) =>
        throw new NotSupportedException("O iCloud liga-se com o Apple ID e uma palavra-passe da app.");

    public Task<OAuthTokens> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct) =>
        throw new NotSupportedException("O iCloud liga-se com o Apple ID e uma palavra-passe da app.");

    public async Task<OAuthTokens> SignInAsync(string account, string password, CancellationToken ct)
    {
        var credentials = Credentials(account.Trim(), password.Replace(" ", ""));
        await PrincipalAsync(credentials, ct);
        return new OAuthTokens(credentials, credentials, account.Trim());
    }

    /// <summary>As credenciais não expiram: só deixam de servir se a pessoa apagar a palavra-passe da app.</summary>
    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        Task.FromResult(new OAuthTokens(refreshToken, null, null));

    /// <summary>A palavra-passe da app apaga-se no site da Apple; aqui não há nada a retirar.</summary>
    public Task RevokeAsync(string refreshToken, CancellationToken ct) => Task.CompletedTask;

    public async Task<string> CreateCalendarAsync(string accessToken, string name, TimeZoneInfo zone, CancellationToken ct)
    {
        var home = await HomeAsync(accessToken, await PrincipalAsync(accessToken, ct), ct);
        var url = new Uri(home, Guid.NewGuid().ToString("N") + "/");
        var body = new XElement(CalDav + "mkcalendar",
            new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", CalDav.NamespaceName),
            new XElement(Dav + "set", new XElement(Dav + "prop",
                new XElement(Dav + "displayname", name),
                new XElement(CalDav + "supported-calendar-component-set",
                    new XElement(CalDav + "comp", new XAttribute("name", "VEVENT"))))));
        using var _ = await SendAsync(accessToken, "MKCALENDAR", url, Xml(body), ct);
        return url.AbsoluteUri;
    }

    public async Task DeleteCalendarAsync(string accessToken, string calendarId, CancellationToken ct)
    {
        using var _ = await SendAsync(accessToken, "DELETE", new Uri(calendarId), null, ct, allowMissing: true);
    }

    public async Task<ExternalChanges> ListChangesAsync(ExternalCalendar calendar, string? cursor, CancellationToken ct)
    {
        var url = new Uri(calendar.CalendarId);
        var body = new XElement(CalDav + "calendar-query",
            new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", CalDav.NamespaceName),
            new XElement(Dav + "prop", new XElement(Dav + "getetag"), new XElement(CalDav + "calendar-data")),
            new XElement(CalDav + "filter", new XElement(CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"),
                new XElement(CalDav + "comp-filter", new XAttribute("name", "VEVENT")))));
        using var response = await SendAsync(calendar.AccessToken, "REPORT", url, Xml(body), ct, depth: 1, allowMissing: true);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new CalendarNotFoundException();
        }

        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var events = new List<ExternalEvent>();
        foreach (var item in document.Descendants(Dav + "response"))
        {
            var href = item.Element(Dav + "href")?.Value.Trim();
            var data = item.Descendants(CalDav + "calendar-data").FirstOrDefault()?.Value;
            if (href is { Length: > 0 } && !string.IsNullOrWhiteSpace(data))
            {
                events.AddRange(ToExternal(new Uri(url, href).AbsolutePath, IcsComponent.Parse(data), calendar.Zone));
            }
        }

        return new ExternalChanges(events, IsFullListing: true, Cursor: null);
    }

    public async Task<ExternalRef> CreateEventAsync(ExternalCalendar calendar, ExternalEventData data, CancellationToken ct)
    {
        var url = new Uri(new Uri(calendar.CalendarId), Guid.NewGuid().ToString("N") + ".ics");
        var master = Event($"{Guid.NewGuid():N}@kubico", data, calendar.Zone, recurrenceId: null, clock.GetUtcNow());
        await PutAsync(calendar, url, [master], create: true, ct);
        return new ExternalRef(url.AbsolutePath, master.Version());
    }

    public async Task<ExternalRef?> UpdateEventAsync(
        ExternalCalendar calendar, string eventId, ExternalEventData data, bool isOccurrence, CancellationToken ct)
    {
        var (path, date) = Split(eventId);
        if (isOccurrence && date is { } originalDate)
        {
            return await UpdateOccurrenceAsync(calendar, path, originalDate, data, ct);
        }

        var url = new Uri(new Uri(calendar.CalendarId), path);
        if (await GetAsync(calendar, url, ct) is not { } resource || Master(resource) is not { } old)
        {
            return null;
        }

        var master = Event(Uid(old), data, calendar.Zone, recurrenceId: null, clock.GetUtcNow());
        await PutAsync(calendar, url, [master, .. Overrides(resource)], create: false, ct);
        return new ExternalRef(path, master.Version());
    }

    public async Task<ExternalRef?> UpdateOccurrenceAsync(
        ExternalCalendar calendar, string seriesId, DateOnly originalDate, ExternalEventData data, CancellationToken ct)
    {
        var url = new Uri(new Uri(calendar.CalendarId), seriesId);
        if (await GetAsync(calendar, url, ct) is not { } resource || Master(resource) is not { } master
            || master.Property("RRULE") is null)
        {
            return null;
        }

        var recurrenceId = OccurrenceProperty("RECURRENCE-ID", master, originalDate, calendar.Zone);
        var occurrence = Event(
            Uid(master), data with { Recurrence = null, CancelledDates = null }, calendar.Zone, recurrenceId, clock.GetUtcNow());
        var others = Overrides(resource).Where(o => OriginalDate(o, calendar.Zone) != originalDate);
        await PutAsync(calendar, url, [master, .. others, occurrence], create: false, ct);
        return new ExternalRef($"{seriesId}#{Day(originalDate)}", occurrence.Version());
    }

    public async Task DeleteEventAsync(ExternalCalendar calendar, string eventId, CancellationToken ct)
    {
        var (path, date) = Split(eventId);
        var url = new Uri(new Uri(calendar.CalendarId), path);
        if (date is not { } originalDate)
        {
            using var _ = await SendAsync(calendar.AccessToken, "DELETE", url, null, ct, allowMissing: true);
            return;
        }

        // Um dia de uma série: sai a alteração desse dia e o dia passa a estar retirado da série (EXDATE).
        if (await GetAsync(calendar, url, ct) is not { } resource || Master(resource) is not { } master)
        {
            return;
        }

        master.Lines.Insert(Math.Max(0, master.Lines.FindLastIndex(l => l.StartsWith("RRULE", StringComparison.OrdinalIgnoreCase)) + 1),
            OccurrenceProperty("EXDATE", master, originalDate, calendar.Zone));
        var others = Overrides(resource).Where(o => OriginalDate(o, calendar.Zone) != originalDate);
        await PutAsync(calendar, url, [master, .. others], create: false, ct);
    }

    // --- Leitura ------------------------------------------------------------

    /// <summary>Um recurso: a série (ou o evento) e os dias alterados dela.</summary>
    internal static IEnumerable<ExternalEvent> ToExternal(string path, IcsComponent resource, TimeZoneInfo zone)
    {
        if (Master(resource) is not { } master)
        {
            yield break;
        }

        yield return new ExternalEvent(path, master.Version(), Modified(master), ToData(master, zone, withRecurrence: true));
        foreach (var occurrence in Overrides(resource))
        {
            if (OriginalDate(occurrence, zone) is not { } date)
            {
                continue;
            }

            var cancelled = string.Equals(occurrence.Property("STATUS")?.Value, "CANCELLED", StringComparison.OrdinalIgnoreCase);
            yield return new ExternalEvent($"{path}#{Day(date)}", occurrence.Version(), Modified(occurrence),
                cancelled ? null : ToData(occurrence, zone, withRecurrence: false), SeriesId: path, OriginalDate: date);
        }
    }

    private static ExternalEventData ToData(IcsComponent e, TimeZoneInfo zone, bool withRecurrence)
    {
        var title = IcsValues.Text(e.Property("SUMMARY")) ?? "";
        var description = IcsValues.Text(e.Property("DESCRIPTION"));
        var location = IcsValues.Text(e.Property("LOCATION"));
        var start = e.Property("DTSTART");
        var end = e.Property("DTEND");
        var duration = IcsValues.Duration(e.Property("DURATION")?.Value);

        DateTimeOffset? startsAt = null, endsAt = null;
        DateOnly? startDate = null, endDate = null;
        if (start is not null && IcsValues.IsDate(start))
        {
            startDate = IcsValues.Date(start.Value);
            var exclusiveEnd = end is not null ? IcsValues.Date(end.Value)
                : duration is { } d && startDate is { } s ? s.AddDays(Math.Max(1, (int)d.TotalDays)) : startDate?.AddDays(1);
            endDate = exclusiveEnd is { } x && startDate is { } first && x > first ? x.AddDays(-1) : startDate;
        }
        else if (start is not null)
        {
            startsAt = IcsValues.Instant(start, zone);
            endsAt = end is not null ? IcsValues.Instant(end, zone) : startsAt + (duration ?? TimeSpan.Zero);
            if (endsAt < startsAt)
            {
                endsAt = startsAt;
            }
        }

        if (startsAt is null && startDate is null)
        {
            // Sem início não é um evento que o Kubico consiga mostrar.
            return new ExternalEventData(title, description, location, null, null, null, null, UnsupportedRecurrence: true);
        }

        string? rule = null;
        List<DateOnly> cancelled = [];
        var unsupported = false;
        if (withRecurrence)
        {
            var lines = e.Lines.Where(l => IcsProperty.Parse(l).Name is "RRULE" or "EXDATE" or "RDATE" or "EXRULE");
            var firstDate = startDate ?? ProviderHttp.LocalDate(startsAt!.Value, zone);
            (rule, cancelled, unsupported) = IcsRecurrence.FromLines(lines, firstDate, zone);
        }

        return new ExternalEventData(title, description, location, startsAt, endsAt, startDate, endDate, rule,
            cancelled, unsupported);
    }

    private static DateTimeOffset? Modified(IcsComponent e) =>
        e.Property("LAST-MODIFIED") is { } modified ? IcsValues.Instant(modified, TimeZoneInfo.Utc) : null;

    private static IcsComponent? Master(IcsComponent resource) =>
        resource.Events.FirstOrDefault(e => e.Property("RECURRENCE-ID") is null);

    private static IEnumerable<IcsComponent> Overrides(IcsComponent resource) =>
        resource.Events.Where(e => e.Property("RECURRENCE-ID") is not null);

    private static DateOnly? OriginalDate(IcsComponent occurrence, TimeZoneInfo zone) =>
        occurrence.Property("RECURRENCE-ID") is { } id ? IcsValues.LocalDate(id, zone) : null;

    private static string Uid(IcsComponent e) => e.Property("UID")?.Value ?? $"{Guid.NewGuid():N}@kubico";

    // --- Escrita ------------------------------------------------------------

    /// <summary>Um VEVENT do Kubico: com hora no fuso da casa (TZID), ou de dia inteiro.</summary>
    private static IcsComponent Event(
        string uid, ExternalEventData data, TimeZoneInfo zone, string? recurrenceId, DateTimeOffset now)
    {
        var tzid = TimeZoneIds.Iana(zone);
        var e = new IcsComponent("VEVENT");
        e.Lines.Add($"UID:{uid}");
        e.Lines.Add($"DTSTAMP:{now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}");
        if (data.IsAllDay)
        {
            e.Lines.Add($"DTSTART;VALUE=DATE:{Day(data.StartDate!.Value)}");
            e.Lines.Add($"DTEND;VALUE=DATE:{Day(data.EndDate!.Value.AddDays(1))}");
        }
        else
        {
            e.Lines.Add(Timed("DTSTART", data.StartsAt!.Value, zone, tzid));
            e.Lines.Add(Timed("DTEND", data.EndsAt!.Value, zone, tzid));
        }

        if (recurrenceId is not null)
        {
            e.Lines.Add(recurrenceId);
        }
        else if (IcsRecurrence.ToLines(data, zone, tzid) is { } recurrence)
        {
            e.Lines.AddRange(recurrence);
        }

        e.Lines.Add($"SUMMARY:{IcsWriter.Text(data.Title)}");
        if (data.Location is { Length: > 0 } location)
        {
            e.Lines.Add($"LOCATION:{IcsWriter.Text(location)}");
        }

        if (data.Description is { Length: > 0 } description)
        {
            e.Lines.Add($"DESCRIPTION:{IcsWriter.Text(description)}");
        }

        return e;
    }

    private static string Timed(string property, DateTimeOffset instant, TimeZoneInfo zone, string tzid) =>
        tzid == "UTC"
            ? $"{property}:{instant.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}"
            : $"{property};TZID={tzid}:{TimeZoneInfo.ConvertTime(instant, zone).DateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}";

    /// <summary>Um dia de uma série (RECURRENCE-ID, EXDATE), no formato do início da série: dia, hora local ou UTC.</summary>
    private static string OccurrenceProperty(string property, IcsComponent master, DateOnly date, TimeZoneInfo zone)
    {
        var start = master.Property("DTSTART")!;
        if (IcsValues.IsDate(start))
        {
            return $"{property};VALUE=DATE:{Day(date)}";
        }

        var value = start.Value.Trim();
        var time = value.Length >= 15 ? value[9..15] : "000000";
        if (start.Parameter("TZID") is { } tzid)
        {
            return $"{property};TZID={tzid}:{Day(date)}T{time}";
        }

        if (value.EndsWith('Z') && IcsValues.Instant(start, zone) is { } first)
        {
            // A série está em UTC: o dia, à hora local da série, convertido para UTC.
            var local = TimeZoneInfo.ConvertTime(first, zone).DateTime;
            var instant = ZonedTime.ToInstant(date.ToDateTime(TimeOnly.FromDateTime(local)), zone);
            return $"{property}:{instant.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
        }

        return $"{property}:{Day(date)}T{time}";
    }

    private async Task PutAsync(ExternalCalendar calendar, Uri url, IReadOnlyList<IcsComponent> events, bool create, CancellationToken ct)
    {
        var root = new IcsComponent("VCALENDAR");
        root.Lines.AddRange(["VERSION:2.0", "PRODID:-//Kubico//Calendario partilhado//PT", "CALSCALE:GREGORIAN"]);
        var tzids = events.SelectMany(e => e.Properties).Select(p => p.Parameter("TZID")).OfType<string>().Distinct();
        foreach (var tzid in tzids)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out var zone))
            {
                // O texto do VTIMEZONE não tem VCALENDAR à volta: os componentes lidos são o próprio VTIMEZONE.
                var text = string.Join("\r\n", IcsWriter.TimeZoneComponent(zone, tzid, clock.GetUtcNow().Year));
                root.Children.AddRange(IcsComponent.Parse(text).Children);
            }
        }

        root.Children.AddRange(events);
        using var _ = await SendAsync(calendar.AccessToken, "PUT", url,
            new Body(root.Write(), "text/calendar"), ct, configure: request =>
            {
                if (create)
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                }
            });
    }

    private async Task<IcsComponent?> GetAsync(ExternalCalendar calendar, Uri url, CancellationToken ct)
    {
        using var response = await SendAsync(calendar.AccessToken, "GET", url, null, ct, allowMissing: true);
        return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
            ? null
            : IcsComponent.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    // --- CalDAV ---------------------------------------------------------------

    private async Task<Uri> PrincipalAsync(string credentials, CancellationToken ct)
    {
        var server = new Uri(options.Value.Apple.ServerUrl);
        var href = await PropertyHrefAsync(credentials, server, Dav + "current-user-principal", ct);
        return new Uri(server, href);
    }

    private async Task<Uri> HomeAsync(string credentials, Uri principal, CancellationToken ct)
    {
        var home = new Uri(principal, await PropertyHrefAsync(credentials, principal, CalDav + "calendar-home-set", ct));
        return home.AbsoluteUri.EndsWith('/') ? home : new Uri(home.AbsoluteUri + "/");
    }

    private async Task<string> PropertyHrefAsync(string credentials, Uri url, XName property, CancellationToken ct)
    {
        var body = new XElement(Dav + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", CalDav.NamespaceName),
            new XElement(Dav + "prop", new XElement(property)));
        using var response = await SendAsync(credentials, "PROPFIND", url, Xml(body), ct, depth: 0);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.Descendants(property).Elements(Dav + "href").FirstOrDefault()?.Value.Trim() is { Length: > 0 } href
            ? href
            : throw new CalendarProviderException($"O iCloud não indicou {property.LocalName}.");
    }

    private sealed record Body(string Text, string MediaType);

    private static Body Xml(XElement element) =>
        new(new XDeclaration("1.0", "utf-8", null) + Environment.NewLine + element.ToString(SaveOptions.DisableFormatting), "application/xml");

    /// <summary>
    /// Um pedido autenticado. Os redirecionamentos seguem-se aqui (o HttpClient não volta a enviar a autenticação),
    /// e a falta do recurso só é erro quando não se espera.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        string credentials,
        string method,
        Uri url,
        Body? body,
        CancellationToken ct,
        int? depth = null,
        bool allowMissing = false,
        Action<HttpRequestMessage>? configure = null)
    {
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            if (depth is { } value)
            {
                request.Headers.Add("Depth", value.ToString(CultureInfo.InvariantCulture));
            }

            if (body is not null)
            {
                request.Content = new StringContent(body.Text, Encoding.UTF8, body.MediaType);
            }

            configure?.Invoke(request);
            var response = await http.SendAsync(request, ct);
            if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is { } location && redirects < 3)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                response.Dispose();
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new CalendarAuthException("O iCloud recusou o Apple ID ou a palavra-passe da app.");
            }

            if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return response;
            }

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                var status = response.StatusCode;
                response.Dispose();
                throw new CalendarProviderException(
                    $"O iCloud respondeu {(int)status} a {method} {url}: {(text.Length > 300 ? text[..300] : text)}", status);
            }

            return response;
        }
    }

    /// <summary>Apple ID e palavra-passe da app, como vão no cabeçalho Authorization (Basic).</summary>
    private static string Credentials(string account, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account}:{password}"));

    private static (string Path, DateOnly? Date) Split(string eventId)
    {
        var hash = eventId.LastIndexOf('#');
        return hash < 0 ? (eventId, null) : (eventId[..hash], IcsValues.Date(eventId[(hash + 1)..]));
    }

    private static string Day(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
