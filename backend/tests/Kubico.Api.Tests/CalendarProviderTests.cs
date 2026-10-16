using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using Kubico.Api.Calendar.Sync;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Tests;

/// <summary>O que o Kubico envia às APIs do Google Calendar e do Microsoft Graph, e como lê as respostas.</summary>
public class CalendarProviderTests
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    private static readonly ExternalCalendar GoogleCalendar = new("access", "kubico@group.calendar.google.com", Lisbon);

    private static readonly ExternalCalendar OutlookCalendar = new("access", "AAMk-kubico", Lisbon);

    [Fact]
    public void Google_asks_only_for_the_calendars_kubico_creates_with_pkce()
    {
        var url = new Uri(Google(new FakeHandler(_ => Json("{}"))).AuthorizationUrl(
            "http://localhost/api/calendar-sync/callback/google", "estado", "desafio"));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query);

        Assert.Equal("accounts.google.com", url.Host);
        Assert.Equal("openid email https://www.googleapis.com/auth/calendar.app.created", query["scope"]);
        Assert.Equal(("offline", "consent"), (query["access_type"].ToString(), query["prompt"].ToString()));
        Assert.Equal(("desafio", "S256", "estado"), (query["code_challenge"].ToString(), query["code_challenge_method"].ToString(), query["state"].ToString()));
    }

    [Fact]
    public async Task Signing_in_reads_the_account_email_and_a_revoked_grant_asks_to_reconnect()
    {
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"email":"ana@example.com"}"""));
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? Json($$"""{"access_token":"a","refresh_token":"r","id_token":"h.{{payload}}.s","expires_in":3599}""")
            : Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));
        var google = Google(handler);

        var tokens = await google.ExchangeCodeAsync("codigo", "http://localhost/cb", "verificador", default);
        var form = handler.Requests[0].Body;

        Assert.Equal(new OAuthTokens("a", "r", "ana@example.com"), tokens);
        Assert.Contains("code_verifier=verificador", form);
        Assert.Contains("grant_type=authorization_code", form);
        await Assert.ThrowsAsync<CalendarAuthException>(() => google.RefreshAsync("r", default));
    }

    [Fact]
    public async Task Google_events_keep_the_local_time_and_the_days_taken_out_of_the_series()
    {
        var handler = new FakeHandler(_ => Json("""{"id":"novo","etag":"\"1\""}"""));
        var google = Google(handler);

        var series = await google.CreateEventAsync(GoogleCalendar, new ExternalEventData(
            "Natação", "Levar touca", null,
            new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero),
            null, null, "FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231", [new DateOnly(2026, 10, 27)]), default);
        await google.CreateEventAsync(GoogleCalendar, new ExternalEventData(
            "Férias", null, null, null, null, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15)), default);

        var timed = handler.Requests[0].Json;
        var allDay = handler.Requests[1].Json;
        Assert.Equal(new ExternalRef("novo", "\"1\""), series);
        Assert.Equal("https://www.googleapis.com/calendar/v3/calendars/kubico%40group.calendar.google.com/events", handler.Requests[0].Uri);
        Assert.Equal(("2026-10-20T18:00:00", "Europe/Lisbon"), (timed.GetProperty("start").GetProperty("dateTime").GetString(), timed.GetProperty("start").GetProperty("timeZone").GetString()));
        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231T235959Z", "EXDATE;TZID=Europe/Lisbon:20261027T180000"],
            timed.GetProperty("recurrence").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(("2026-08-01", "2026-08-16"), (allDay.GetProperty("start").GetProperty("date").GetString(), allDay.GetProperty("end").GetProperty("date").GetString()));
        Assert.Equal(JsonValueKind.Null, allDay.GetProperty("recurrence").ValueKind);
    }

    [Fact]
    public async Task Google_changes_come_with_the_sync_token_and_a_stale_token_reads_everything_again()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Query.Contains("syncToken=antigo")
            ? Json("{}", HttpStatusCode.Gone)
            : Json("""
                {
                  "items": [
                    { "id": "ferias", "etag": "\"1\"", "status": "confirmed", "summary": "Férias",
                      "start": { "date": "2026-08-01" }, "end": { "date": "2026-08-16" } },
                    { "id": "natacao", "etag": "\"2\"", "status": "confirmed", "summary": "Natação",
                      "updated": "2026-10-16T10:00:00.000Z",
                      "start": { "dateTime": "2026-10-20T18:00:00+01:00", "timeZone": "Europe/Lisbon" },
                      "end": { "dateTime": "2026-10-20T19:00:00+01:00", "timeZone": "Europe/Lisbon" },
                      "recurrence": [ "RRULE:FREQ=WEEKLY;WKST=SU;BYDAY=TU;UNTIL=20261231T235959Z",
                                      "EXDATE;TZID=Europe/Lisbon:20261027T180000" ] },
                    { "id": "natacao_20261103T180000Z", "status": "cancelled", "recurringEventId": "natacao",
                      "originalStartTime": { "dateTime": "2026-11-03T18:00:00Z" } },
                    { "id": "reuniao", "status": "confirmed", "summary": "Reunião",
                      "start": { "dateTime": "2026-10-13T18:00:00+01:00" }, "end": { "dateTime": "2026-10-13T19:00:00+01:00" },
                      "recurrence": [ "RRULE:FREQ=MONTHLY;BYDAY=2TU" ] }
                  ],
                  "nextSyncToken": "novo"
                }
                """));

        var changes = await Google(handler).ListChangesAsync(GoogleCalendar, "antigo", default);

        var byId = changes.Events.ToDictionary(e => e.Id);
        Assert.Equal((true, "novo"), (changes.IsFullListing, changes.Cursor));
        Assert.Contains("showDeleted=false", handler.Requests[1].Uri);
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15)), (byId["ferias"].Data!.StartDate!.Value, byId["ferias"].Data!.EndDate!.Value));
        Assert.Equal("FREQ=WEEKLY;UNTIL=20261231", byId["natacao"].Data!.Recurrence);
        Assert.Equal([new DateOnly(2026, 10, 27)], byId["natacao"].Data!.Cancelled);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero), byId["natacao"].Data!.StartsAt);
        Assert.Equal((true, "natacao", new DateOnly(2026, 11, 3)),
            (byId["natacao_20261103T180000Z"].IsDeleted, byId["natacao_20261103T180000Z"].SeriesId, byId["natacao_20261103T180000Z"].OriginalDate!.Value));
        Assert.True(byId["reuniao"].Data!.UnsupportedRecurrence);
    }

    [Fact]
    public async Task Google_changes_one_day_by_patching_that_occurrence()
    {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? Json("""
                { "items": [
                  { "id": "natacao_20261020T170000Z", "originalStartTime": { "dateTime": "2026-10-20T18:00:00+01:00" } },
                  { "id": "natacao_20261027T180000Z", "originalStartTime": { "dateTime": "2026-10-27T18:00:00Z" } } ] }
                """)
            : Json("""{"id":"natacao_20261027T180000Z","etag":"\"9\""}"""));

        var copy = await Google(handler).UpdateOccurrenceAsync(GoogleCalendar, "natacao", new DateOnly(2026, 10, 27),
            new ExternalEventData("Natação (piscina nova)", null, null,
                new DateTimeOffset(2026, 10, 27, 19, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero), null, null), default);

        var patch = handler.Requests[1];
        Assert.Equal(new ExternalRef("natacao_20261027T180000Z", "\"9\""), copy);
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.EndsWith("/events/natacao_20261027T180000Z", patch.Uri);
        Assert.Equal("confirmed", patch.Json.GetProperty("status").GetString());
        Assert.False(patch.Json.TryGetProperty("recurrence", out _));
    }

    [Fact]
    public async Task Outlook_series_are_a_pattern_and_the_days_taken_out_are_deleted_one_by_one()
    {
        var handler = new FakeHandler(request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("POST", _) => Json("""{"id":"serie","changeKey":"1"}"""),
            ("GET", var path) when path.EndsWith("/instances", StringComparison.Ordinal) => Json("""
                { "value": [ { "id": "dia20", "originalStart": "2026-10-20T17:00:00Z" },
                             { "id": "dia27", "originalStart": "2026-10-27T18:00:00Z" } ] }
                """),
            ("DELETE", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => Json("""{"id":"serie","changeKey":"2"}"""),
        });

        var copy = await Outlook(handler).CreateEventAsync(OutlookCalendar, new ExternalEventData(
            "Natação", null, "Piscina municipal",
            new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero),
            null, null, "FREQ=WEEKLY;BYDAY=TU;COUNT=10", [new DateOnly(2026, 10, 27)]), default);

        var body = handler.Requests[0].Json;
        var recurrence = body.GetProperty("recurrence");
        Assert.Equal(new ExternalRef("serie", "2"), copy);
        Assert.Equal(("2026-10-20T18:00:00", "GMT Standard Time"), (body.GetProperty("start").GetProperty("dateTime").GetString(), body.GetProperty("start").GetProperty("timeZone").GetString()));
        Assert.Equal("Piscina municipal", body.GetProperty("location").GetProperty("displayName").GetString());
        Assert.Equal("weekly", recurrence.GetProperty("pattern").GetProperty("type").GetString());
        Assert.Equal(["tuesday"], recurrence.GetProperty("pattern").GetProperty("daysOfWeek").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(("numbered", 10, "2026-10-20"), (recurrence.GetProperty("range").GetProperty("type").GetString(),
            recurrence.GetProperty("range").GetProperty("numberOfOccurrences").GetInt32(), recurrence.GetProperty("range").GetProperty("startDate").GetString()));
        Assert.Equal(["/v1.0/me/events/dia27"], handler.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => new Uri(r.Uri).AbsolutePath));
        Assert.All(handler.Requests, r => Assert.Contains("outlook.timezone=\"UTC\"", r.Prefer));
    }

    [Fact]
    public async Task Outlook_is_read_in_utc_page_by_page_and_relative_patterns_are_not_supported()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Query.Contains("pagina2")
            ? Json("""
                { "value": [ { "id": "jantar", "changeKey": "3", "type": "singleInstance", "subject": "Jantar",
                  "body": { "contentType": "text", "content": "Levar vinho\r\n\r\n— Kubico —\r\nParticipantes: Ana" },
                  "start": { "dateTime": "2026-10-24T19:00:00.0000000", "timeZone": "UTC" },
                  "end": { "dateTime": "2026-10-24T21:00:00.0000000", "timeZone": "UTC" } } ] }
                """)
            : Json("""
                { "value": [
                    { "id": "ferias", "changeKey": "1", "type": "singleInstance", "subject": "Férias", "isAllDay": true,
                      "start": { "dateTime": "2026-08-01T00:00:00.0000000", "timeZone": "UTC" },
                      "end": { "dateTime": "2026-08-16T00:00:00.0000000", "timeZone": "UTC" } },
                    { "id": "natacao", "changeKey": "2", "type": "seriesMaster", "subject": "Natação",
                      "start": { "dateTime": "2026-10-20T17:00:00.0000000", "timeZone": "UTC" },
                      "end": { "dateTime": "2026-10-20T18:00:00.0000000", "timeZone": "UTC" },
                      "recurrence": { "pattern": { "type": "weekly", "interval": 2, "daysOfWeek": [ "tuesday", "thursday" ] },
                                      "range": { "type": "endDate", "startDate": "2026-10-20", "endDate": "2026-12-31" } } },
                    { "id": "reuniao", "changeKey": "4", "type": "seriesMaster", "subject": "Reunião",
                      "start": { "dateTime": "2026-10-13T17:00:00.0000000", "timeZone": "UTC" },
                      "end": { "dateTime": "2026-10-13T18:00:00.0000000", "timeZone": "UTC" },
                      "recurrence": { "pattern": { "type": "relativeMonthly", "interval": 1, "daysOfWeek": [ "tuesday" ], "index": "second" },
                                      "range": { "type": "noEnd", "startDate": "2026-10-13" } } },
                    { "id": "ocorrencia", "type": "occurrence" }
                  ],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/calendars/AAMk-kubico/events?pagina2" }
                """));

        var changes = await Outlook(handler).ListChangesAsync(OutlookCalendar, null, default);

        var byId = changes.Events.ToDictionary(e => e.Id);
        Assert.True(changes.IsFullListing);
        Assert.Equal(["ferias", "natacao", "reuniao", "jantar"], changes.Events.Select(e => e.Id));
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15)), (byId["ferias"].Data!.StartDate!.Value, byId["ferias"].Data!.EndDate!.Value));
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH;UNTIL=20261231", byId["natacao"].Data!.Recurrence);
        Assert.True(byId["reuniao"].Data!.UnsupportedRecurrence);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 19, 0, 0, TimeSpan.Zero), byId["jantar"].Data!.StartsAt);
        Assert.Equal("Levar vinho", CalendarSyncService.Notes(byId["jantar"].Data!.Description));
    }

    [Fact]
    public async Task Outlook_only_sends_the_recurrence_of_a_series_when_it_changed()
    {
        const string current = """
            { "id": "natacao", "changeKey": "2", "type": "seriesMaster", "subject": "Natação",
              "start": { "dateTime": "2026-10-20T17:00:00.0000000", "timeZone": "UTC" },
              "end": { "dateTime": "2026-10-20T18:00:00.0000000", "timeZone": "UTC" },
              "recurrence": { "pattern": { "type": "weekly", "interval": 1, "daysOfWeek": [ "tuesday" ] },
                              "range": { "type": "noEnd", "startDate": "2026-10-20" } } }
            """;
        var handler = new FakeHandler(request => Json(request.Method == HttpMethod.Get ? current : """{"id":"natacao","changeKey":"3"}"""));
        var outlook = Outlook(handler);
        var series = new ExternalEventData("Natação (novo título)", null, null,
            new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero),
            null, null, "FREQ=WEEKLY");

        await outlook.UpdateEventAsync(OutlookCalendar, "natacao", series, isOccurrence: false, default);
        await outlook.UpdateEventAsync(OutlookCalendar, "natacao", series with { Recurrence = "FREQ=DAILY" }, isOccurrence: false, default);

        var patches = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.False(patches[0].Json.TryGetProperty("recurrence", out _));
        Assert.Equal("daily", patches[1].Json.GetProperty("recurrence").GetProperty("pattern").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Microsoft_hands_out_a_new_refresh_token_each_time()
    {
        var handler = new FakeHandler(_ => Json("""{"access_token":"a","refresh_token":"novo"}"""));

        var tokens = await Outlook(handler).RefreshAsync("antigo", default);

        Assert.Equal("novo", tokens.RefreshToken);
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", handler.Requests[0].Uri);
        Assert.Contains("scope=offline_access+openid+email+Calendars.ReadWrite", handler.Requests[0].Body);
    }

    private static GoogleCalendarProvider Google(FakeHandler handler) =>
        new(new HttpClient(handler), Options.Create(new CalendarSyncOptions { Google = { ClientId = "id", ClientSecret = "segredo" } }));

    private static MicrosoftCalendarProvider Outlook(FakeHandler handler) =>
        new(new HttpClient(handler), Options.Create(new CalendarSyncOptions { Microsoft = { ClientId = "id", ClientSecret = "segredo" } }));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string Body, string Prefer)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var prefer = request.Headers.TryGetValues("Prefer", out var values) ? string.Join(", ", values) : "";
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsoluteUri, body, prefer));
            return respond(request);
        }
    }
}
