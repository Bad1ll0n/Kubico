using System.Net;
using System.Net.Http.Json;
using System.Text;
using Kubico.Api.Calendar;

namespace Kubico.Api.Tests;

public class CalendarFeedTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    [Fact]
    public async Task Feed_has_repeats_exceptions_all_day_events_and_the_household_time_zone()
    {
        var (client, auth) = await factory.CreateUserAsync("Filipe");
        var household = await HouseholdTests.CreateHousehold(client, "Casa da Amora");
        var url = $"/api/households/{household.Id}/events";
        var swimming = await CreateEvent(client, household.Id, new SaveEventRequest(
            "Natação, piscina",
            Notes: "Levar touca",
            StartsAt: DateTimeOffset.Parse("2026-10-20T18:00:00+01:00"),
            EndsAt: DateTimeOffset.Parse("2026-10-20T19:00:00+01:00"),
            Recurrence: "FREQ=WEEKLY;UNTIL=20261222",
            ParticipantUserIds: [auth.User.Id],
            DropOffUserId: auth.User.Id));
        (await client.DeleteAsync($"{url}/{swimming.Id}/occurrences/2026-11-03")).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"{url}/{swimming.Id}/occurrences/2026-11-10", new SaveEventRequest(
            "Natação (quarta)", StartsAt: Utc(2026, 11, 11, 18), EndsAt: Utc(2026, 11, 11, 19)), TestJson.Options))
            .EnsureSuccessStatusCode();
        await CreateEvent(client, household.Id, new SaveEventRequest(
            "Férias", IsAllDay: true, StartDate: new DateOnly(2026, 12, 21), EndDate: new DateOnly(2026, 12, 23)));

        var feed = await GetFeedAddress(client, household.Id);
        var sameFeed = await GetFeedAddress(client, household.Id);
        var response = await factory.CreateClient().GetAsync(new Uri(feed.Url).PathAndQuery);
        var ics = await response.Content.ReadAsStringAsync();
        var lines = ics.Replace("\r\n ", "").Split("\r\n");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(feed.Url, sameFeed.Url);
        Assert.StartsWith("webcal://", feed.WebcalUrl);
        Assert.All(ics.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.Contains("X-WR-CALNAME:Casa da Amora", lines);
        Assert.Contains("TZID:Europe/Lisbon", lines);
        Assert.Contains($"UID:{swimming.Id}@kubico", lines);
        Assert.Contains("DTSTART;TZID=Europe/Lisbon:20261020T180000", lines);
        Assert.Contains("RRULE:FREQ=WEEKLY;UNTIL=20261222T235959Z", lines);
        Assert.Contains("EXDATE;TZID=Europe/Lisbon:20261103T180000", lines);
        Assert.Contains("RECURRENCE-ID;TZID=Europe/Lisbon:20261110T180000", lines);
        Assert.Contains("SUMMARY:Natação\\, piscina", lines);
        Assert.Contains("DESCRIPTION:Levar touca\\nParticipantes: Filipe\\nLeva: Filipe", lines);
        Assert.Contains("DTSTART;VALUE=DATE:20261221", lines);
        Assert.Contains("DTEND;VALUE=DATE:20261224", lines);
        Assert.Equal(3, lines.Count(l => l == "BEGIN:VEVENT"));
    }

    [Fact]
    public async Task A_new_address_disables_the_old_one_and_only_members_get_one()
    {
        var (client, _) = await factory.CreateUserAsync();
        var (stranger, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var anonymous = factory.CreateClient();

        var old = await GetFeedAddress(client, household.Id);
        (await client.DeleteAsync($"/api/households/{household.Id}/calendar-feed")).EnsureSuccessStatusCode();
        var renewed = await GetFeedAddress(client, household.Id);
        var strangerFeed = await stranger.PostAsync($"/api/households/{household.Id}/calendar-feed", null);

        Assert.NotEqual(old.Url, renewed.Url);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(new Uri(old.Url).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(new Uri(renewed.Url).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerFeed.StatusCode);
    }

    [Fact]
    public void Time_zone_rules_follow_the_local_clock_changes()
    {
        var lisbon = IcsWriter.TimeZoneComponent(Lisbon, "Europe/Lisbon", 2026).ToList();
        var newYork = IcsWriter.TimeZoneComponent(
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), "America/New_York", 2026).ToList();

        Assert.Equal(
            [
                "BEGIN:VTIMEZONE", "TZID:Europe/Lisbon",
                "BEGIN:DAYLIGHT", "DTSTART:19700329T010000", "RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU",
                "TZOFFSETFROM:+0000", "TZOFFSETTO:+0100", "END:DAYLIGHT",
                "BEGIN:STANDARD", "DTSTART:19701025T020000", "RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU",
                "TZOFFSETFROM:+0100", "TZOFFSETTO:+0000", "END:STANDARD",
                "END:VTIMEZONE",
            ],
            lisbon);
        Assert.Contains("RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU", newYork);
        Assert.Contains("RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU", newYork);
        Assert.Contains("TZOFFSETTO:-0400", newYork);
    }

    [Fact]
    public void Long_lines_are_folded_without_splitting_characters()
    {
        var line = "DESCRIPTION:" + string.Concat(Enumerable.Repeat("Ação na piscina municipal ", 8));

        var folded = IcsWriter.Fold(line);

        Assert.All(folded.Split("\r\n"), part => Assert.True(Encoding.UTF8.GetByteCount(part) <= 75));
        Assert.Equal(line, folded.Replace("\r\n ", ""));
        Assert.Equal("a\\, b\\; c\\\\d\\ne", IcsWriter.Text("a, b; c\\d\ne"));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static async Task<CalendarEventDto> CreateEvent(HttpClient client, Guid householdId, SaveEventRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/households/{householdId}/events", request, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CalendarEventDto>(TestJson.Options))!;
    }

    private static async Task<CalendarFeedDto> GetFeedAddress(HttpClient client, Guid householdId)
    {
        var response = await client.PostAsync($"/api/households/{householdId}/calendar-feed", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CalendarFeedDto>(TestJson.Options))!;
    }
}
