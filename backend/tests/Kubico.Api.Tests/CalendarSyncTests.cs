using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Calendar;
using Kubico.Api.Calendar.Sync;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kubico.Api.Tests;

/// <summary>A API com um Google Calendar e um Outlook falsos, em memória.</summary>
public sealed class CalendarSyncFixture : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    public CalendarSyncFixture()
    {
        Google = new FakeCalendarProvider(CalendarProvider.Google, fullListing: false, () => Clock.GetUtcNow());
        Outlook = new FakeCalendarProvider(CalendarProvider.Microsoft, fullListing: true, () => Clock.GetUtcNow());
        App = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICalendarProvider>();
            s.AddSingleton<ICalendarProvider>(Google);
            s.AddSingleton<ICalendarProvider>(Outlook);
        }));
    }

    public FakeCalendarProvider Google { get; }

    public FakeCalendarProvider Outlook { get; }

    public WebApplicationFactory<Program> App { get; }

    public FakeTimeProvider Clock => _factory.Clock;

    /// <summary>Um minuto depois: cada alteração fica com a sua hora.</summary>
    public void Tick() => Clock.Advance(TimeSpan.FromMinutes(1));

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

public class CalendarSyncTests(CalendarSyncFixture fixture) : IClassFixture<CalendarSyncFixture>
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    private FakeCalendarProvider Google => fixture.Google;

    private FakeCalendarProvider Outlook => fixture.Outlook;

    [Fact]
    public async Task Only_adults_connect_and_each_member_sees_only_their_own_connections()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var (child, _) = await fixture.RegisterAsync("Rita");
        var (stranger, _) = await fixture.RegisterAsync("Vizinho");
        var household = await HouseholdTests.CreateHousehold(admin, Unique("Casa"));
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        await ConnectAsync(admin, household.Id, "google", "mae@example.com");

        var adminView = await Overview(admin, household.Id);
        var childView = await Overview(child, household.Id);
        var childTry = await child.PostAsync($"/api/households/{household.Id}/calendar-sync/google/authorize", null);
        var strangerTry = await stranger.GetAsync($"/api/households/{household.Id}/calendar-sync");
        var unknownService = await admin.PostAsync($"/api/households/{household.Id}/calendar-sync/yahoo/authorize", null);

        Assert.True(adminView.CanConnect);
        Assert.Equal([CalendarProvider.Google, CalendarProvider.Microsoft], adminView.Providers.Where(p => p.Available).Select(p => p.Provider));
        Assert.Equal("mae@example.com", Assert.Single(adminView.Connections).AccountEmail);
        Assert.False(childView.CanConnect);
        Assert.Empty(childView.Connections);
        Assert.Equal(HttpStatusCode.Forbidden, childTry.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerTry.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownService.StatusCode);
    }

    [Fact]
    public async Task Connecting_creates_a_kubico_calendar_in_the_account_with_the_household_events()
    {
        var (admin, auth) = await fixture.RegisterAsync("Mãe");
        var (partner, partnerAuth) = await fixture.RegisterAsync("Pai");
        var name = Unique("Casa");
        var household = await HouseholdTests.CreateHousehold(admin, name);
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);
        var otherHousehold = await HouseholdTests.CreateHousehold(admin, Unique("Outra"));
        await CreateEvent(admin, otherHousehold.Id, Timed("Não é desta casa", 2026, 11, 2, 10));
        await CreateEvent(admin, household.Id, Timed("Natação", 2026, 10, 20, 18) with
        {
            Notes = "Levar touca",
            Recurrence = "FREQ=WEEKLY;BYDAY=TU",
            ParticipantUserIds = [auth.User.Id],
            PickUpUserId = partnerAuth.User.Id,
        });
        await CreateEvent(admin, household.Id, new SaveEventRequest(
            "Férias", IsAllDay: true, StartDate: new DateOnly(2026, 8, 1), EndDate: new DateOnly(2026, 8, 15)));

        var page = await ConnectAsync(admin, household.Id, "google", "mae@example.com");
        var connection = await SyncAsync(admin, household.Id);

        var calendar = Google.CalendarNamed($"Kubico · {name}");
        var swim = calendar.Live.Single(e => e.Data.Title == "Natação").Data;
        var holidays = calendar.Live.Single(e => e.Data.Title == "Férias").Data;
        var storedToken = await fixture.Query(db => db.CalendarConnections.Single(c => c.HouseholdId == household.Id).ProtectedRefreshToken);
        Assert.Contains("Calendário ligado", page);
        Assert.Equal(CalendarConnectionStatus.Active, connection.Status);
        Assert.Equal(fixture.Clock.GetUtcNow(), connection.LastSyncedAt);
        Assert.Null(connection.LastError);
        Assert.Equal(2, calendar.Live.Count());
        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", swim.Recurrence);
        Assert.Equal("Levar touca\n\n— Kubico —\nParticipantes: Mãe\nVai buscar: Pai", swim.Description);
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15)), (holidays.StartDate!.Value, holidays.EndDate!.Value));

        // O refresh token fica cifrado na base de dados.
        Assert.DoesNotContain("mae@example.com", storedToken);
    }

    [Fact]
    public async Task What_changes_in_Google_comes_back_to_the_household()
    {
        var (admin, auth) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var calendarName = $"Kubico · {name}";
        var household = await HouseholdTests.CreateHousehold(admin, name);
        var swim = await CreateEvent(admin, household.Id, Timed("Natação", 2026, 10, 20, 18) with
        {
            Recurrence = "FREQ=WEEKLY;BYDAY=TU",
            ParticipantUserIds = [auth.User.Id],
        });
        var dentist = await CreateEvent(admin, household.Id, Timed("Dentista", 2026, 10, 21, 9));
        await ConnectAsync(admin, household.Id, "google", "mae@example.com");
        await SyncAsync(admin, household.Id);
        var calendar = Google.CalendarNamed(calendarName);
        string IdOf(string title) => calendar.Live.Single(e => e.Data.Title == title).Id;

        fixture.Tick();
        Google.PersonAdds(calendarName, External("Jantar dos avós", At(2026, 10, 24, 20)) with { Description = "Levar sobremesa" });
        Google.PersonChanges(calendarName, IdOf("Dentista"), d => d with { Title = "Dentista (Rita)", Location = "Clínica" });
        Google.PersonChangesOccurrence(calendarName, IdOf("Natação"), new DateOnly(2026, 10, 27),
            External("Natação (piscina nova)", At(2026, 10, 27, 19)));
        Google.PersonCancelsOccurrence(calendarName, IdOf("Natação"), new DateOnly(2026, 11, 3));
        var connection = await SyncAsync(admin, household.Id);
        var after = await GetCalendar(admin, household.Id, "2026-10-19", "2026-11-08");

        var dinner = after.Events.Single(e => e.Title == "Jantar dos avós");
        var moved = after.Events.Single(e => e.SeriesId == swim.Id);
        Assert.Null(connection.LastError);
        Assert.Equal("Levar sobremesa", dinner.Notes);
        Assert.Equal(("Dentista (Rita)", "Clínica"), after.Events.Where(e => e.Id == dentist.Id).Select(e => (e.Title, e.Location)).Single());
        Assert.Equal(("Natação (piscina nova)", new DateOnly(2026, 10, 27)), (moved.Title, moved.OriginalDate!.Value));
        Assert.Equal([auth.User.Id], moved.ParticipantUserIds);
        Assert.Equal(
            [Utc(2026, 10, 20, 17), Utc(2026, 10, 27, 19)],
            after.Occurrences.Where(o => o.EventId == swim.Id || o.EventId == moved.Id).Select(o => o.StartsAt!.Value));

        // O que veio de lá não volta a ser copiado (nem duplicado).
        Assert.Single(calendar.Live, e => e.Data.Title == "Jantar dos avós");

        fixture.Tick();
        Google.PersonDeletes(calendarName, IdOf("Dentista (Rita)"));
        await SyncAsync(admin, household.Id);
        var afterDelete = await GetCalendar(admin, household.Id, "2026-10-19", "2026-11-08");
        Assert.DoesNotContain(afterDelete.Events, e => e.Id == dentist.Id);
    }

    [Fact]
    public async Task What_changes_in_the_household_reaches_the_copy()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var household = await HouseholdTests.CreateHousehold(admin, name);
        var swim = await CreateEvent(admin, household.Id, Timed("Natação", 2026, 10, 20, 18) with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" });
        var dentist = await CreateEvent(admin, household.Id, Timed("Dentista", 2026, 10, 21, 9));
        await ConnectAsync(admin, household.Id, "google", "mae@example.com");
        await SyncAsync(admin, household.Id);

        fixture.Tick();
        await Put(admin, $"/api/households/{household.Id}/events/{dentist.Id}", Timed("Dentista (Rita)", 2026, 10, 21, 10));
        await Put(admin, $"/api/households/{household.Id}/events/{swim.Id}/occurrences/2026-10-27?scope=this",
            Timed("Natação (piscina nova)", 2026, 10, 27, 19));
        (await admin.DeleteAsync($"/api/households/{household.Id}/events/{swim.Id}/occurrences/2026-11-03?scope=this"))
            .EnsureSuccessStatusCode();
        await CreateEvent(admin, household.Id, Timed("Reunião de pais", 2026, 10, 22, 18));
        await SyncAsync(admin, household.Id);

        var calendar = Google.CalendarNamed($"Kubico · {name}");
        var series = calendar.Live.Single(e => e.Data.Title == "Natação");
        var moved = calendar.Events.Values.Single(e => e.SeriesId == series.Id);
        Assert.Equal(["Dentista (Rita)", "Natação", "Reunião de pais"], calendar.Live.Select(e => e.Data.Title).Order());
        Assert.Equal(At(2026, 10, 21, 10), calendar.Live.Single(e => e.Data.Title == "Dentista (Rita)").Data.StartsAt);

        // O dia alterado é uma ocorrência alterada da série; só o dia apagado sai da série.
        Assert.Equal([new DateOnly(2026, 11, 3)], series.Data.Cancelled);
        Assert.Equal((new DateOnly(2026, 10, 27), "Natação (piscina nova)"), (moved.OriginalDate!.Value, moved.Data.Title));

        fixture.Tick();
        (await admin.DeleteAsync($"/api/households/{household.Id}/events/{dentist.Id}")).EnsureSuccessStatusCode();
        await SyncAsync(admin, household.Id);
        Assert.DoesNotContain(calendar.Live, e => e.Data.Title.StartsWith("Dentista", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_both_sides_change_the_same_event_the_newest_change_wins()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var calendarName = $"Kubico · {name}";
        var household = await HouseholdTests.CreateHousehold(admin, name);
        var meeting = await CreateEvent(admin, household.Id, Timed("Reunião", 2026, 10, 22, 18));
        await ConnectAsync(admin, household.Id, "google", "mae@example.com");
        await SyncAsync(admin, household.Id);
        var copyId = Google.CalendarNamed(calendarName).Live.Single().Id;
        var url = $"/api/households/{household.Id}/events/{meeting.Id}";

        // O Kubico muda primeiro e o Google depois: fica a do Google.
        fixture.Tick();
        await Put(admin, url, Timed("Reunião (Kubico)", 2026, 10, 22, 18));
        fixture.Tick();
        Google.PersonChanges(calendarName, copyId, d => d with { Title = "Reunião (Google)" });
        await SyncAsync(admin, household.Id);
        var first = (await GetCalendar(admin, household.Id, "2026-10-22", "2026-10-22")).Events.Single().Title;
        var firstCopy = Google.CalendarNamed(calendarName).Live.Single().Data.Title;

        // O Google muda primeiro e o Kubico depois: fica a do Kubico.
        fixture.Tick();
        Google.PersonChanges(calendarName, copyId, d => d with { Title = "Reunião (Google, outra vez)" });
        fixture.Tick();
        await Put(admin, url, Timed("Reunião (Kubico, outra vez)", 2026, 10, 22, 18));
        await SyncAsync(admin, household.Id);
        var second = (await GetCalendar(admin, household.Id, "2026-10-22", "2026-10-22")).Events.Single().Title;
        var secondCopy = Google.CalendarNamed(calendarName).Live.Single().Data.Title;

        Assert.Equal(("Reunião (Google)", "Reunião (Google)"), (first, firstCopy));
        Assert.Equal(("Reunião (Kubico, outra vez)", "Reunião (Kubico, outra vez)"), (second, secondCopy));
    }

    [Fact]
    public async Task Outlook_is_read_in_full_and_keeps_the_days_deleted_in_the_household()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var calendarName = $"Kubico · {name}";
        var household = await HouseholdTests.CreateHousehold(admin, name);
        var swim = await CreateEvent(admin, household.Id, Timed("Natação", 2026, 10, 20, 18) with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" });
        await ConnectAsync(admin, household.Id, "microsoft", "mae@outlook.example");
        await SyncAsync(admin, household.Id);

        fixture.Tick();
        (await admin.DeleteAsync($"/api/households/{household.Id}/events/{swim.Id}/occurrences/2026-10-27?scope=this"))
            .EnsureSuccessStatusCode();
        await SyncAsync(admin, household.Id);
        var series = Outlook.CalendarNamed(calendarName).Live.Single();
        var cancelledInCopy = series.Data.Cancelled.ToList();

        // O Outlook não diz que dias foram apagados: mudar lá o título não pode trazer o dia de volta.
        fixture.Tick();
        Outlook.PersonChanges(calendarName, series.Id, d => d with { Title = "Natação (Outlook)", CancelledDates = null });
        var appointmentId = Outlook.PersonAdds(calendarName, External("Consulta", At(2026, 10, 23, 15)));
        await SyncAsync(admin, household.Id);
        var afterChanges = await GetCalendar(admin, household.Id, "2026-10-19", "2026-11-01");

        fixture.Tick();
        Outlook.PersonDeletes(calendarName, appointmentId);
        await SyncAsync(admin, household.Id);
        var afterDelete = await GetCalendar(admin, household.Id, "2026-10-19", "2026-11-01");

        var swimNow = afterChanges.Events.Single(e => e.Id == swim.Id);
        Assert.Equal([new DateOnly(2026, 10, 27)], cancelledInCopy);
        Assert.Equal("Natação (Outlook)", swimNow.Title);
        Assert.DoesNotContain(afterChanges.Occurrences, o => o.EventId == swim.Id && o.OccurrenceDate == new DateOnly(2026, 10, 27));
        Assert.Contains(afterChanges.Events, e => e.Title == "Consulta");
        Assert.DoesNotContain(afterDelete.Events, e => e.Title == "Consulta");

        // A Microsoft troca o refresh token a cada renovação, e o Kubico usa sempre o mais recente.
        var used = Outlook.RefreshTokensUsed.Where(t => t.Contains("mae@outlook.example", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, used.Count);
        Assert.Equal(used.Count, used.Distinct().Count());
    }

    [Fact]
    public async Task Events_the_household_cannot_hold_are_reported_and_left_out()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var household = await HouseholdTests.CreateHousehold(admin, name);
        await ConnectAsync(admin, household.Id, "google", "mae@example.com");
        await SyncAsync(admin, household.Id);

        Google.PersonAdds($"Kubico · {name}", External("Reunião mensal", At(2026, 10, 13, 18)) with { UnsupportedRecurrence = true });
        var connection = await SyncAsync(admin, household.Id);
        var calendar = await GetCalendar(admin, household.Id, "2026-10-01", "2026-10-31");

        Assert.Equal("«Reunião mensal» repete-se de uma forma que o Kubico ainda não suporta e não foi sincronizado.", connection.LastError);
        Assert.Empty(calendar.Events);
    }

    [Fact]
    public async Task Lost_access_asks_to_reconnect_and_disconnecting_removes_the_copy_but_keeps_the_events()
    {
        var (admin, _) = await fixture.RegisterAsync("Pai");
        var name = Unique("Casa");
        var calendarName = $"Kubico · {name}";
        var household = await HouseholdTests.CreateHousehold(admin, name);
        var dinner = await CreateEvent(admin, household.Id, Timed("Jantar", 2026, 10, 22, 20));
        await ConnectAsync(admin, household.Id, "google", "pai@example.com");
        await SyncAsync(admin, household.Id);

        Google.RevokedAccounts.Add("pai@example.com");
        var lost = await SyncAsync(admin, household.Id);
        Google.RevokedAccounts.Remove("pai@example.com");
        await ConnectAsync(admin, household.Id, "google", "pai@example.com");
        var reconnected = Assert.Single((await Overview(admin, household.Id)).Connections);
        var copies = Google.Calendars.Values.Count(c => c.Name == calendarName);

        var disconnect = await admin.DeleteAsync($"/api/households/{household.Id}/calendar-sync/{reconnected.Id}");
        var afterDisconnect = await Overview(admin, household.Id);
        var events = await GetCalendar(admin, household.Id, "2026-10-22", "2026-10-22");

        Assert.Equal(CalendarConnectionStatus.NeedsReconnect, lost.Status);
        Assert.Equal("O acesso à conta expirou ou foi retirado. Volta a ligar a conta.", lost.LastError);
        Assert.Equal((CalendarConnectionStatus.Active, null), (reconnected.Status, reconnected.LastError));
        Assert.Equal(1, copies);
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);
        Assert.Empty(afterDisconnect.Connections);
        Assert.DoesNotContain(Google.Calendars.Values, c => c.Name == calendarName);
        Assert.Contains(Google.RevokedTokens, t => t.Contains("pai@example.com", StringComparison.Ordinal));
        Assert.Equal(dinner.Id, Assert.Single(events.Events).Id);
    }

    [Fact]
    public async Task Nobody_syncs_or_disconnects_someone_elses_account()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var (partner, _) = await fixture.RegisterAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(admin, Unique("Casa"));
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);
        await ConnectAsync(admin, household.Id, "google", "mae@example.com");
        var connection = Assert.Single((await Overview(admin, household.Id)).Connections);

        var sync = await partner.PostAsync($"/api/households/{household.Id}/calendar-sync/{connection.Id}/sync", null);
        var disconnect = await partner.DeleteAsync($"/api/households/{household.Id}/calendar-sync/{connection.Id}");

        Assert.Equal(HttpStatusCode.NotFound, sync.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disconnect.StatusCode);
        Assert.Single((await Overview(admin, household.Id)).Connections);
    }

    [Fact]
    public async Task A_kubico_calendar_deleted_in_the_account_is_created_again()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var name = Unique("Casa");
        var calendarName = $"Kubico · {name}";
        var household = await HouseholdTests.CreateHousehold(admin, name);
        await CreateEvent(admin, household.Id, Timed("Jantar", 2026, 10, 22, 20));
        await ConnectAsync(admin, household.Id, "google", "mae@example.com");
        await SyncAsync(admin, household.Id);

        Google.Calendars.Remove(Google.CalendarIdNamed(calendarName));
        var connection = await SyncAsync(admin, household.Id);

        Assert.Equal("O calendário do Kubico tinha sido apagado na conta e foi criado de novo.", connection.LastError);
        Assert.Equal("Jantar", Assert.Single(Google.CalendarNamed(calendarName).Live).Data.Title);
    }

    [Fact]
    public async Task The_return_page_turns_away_cancelled_forged_and_expired_requests()
    {
        var (admin, _) = await fixture.RegisterAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(admin, Unique("Casa"));
        var (callback, state) = await AuthorizeAsync(admin, household.Id, "google");
        using var browser = fixture.App.CreateClient();
        async Task<(HttpStatusCode, string)> Visit(string path)
        {
            var response = await browser.GetAsync(path);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var cancelled = await Visit($"{callback}?error=access_denied&state={Uri.EscapeDataString(state)}");
        var forged = await Visit($"{callback}?code=ok:mae@example.com&state={Uri.EscapeDataString(state + "x")}");
        var otherService = await Visit($"/api/calendar-sync/callback/microsoft?code=ok:mae@example.com&state={Uri.EscapeDataString(state)}");
        var refused = await Visit($"{callback}?code=recusado&state={Uri.EscapeDataString(state)}");
        fixture.Clock.Advance(TimeSpan.FromMinutes(16));
        var expired = await Visit($"{callback}?code=ok:mae@example.com&state={Uri.EscapeDataString(state)}");

        Assert.Equal((HttpStatusCode.BadRequest, true), (cancelled.Item1, cancelled.Item2.Contains("Ligação cancelada")));
        Assert.Equal((HttpStatusCode.BadRequest, true), (forged.Item1, forged.Item2.Contains("Pedido inválido")));
        Assert.Equal(HttpStatusCode.BadRequest, otherService.Item1);
        Assert.Equal((HttpStatusCode.BadGateway, true), (refused.Item1, refused.Item2.Contains("Não foi possível ligar")));
        Assert.Equal((HttpStatusCode.BadRequest, true), (expired.Item1, expired.Item2.Contains("O pedido expirou")));
        Assert.Empty((await Overview(admin, household.Id)).Connections);
    }

    // --- Ajudas ---------------------------------------------------------------

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Hora local de Lisboa.</summary>
    private static DateTimeOffset At(int year, int month, int day, int hour) =>
        ZonedTime.ToInstant(new DateTime(year, month, day, hour, 0, 0), Lisbon);

    private static DateTimeOffset Utc(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static SaveEventRequest Timed(string title, int year, int month, int day, int hour) =>
        new(title, StartsAt: At(year, month, day, hour), EndsAt: At(year, month, day, hour).AddHours(1));

    private static ExternalEventData External(string title, DateTimeOffset start) =>
        new(title, null, null, start, start.AddHours(1), null, null);

    private async Task<(string Callback, string State)> AuthorizeAsync(HttpClient client, Guid householdId, string provider)
    {
        var response = await client.PostAsync($"/api/households/{householdId}/calendar-sync/{provider}/authorize", null);
        response.EnsureSuccessStatusCode();
        var url = (await response.Content.ReadFromJsonAsync<CalendarAuthorizationDto>(TestJson.Options))!.Url;
        var query = QueryHelpers.ParseQuery(new Uri(url).Query);
        return (new Uri(query["redirect_uri"].ToString()).AbsolutePath, query["state"].ToString());
    }

    /// <summary>Faz o que a pessoa faz no navegador: autoriza e volta à página do Kubico.</summary>
    private async Task<string> ConnectAsync(HttpClient client, Guid householdId, string provider, string email)
    {
        var (callback, state) = await AuthorizeAsync(client, householdId, provider);
        using var browser = fixture.App.CreateClient();
        var page = await browser.GetAsync($"{callback}?code={Uri.EscapeDataString("ok:" + email)}&state={Uri.EscapeDataString(state)}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.True(page.IsSuccessStatusCode, html);
        return html;
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

    private static async Task Put(HttpClient client, string url, SaveEventRequest request) =>
        (await client.PutAsJsonAsync(url, request, TestJson.Options)).EnsureSuccessStatusCode();

    private static async Task<CalendarDto> GetCalendar(HttpClient client, Guid householdId, string from, string to) =>
        (await client.GetFromJsonAsync<CalendarDto>($"/api/households/{householdId}/events?from={from}&to={to}", TestJson.Options))!;
}
