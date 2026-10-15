using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Calendar;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

public class CalendarTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Weekly_event_is_expanded_in_the_requested_range_with_the_holidays()
    {
        var (client, auth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");

        // 18:00 em Lisboa, ainda na hora de verão (+01:00).
        var created = await CreateEvent(client, household.Id, new SaveEventRequest(
            "Natação",
            StartsAt: DateTimeOffset.Parse("2026-10-20T18:00:00+01:00"),
            EndsAt: DateTimeOffset.Parse("2026-10-20T19:00:00+01:00"),
            Recurrence: "freq=weekly;byday=tu",
            ParticipantUserIds: [auth.User.Id]));

        var calendar = await GetCalendar(client, household.Id, "2026-10-01", "2026-11-07");

        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", created.Recurrence);
        Assert.Equal(TimeSpan.Zero, created.StartsAt!.Value.Offset);
        Assert.Equal(created.Id, Assert.Single(calendar.Events).Id);
        Assert.Equal(
            new[] { Utc(2026, 10, 20, 17), Utc(2026, 10, 27, 18), Utc(2026, 11, 3, 18) },
            calendar.Occurrences.Select(o => o.StartsAt!.Value));
        Assert.Equal(
            new[] { "Implantação da República", "Todos os Santos" },
            calendar.Holidays.Select(h => h.Name));
    }

    [Fact]
    public async Task All_day_event_shows_up_on_any_of_its_days()
    {
        var (client, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        await CreateEvent(client, household.Id, new SaveEventRequest(
            "Férias no Algarve", IsAllDay: true, StartDate: new DateOnly(2026, 8, 1), EndDate: new DateOnly(2026, 8, 15)));

        var during = await GetCalendar(client, household.Id, "2026-08-10", "2026-08-20");
        var after = await GetCalendar(client, household.Id, "2026-08-16", "2026-08-20");

        var occurrence = Assert.Single(during.Occurrences);
        Assert.Null(occurrence.StartsAt);
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15)), (occurrence.StartDate!.Value, occurrence.EndDate!.Value));
        Assert.Equal("Assunção de Nossa Senhora", Assert.Single(during.Holidays).Name);
        Assert.Empty(after.Occurrences);
    }

    [Fact]
    public async Task Members_are_warned_when_whoever_drives_is_busy()
    {
        var (mom, momAuth) = await factory.CreateUserAsync("Mãe");
        var (dad, dadAuth) = await factory.CreateUserAsync("Pai");
        var (kid, kidAuth) = await factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(mom, "Casa");
        await Join(mom, dad, household.Id, HouseholdRole.Adult);
        await Join(mom, kid, household.Id, HouseholdRole.Child);

        var meeting = await CreateEvent(mom, household.Id, new SaveEventRequest(
            "Reunião", StartsAt: Utc(2026, 11, 10, 9), EndsAt: Utc(2026, 11, 10, 11), ParticipantUserIds: [momAuth.User.Id]));
        var football = await CreateEvent(dad, household.Id, new SaveEventRequest(
            "Futebol",
            StartsAt: Utc(2026, 11, 10, 10),
            EndsAt: Utc(2026, 11, 10, 11).AddMinutes(30),
            ParticipantUserIds: [kidAuth.User.Id],
            DropOffUserId: momAuth.User.Id,
            PickUpUserId: dadAuth.User.Id));

        var calendar = await GetCalendar(kid, household.Id, "2026-11-10", "2026-11-10");

        Assert.Empty(calendar.Occurrences.Single(o => o.EventId == meeting.Id).ConflictUserIds);
        Assert.Equal(momAuth.User.Id, Assert.Single(calendar.Occurrences.Single(o => o.EventId == football.Id).ConflictUserIds));
    }

    [Fact]
    public async Task Event_can_be_moved_and_deleted()
    {
        var (client, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var url = $"/api/households/{household.Id}/events";
        var created = await CreateEvent(client, household.Id, new SaveEventRequest(
            "Dentista", StartsAt: Utc(2026, 11, 12, 15), EndsAt: Utc(2026, 11, 12, 16)));

        var moved = await client.PutAsJsonAsync($"{url}/{created.Id}", new SaveEventRequest(
            "Dentista (Rui)", Location: "Clínica da Amora", StartsAt: Utc(2026, 11, 13, 9), EndsAt: Utc(2026, 11, 13, 10)),
            TestJson.Options);
        var updated = (await moved.Content.ReadFromJsonAsync<CalendarEventDto>(TestJson.Options))!;
        var before = await GetCalendar(client, household.Id, "2026-11-12", "2026-11-12");
        var after = await GetCalendar(client, household.Id, "2026-11-13", "2026-11-13");

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(("Dentista (Rui)", "Clínica da Amora"), (updated.Title, updated.Location));
        Assert.Empty(before.Occurrences);
        Assert.Single(after.Occurrences);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{url}/{created.Id}")).StatusCode);
        Assert.Empty((await GetCalendar(client, household.Id, "2026-11-13", "2026-11-13")).Occurrences);
    }

    [Fact]
    public async Task Children_see_the_calendar_but_cannot_change_it()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (child, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await Join(admin, child, household.Id, HouseholdRole.Child);
        var url = $"/api/households/{household.Id}/events";
        var party = await CreateEvent(admin, household.Id, new SaveEventRequest(
            "Festa", IsAllDay: true, StartDate: new DateOnly(2026, 12, 19)));

        var calendar = await GetCalendar(child, household.Id, "2026-12-01", "2026-12-31");
        var create = await child.PostAsJsonAsync(url, new SaveEventRequest(
            "Sem escola", IsAllDay: true, StartDate: new DateOnly(2026, 12, 20)), TestJson.Options);
        var delete = await child.DeleteAsync($"{url}/{party.Id}");

        Assert.Single(calendar.Occurrences);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Invalid_events_and_ranges_are_rejected()
    {
        var (client, auth) = await factory.CreateUserAsync();
        var (_, stranger) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var url = $"/api/households/{household.Id}/events";
        var start = Utc(2026, 11, 10, 9);

        SaveEventRequest[] invalid =
        [
            new("Ao contrário", StartsAt: start, EndsAt: start.AddHours(-1)),
            new("Sem hora", StartsAt: start),
            new("Sem dia", IsAllDay: true),
            new("De hora a hora", StartsAt: start, EndsAt: start.AddHours(1), Recurrence: "FREQ=HOURLY"),
            new("Com estranhos", StartsAt: start, EndsAt: start.AddHours(1), ParticipantUserIds: [auth.User.Id, stranger.User.Id]),
            new("Boleia de estranhos", StartsAt: start, EndsAt: start.AddHours(1), DropOffUserId: stranger.User.Id),
        ];
        foreach (var request in invalid)
        {
            var response = await client.PostAsJsonAsync(url, request, TestJson.Options);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"\"{request.Title}\" devia ser recusado.");
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}?from=2026-11-10&to=2026-11-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}?from=2026-01-01&to=2026-12-31")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Events_from_another_household_are_not_reachable()
    {
        var (owner, _) = await factory.CreateUserAsync();
        var (other, _) = await factory.CreateUserAsync();
        var ownerHousehold = await HouseholdTests.CreateHousehold(owner, "Casa A");
        var otherHousehold = await HouseholdTests.CreateHousehold(other, "Casa B");
        var secret = await CreateEvent(owner, ownerHousehold.Id, new SaveEventRequest(
            "Privado", StartsAt: Utc(2026, 11, 10, 9), EndsAt: Utc(2026, 11, 10, 10)));

        var read = await other.GetAsync($"/api/households/{ownerHousehold.Id}/events?from=2026-11-10&to=2026-11-10");
        var delete = await other.DeleteAsync($"/api/households/{otherHousehold.Id}/events/{secret.Id}");
        var ownCalendar = await GetCalendar(other, otherHousehold.Id, "2026-11-10", "2026-11-10");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Empty(ownCalendar.Occurrences);
    }

    [Fact]
    public async Task Members_see_new_and_deleted_events_in_real_time()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (partner, partnerAuth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await Join(admin, partner, household.Id, HouseholdRole.Adult);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HouseholdHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(partnerAuth.AccessToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = TestJson.Options)
            .Build();
        var upserted = new TaskCompletionSource<CalendarEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<CalendarEventDto>(HouseholdEvents.CalendarEventUpserted, e => upserted.TrySetResult(e));
        connection.On<Guid>(HouseholdEvents.CalendarEventDeleted, id => deleted.TrySetResult(id));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinHousehold", household.Id);

        var created = await CreateEvent(admin, household.Id, new SaveEventRequest(
            "Jantar com os avós", StartsAt: Utc(2026, 11, 14, 19), EndsAt: Utc(2026, 11, 14, 22)));
        var received = await upserted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await admin.DeleteAsync($"/api/households/{household.Id}/events/{created.Id}");

        Assert.Equal(("Jantar com os avós", created.Id), (received.Title, received.Id));
        Assert.Equal(created.Id, await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task One_occurrence_can_be_moved_or_deleted_without_touching_the_rest()
    {
        var (client, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var url = $"/api/households/{household.Id}/events";
        var swimming = await CreateEvent(client, household.Id, new SaveEventRequest(
            "Natação", StartsAt: Utc(2026, 11, 3, 18), EndsAt: Utc(2026, 11, 3, 19), Recurrence: "FREQ=WEEKLY"));
        var moved = new SaveEventRequest("Natação (quarta)", StartsAt: Utc(2026, 11, 11, 19), EndsAt: Utc(2026, 11, 11, 20));

        var first = await client.PutAsJsonAsync($"{url}/{swimming.Id}/occurrences/2026-11-10?scope=this", moved, TestJson.Options);
        var again = await client.PutAsJsonAsync($"{url}/{swimming.Id}/occurrences/2026-11-10", moved, TestJson.Options);
        var deleted = await client.DeleteAsync($"{url}/{swimming.Id}/occurrences/2026-11-17");
        var exception = (await first.Content.ReadFromJsonAsync<CalendarEventDto>(TestJson.Options))!;
        var calendar = await GetCalendar(client, household.Id, "2026-11-01", "2026-11-30");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal((swimming.Id, new DateOnly(2026, 11, 10)), (exception.SeriesId!.Value, exception.OriginalDate!.Value));
        Assert.Equal(
            new[] { (swimming.Id, Utc(2026, 11, 3, 18)), (exception.Id, Utc(2026, 11, 11, 19)), (swimming.Id, Utc(2026, 11, 24, 18)) },
            calendar.Occurrences.Select(o => (o.EventId, o.StartsAt!.Value)));

        // Apagar a série apaga também as exceções.
        (await client.DeleteAsync($"{url}/{swimming.Id}")).EnsureSuccessStatusCode();
        Assert.Empty((await GetCalendar(client, household.Id, "2026-11-01", "2026-11-30")).Occurrences);
    }

    [Fact]
    public async Task This_and_following_splits_or_ends_the_series()
    {
        var (client, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var url = $"/api/households/{household.Id}/events";
        var swimming = await CreateEvent(client, household.Id, new SaveEventRequest(
            "Natação", StartsAt: Utc(2026, 11, 3, 18), EndsAt: Utc(2026, 11, 3, 19), Recurrence: "FREQ=WEEKLY"));

        var split = await client.PutAsJsonAsync($"{url}/{swimming.Id}/occurrences/2026-11-17?scope=following", new SaveEventRequest(
            "Natação (horário de inverno)", StartsAt: Utc(2026, 11, 17, 19), EndsAt: Utc(2026, 11, 17, 20), Recurrence: "FREQ=WEEKLY"),
            TestJson.Options);
        var winter = (await split.Content.ReadFromJsonAsync<CalendarEventDto>(TestJson.Options))!;
        var calendar = await GetCalendar(client, household.Id, "2026-11-01", "2026-11-30");

        Assert.Equal(HttpStatusCode.Created, split.StatusCode);
        Assert.Equal(
            new[] { Utc(2026, 11, 3, 18), Utc(2026, 11, 10, 18), Utc(2026, 11, 17, 19), Utc(2026, 11, 24, 19) },
            calendar.Occurrences.Select(o => o.StartsAt!.Value));
        Assert.Equal("FREQ=WEEKLY;UNTIL=20261110", calendar.Events.Single(e => e.Id == swimming.Id).Recurrence);

        // "Este e os seguintes" a partir da primeira ocorrência apaga a série toda.
        var stopped = await client.DeleteAsync($"{url}/{winter.Id}/occurrences/2026-11-24?scope=following");
        var gone = await client.DeleteAsync($"{url}/{swimming.Id}/occurrences/2026-11-03?scope=following");
        var after = await GetCalendar(client, household.Id, "2026-11-01", "2026-12-31");

        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        Assert.Equal(Utc(2026, 11, 17, 19), Assert.Single(after.Occurrences).StartsAt);
    }

    [Fact]
    public async Task Moving_the_whole_series_drops_its_exceptions()
    {
        var (client, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var url = $"/api/households/{household.Id}/events";
        var dinner = await CreateEvent(client, household.Id, new SaveEventRequest(
            "Jantar de família", StartsAt: Utc(2026, 11, 1, 20), EndsAt: Utc(2026, 11, 1, 22), Recurrence: "FREQ=WEEKLY"));
        (await client.DeleteAsync($"{url}/{dinner.Id}/occurrences/2026-11-08")).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"{url}/{dinner.Id}/occurrences/2026-11-15", new SaveEventRequest(
            "Jantar em casa dos avós", StartsAt: Utc(2026, 11, 15, 13), EndsAt: Utc(2026, 11, 15, 15)), TestJson.Options))
            .EnsureSuccessStatusCode();

        var renamed = await client.PutAsJsonAsync($"{url}/{dinner.Id}", new SaveEventRequest(
            "Jantar", StartsAt: Utc(2026, 11, 1, 20), EndsAt: Utc(2026, 11, 1, 22), Recurrence: "FREQ=WEEKLY"), TestJson.Options);
        var kept = await GetCalendar(client, household.Id, "2026-11-01", "2026-11-21");
        var moved = await client.PutAsJsonAsync($"{url}/{dinner.Id}", new SaveEventRequest(
            "Jantar", StartsAt: Utc(2026, 11, 1, 19), EndsAt: Utc(2026, 11, 1, 21), Recurrence: "FREQ=WEEKLY"), TestJson.Options);
        var reset = await GetCalendar(client, household.Id, "2026-11-01", "2026-11-21");

        renamed.EnsureSuccessStatusCode();
        moved.EnsureSuccessStatusCode();
        Assert.Equal(new[] { 1, 15 }, kept.Occurrences.Select(o => o.StartsAt!.Value.Day));
        Assert.Equal(new[] { 1, 8, 15 }, reset.Occurrences.Select(o => o.StartsAt!.Value.Day));
        Assert.All(reset.Occurrences, o => Assert.Equal(dinner.Id, o.EventId));
    }

    [Fact]
    public async Task Occurrence_changes_check_the_day_the_scope_and_permissions()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (child, _) = await factory.CreateUserAsync();
        var (stranger, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var strangerHousehold = await HouseholdTests.CreateHousehold(stranger, "Outra casa");
        await Join(admin, child, household.Id, HouseholdRole.Child);
        var url = $"/api/households/{household.Id}/events";
        var weekly = await CreateEvent(admin, household.Id, new SaveEventRequest(
            "Treino", StartsAt: Utc(2026, 11, 3, 18), EndsAt: Utc(2026, 11, 3, 19), Recurrence: "FREQ=WEEKLY"));
        var once = await CreateEvent(admin, household.Id, new SaveEventRequest(
            "Reunião", StartsAt: Utc(2026, 11, 4, 9), EndsAt: Utc(2026, 11, 4, 10)));

        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{url}/{weekly.Id}/occurrences/2026-11-04")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{url}/{once.Id}/occurrences/2026-11-04")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"{url}/{weekly.Id}/occurrences/2026-11-10?scope=all")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await child.DeleteAsync($"{url}/{weekly.Id}/occurrences/2026-11-10")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync(
            $"/api/households/{strangerHousehold.Id}/events/{weekly.Id}/occurrences/2026-11-10")).StatusCode);
        Assert.Equal(4, (await GetCalendar(admin, household.Id, "2026-11-01", "2026-11-24")).Occurrences.Count(o => o.EventId == weekly.Id));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static async Task Join(HttpClient admin, HttpClient member, Guid householdId, HouseholdRole role)
    {
        var invite = await HouseholdTests.CreateInvite(admin, householdId, role);
        (await member.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();
    }

    private static async Task<CalendarEventDto> CreateEvent(HttpClient client, Guid householdId, SaveEventRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/households/{householdId}/events", request, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CalendarEventDto>(TestJson.Options))!;
    }

    private static async Task<CalendarDto> GetCalendar(HttpClient client, Guid householdId, string from, string to) =>
        (await client.GetFromJsonAsync<CalendarDto>(
            $"/api/households/{householdId}/events?from={from}&to={to}", TestJson.Options))!;
}
