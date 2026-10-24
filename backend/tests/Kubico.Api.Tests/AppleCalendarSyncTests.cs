using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Calendar;
using Kubico.Api.Calendar.Sync;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Tests;

/// <summary>A API com o iCloud de verdade no código (CalDAV) e um servidor CalDAV falso, em memória.</summary>
public sealed class AppleCalendarFixture : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    public AppleCalendarFixture()
    {
        App = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICalendarProvider>();
            s.AddSingleton<ICalendarProvider>(sp => new AppleCalendarProvider(
                new HttpClient(Server),
                Options.Create(new CalendarSyncOptions { Apple = { ServerUrl = FakeCalDavServer.Root.AbsoluteUri } }),
                sp.GetRequiredService<TimeProvider>()));
        }));
    }

    public FakeCalDavServer Server { get; } = new();

    /// <summary>Um minuto depois: cada alteração fica com a sua hora.</summary>
    public void Tick() => _factory.Clock.Advance(TimeSpan.FromMinutes(1));

    public WebApplicationFactory<Program> App { get; }

    public async Task<(HttpClient Client, AuthResponse Auth)> RegisterAsync(string name)
    {
        var client = App.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest($"{Guid.NewGuid():N}@kubico.test", "palavra-passe-segura", name));
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        return (client, auth);
    }

    public async Task<T> Query<T>(Func<KubicoDbContext, T> query)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return query(scope.ServiceProvider.GetRequiredService<KubicoDbContext>());
    }

    public void Dispose()
    {
        App.Dispose();
        _factory.Dispose();
    }
}

public class AppleCalendarSyncTests(AppleCalendarFixture fixture) : IClassFixture<AppleCalendarFixture>
{
    private FakeCalDavServer Server => fixture.Server;

    [Fact]
    public async Task The_apple_id_and_an_app_password_connect_icloud_and_a_wrong_password_is_explained()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var household = await HouseholdTests.CreateHousehold(admin, name);

        var wrong = await admin.PostAsJsonAsync($"/api/households/{household.Id}/calendar-sync/apple",
            new AppleSignInRequest(FakeCalDavServer.Account, "palavra-passe-normal"));
        var overviewBefore = await Overview(admin, household.Id);
        var connection = await ConnectAsync(admin, household.Id);
        var oauth = await admin.PostAsync($"/api/households/{household.Id}/calendar-sync/apple/authorize", null);
        var stored = await fixture.Query(db => db.CalendarConnections.Single(c => c.HouseholdId == household.Id).ProtectedRefreshToken);

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.StartsWith("O Apple ID ou a palavra-passe da app não estão certos.",
            (await wrong.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options))!.Title);
        Assert.Contains(overviewBefore.Providers, p => p.Provider == CalendarProvider.Apple && p.Available);
        Assert.Equal((CalendarProvider.Apple, FakeCalDavServer.Account), (connection.Provider, connection.AccountEmail));
        Assert.Equal(HttpStatusCode.NotFound, oauth.StatusCode);
        Assert.StartsWith("/123/calendars/", Server.CalendarNamed($"Kubico · {name}").Path);
        Assert.DoesNotContain(FakeCalDavServer.Password, stored);
    }

    [Fact]
    public async Task Household_events_go_to_icloud_in_the_household_time_zone_and_changes_there_come_back()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var calendarName = $"Kubico · {name}";
        var household = await HouseholdTests.CreateHousehold(admin, name);
        var swim = await CreateEvent(admin, household.Id, Timed("Natação", 2026, 10, 20, 17) with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" });
        await CreateEvent(admin, household.Id, new SaveEventRequest(
            "Férias", IsAllDay: true, StartDate: new DateOnly(2026, 8, 1), EndDate: new DateOnly(2026, 8, 15)));
        await CreateEvent(admin, household.Id, Timed("Dentista", 2026, 11, 3, 9));

        await ConnectAsync(admin, household.Id);
        var connection = await SyncAsync(admin, household.Id);
        var series = Server.Resource(calendarName, "SUMMARY:Natação");
        var holidays = Server.Resource(calendarName, "SUMMARY:Férias").Value;

        Assert.Null(connection.LastError);
        Assert.Equal(3, Server.CalendarNamed(calendarName).Resources.Count);
        Assert.Contains("DTSTART;TZID=Europe/Lisbon:20261020T180000", series.Value);
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=TU", series.Value);
        Assert.Contains("CALSCALE:GREGORIAN\r\nBEGIN:VTIMEZONE\r\nTZID:Europe/Lisbon", series.Value);
        Assert.Contains("DTSTART;VALUE=DATE:20260801", holidays);
        Assert.Contains("DTEND;VALUE=DATE:20260816", holidays);

        // No iPhone: muda-se o nome da natação, apaga-se o dentista e cria-se um evento novo.
        fixture.Tick();
        Server.Edit(calendarName, series.Key, ics => ics.Replace("SUMMARY:Natação", "SUMMARY:Natação (piscina nova)"));
        Server.Remove(calendarName, Server.Resource(calendarName, "SUMMARY:Dentista").Key);
        Server.Add(calendarName, "do-iphone.ics", """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Apple Inc.//iPhone OS 26.0//EN
            BEGIN:VEVENT
            UID:5F1C2B1E-IPHONE
            DTSTAMP:20261021T090000Z
            DTSTART;TZID=Europe/Lisbon:20261024T100000
            DTEND;TZID=Europe/Lisbon:20261024T113000
            SUMMARY:Feira do livro
            LOCATION:Parque Eduardo VII\, Lisboa
            END:VEVENT
            END:VCALENDAR
            """.Replace("\n", "\r\n"));
        await SyncAsync(admin, household.Id);
        var october = await GetCalendar(admin, household.Id, "2026-10-01", "2026-11-30");

        Assert.Contains(october.Events, e => e.Id == swim.Id && e.Title == "Natação (piscina nova)");
        Assert.DoesNotContain(october.Events, e => e.Title == "Dentista");
        var fair = Assert.Single(october.Events, e => e.Title == "Feira do livro");
        Assert.Equal(("Parque Eduardo VII, Lisboa", new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero)),
            (fair.Location, fair.StartsAt!.Value));
    }

    [Fact]
    public async Task One_day_changed_or_removed_from_a_series_stays_in_the_same_icloud_event()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var calendarName = $"Kubico · {name}";
        var household = await HouseholdTests.CreateHousehold(admin, name);
        var swim = await CreateEvent(admin, household.Id, Timed("Natação", 2026, 10, 20, 17) with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" });
        await ConnectAsync(admin, household.Id);
        await SyncAsync(admin, household.Id);

        // No Kubico: o dia 27 passa para as 19h e o dia 3 de novembro é cancelado.
        fixture.Tick();
        var url = $"/api/households/{household.Id}/events/{swim.Id}/occurrences";
        (await admin.PutAsJsonAsync($"{url}/2026-10-27?scope=this", Timed("Natação (mais tarde)", 2026, 10, 27, 18), TestJson.Options))
            .EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"{url}/2026-11-03?scope=this")).EnsureSuccessStatusCode();
        var connection = await SyncAsync(admin, household.Id);
        var resource = Server.Resource(calendarName, "SUMMARY:Natação");

        Assert.Null(connection.LastError);
        Assert.Single(Server.CalendarNamed(calendarName).Resources);
        Assert.Contains("RECURRENCE-ID;TZID=Europe/Lisbon:20261027T180000", resource.Value);
        Assert.Contains("SUMMARY:Natação (mais tarde)", resource.Value);
        Assert.Contains("EXDATE;TZID=Europe/Lisbon:20261103T180000", resource.Value);

        // No iPhone: o dia 10 de novembro muda de nome (uma ocorrência alterada, no mesmo recurso).
        fixture.Tick();
        Server.Edit(calendarName, resource.Key, ics => ics.Replace("END:VCALENDAR", """
            BEGIN:VEVENT
            UID:X
            RECURRENCE-ID;TZID=Europe/Lisbon:20261110T180000
            DTSTART;TZID=Europe/Lisbon:20261110T180000
            DTEND;TZID=Europe/Lisbon:20261110T190000
            SUMMARY:Natação (aula aberta)
            END:VEVENT
            END:VCALENDAR
            """.Replace("\n", "\r\n")));
        await SyncAsync(admin, household.Id);
        var november = await GetCalendar(admin, household.Id, "2026-10-20", "2026-11-30");

        Assert.Contains(november.Occurrences, o => o.OccurrenceDate == new DateOnly(2026, 10, 27) && Title(november, o) == "Natação (mais tarde)");
        Assert.Contains(november.Occurrences, o => o.OccurrenceDate == new DateOnly(2026, 11, 10) && Title(november, o) == "Natação (aula aberta)");
        Assert.DoesNotContain(november.Occurrences, o => o.OccurrenceDate == new DateOnly(2026, 11, 3));
    }

    [Fact]
    public async Task Disconnecting_deletes_the_kubico_calendar_in_icloud()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var household = await HouseholdTests.CreateHousehold(admin, name);
        var connection = await ConnectAsync(admin, household.Id);
        Assert.Contains(Server.Calendars.Values, c => c.Name == $"Kubico · {name}");

        var response = await admin.DeleteAsync($"/api/households/{household.Id}/calendar-sync/{connection.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(Server.Calendars.Values, c => c.Name == $"Kubico · {name}");
    }

    [Fact]
    public async Task Events_made_on_apple_devices_are_read_with_their_variations()
    {
        var server = new FakeCalDavServer();
        server.Calendars["/123/calendars/kubico/"] = ("Kubico", new Dictionary<string, string>
        {
            // Dia inteiro sem DTEND, título comprido (linha dobrada) com vírgulas escapadas.
            ["/123/calendars/kubico/aniversario.ics"] = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART;VALUE=DATE:20261105\r\n"
                + "SUMMARY:Aniversário da avó Rosa\\, com bolo\\, velas e a família toda reunida em c\r\n asa\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n",
            // Hora em UTC e duração em vez de DTEND.
            ["/123/calendars/kubico/reuniao.ics"] = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:b\r\nDTSTART:20261106T093000Z\r\n"
                + "DURATION:PT1H30M\r\nSUMMARY:Reunião da escola\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n",
            // Série semanal até um instante UTC, com um dia cancelado (STATUS:CANCELLED).
            ["/123/calendars/kubico/ginastica.ics"] = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:c\r\n"
                + "DTSTART;TZID=Europe/Lisbon:20261007T180000\r\nDTEND;TZID=Europe/Lisbon:20261007T190000\r\n"
                + "RRULE:FREQ=WEEKLY;UNTIL=20261216T235959Z\r\nSUMMARY:Ginástica\r\nEND:VEVENT\r\n"
                + "BEGIN:VEVENT\r\nUID:c\r\nRECURRENCE-ID;TZID=Europe/Lisbon:20261014T180000\r\nSTATUS:CANCELLED\r\n"
                + "DTSTART;TZID=Europe/Lisbon:20261014T180000\r\nDTEND;TZID=Europe/Lisbon:20261014T190000\r\nSUMMARY:Ginástica\r\n"
                + "END:VEVENT\r\nEND:VCALENDAR\r\n",
        });
        var lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{FakeCalDavServer.Account}:{FakeCalDavServer.Password}"));
        var provider = new AppleCalendarProvider(new HttpClient(server),
            Options.Create(new CalendarSyncOptions { Apple = { ServerUrl = FakeCalDavServer.Root.AbsoluteUri } }), TimeProvider.System);

        var changes = await provider.ListChangesAsync(
            new ExternalCalendar(credentials, "https://p01-caldav.test/123/calendars/kubico/", lisbon), null, default);

        var birthday = changes.Events.Single(e => e.Id.EndsWith("aniversario.ics", StringComparison.Ordinal)).Data!;
        var meeting = changes.Events.Single(e => e.Id.EndsWith("reuniao.ics", StringComparison.Ordinal)).Data!;
        var gym = changes.Events.Single(e => e.Id.EndsWith("ginastica.ics", StringComparison.Ordinal)).Data!;
        var cancelled = changes.Events.Single(e => e.IsOccurrence);
        Assert.True(changes.IsFullListing);
        Assert.Equal("Aniversário da avó Rosa, com bolo, velas e a família toda reunida em casa", birthday.Title);
        Assert.Equal((new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 5)), (birthday.StartDate!.Value, birthday.EndDate!.Value));
        Assert.Equal((new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 6, 11, 0, 0, TimeSpan.Zero)),
            (meeting.StartsAt!.Value, meeting.EndsAt!.Value));
        Assert.Equal("FREQ=WEEKLY;UNTIL=20261216", gym.Recurrence);
        Assert.Equal((new DateOnly(2026, 10, 14), true), (cancelled.OriginalDate!.Value, cancelled.IsDeleted));
        Assert.EndsWith("ginastica.ics", cancelled.SeriesId, StringComparison.Ordinal);
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    private static SaveEventRequest Timed(string title, int year, int month, int day, int utcHour) => new(
        title,
        StartsAt: new DateTimeOffset(year, month, day, utcHour, 0, 0, TimeSpan.Zero),
        EndsAt: new DateTimeOffset(year, month, day, utcHour + 1, 0, 0, TimeSpan.Zero));

    private static string? Title(CalendarDto calendar, CalendarOccurrenceDto occurrence) =>
        calendar.Events.SingleOrDefault(e => e.Id == occurrence.EventId)?.Title;

    private static async Task<CalendarConnectionDto> ConnectAsync(HttpClient client, Guid householdId)
    {
        var response = await client.PostAsJsonAsync($"/api/households/{householdId}/calendar-sync/apple",
            new AppleSignInRequest(FakeCalDavServer.Account, FakeCalDavServer.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CalendarConnectionDto>(TestJson.Options))!;
    }

    private static async Task<CalendarSyncDto> Overview(HttpClient client, Guid householdId) =>
        (await client.GetFromJsonAsync<CalendarSyncDto>($"/api/households/{householdId}/calendar-sync", TestJson.Options))!;

    private static async Task<CalendarConnectionDto> SyncAsync(HttpClient client, Guid householdId)
    {
        var connection = Assert.Single((await Overview(client, householdId)).Connections);
        var response = await client.PostAsync($"/api/households/{householdId}/calendar-sync/{connection.Id}/sync", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CalendarConnectionDto>(TestJson.Options))!;
    }

    private static async Task<CalendarEventDto> CreateEvent(HttpClient client, Guid householdId, SaveEventRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/households/{householdId}/events", request, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CalendarEventDto>(TestJson.Options))!;
    }

    private static async Task<CalendarDto> GetCalendar(HttpClient client, Guid householdId, string from, string to) =>
        (await client.GetFromJsonAsync<CalendarDto>($"/api/households/{householdId}/events?from={from}&to={to}", TestJson.Options))!;
}
