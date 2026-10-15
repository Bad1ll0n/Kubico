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
