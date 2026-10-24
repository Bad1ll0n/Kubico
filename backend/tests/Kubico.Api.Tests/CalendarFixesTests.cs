using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Calendar;
using Kubico.Api.Households;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Calendário (T5, T6, T15, A6, S14).</summary>
public class CalendarFixesTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    [Fact]
    public async Task The_calendar_address_stops_working_for_whoever_loses_access_to_the_calendar()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, _) = await Fase1.MemberAsync(factory, admin, household, "Bruno", HouseholdRole.Child);
        var (adult, _) = await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);
        var anonymous = factory.CreateClient();
        await NotificationTests.Post<CalendarEventDto>(admin, $"/api/households/{household}/events", new SaveEventRequest(
            "Evento", StartsAt: DateTimeOffset.Parse("2026-11-04T17:00:00Z"), EndsAt: DateTimeOffset.Parse("2026-11-04T18:00:00Z")));
        var childFeed = await NotificationTests.Post<CalendarFeedDto>(child, $"/api/households/{household}/calendar-feed", null);
        var adultFeed = await NotificationTests.Post<CalendarFeedDto>(adult, $"/api/households/{household}/calendar-feed", null);
        string Path(CalendarFeedDto feed) => new Uri(feed.Url).AbsolutePath;

        var before = await anonymous.GetAsync(Path(childFeed));
        (await admin.PutAsJsonAsync($"/api/households/{household}/permissions", new SavePermissionsRequest(
            [PermissionTests.Role(HouseholdRole.Child, (HouseholdModule.Calendar, ModuleAccess.None))]), TestJson.Options)).EnsureSuccessStatusCode();
        var after = await anonymous.GetAsync(Path(childFeed));

        // Voltar a dar acesso não ressuscita o endereço antigo.
        (await admin.PutAsJsonAsync($"/api/households/{household}/permissions", new SavePermissionsRequest(
            [PermissionTests.Role(HouseholdRole.Child, (HouseholdModule.Calendar, ModuleAccess.View))]), TestJson.Options)).EnsureSuccessStatusCode();
        var backAgain = await anonymous.GetAsync(Path(childFeed));
        var adultStill = await anonymous.GetAsync(Path(adultFeed));

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, backAgain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adultStill.StatusCode);
    }

    [Fact]
    public async Task The_same_person_driving_to_two_places_at_the_same_time_is_a_conflict()
    {
        var (admin, adminAuth, household) = await Fase1.HouseholdAsync(factory);
        var (_, dad) = await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);
        var (_, bruno) = await Fase1.MemberAsync(factory, admin, household, "Bruno", HouseholdRole.Child);
        var (_, carla) = await Fase1.MemberAsync(factory, admin, household, "Carla", HouseholdRole.Child);
        var events = $"/api/households/{household}/events";
        var at = (string time) => DateTimeOffset.Parse($"2026-11-04T{time}:00Z");

        var football = await NotificationTests.Post<CalendarEventDto>(admin, events, new SaveEventRequest(
            "Futebol", StartsAt: at("17:00"), EndsAt: at("18:00"), ParticipantUserIds: [bruno], DropOffUserId: dad));
        var ballet = await NotificationTests.Post<CalendarEventDto>(admin, events, new SaveEventRequest(
            "Ballet", StartsAt: at("17:00"), EndsAt: at("18:00"), ParticipantUserIds: [carla], DropOffUserId: dad));
        var music = await NotificationTests.Post<CalendarEventDto>(admin, events, new SaveEventRequest(
            "Música", StartsAt: at("15:00"), EndsAt: at("16:00"), ParticipantUserIds: [bruno], PickUpUserId: adminAuth.User.Id));
        var english = await NotificationTests.Post<CalendarEventDto>(admin, events, new SaveEventRequest(
            "Inglês", StartsAt: at("16:05"), EndsAt: at("17:30"), ParticipantUserIds: [carla], DropOffUserId: adminAuth.User.Id));
        var calendar = (await admin.GetFromJsonAsync<CalendarDto>($"{events}?from=2026-11-04&to=2026-11-04", TestJson.Options))!;
        var conflicts = calendar.Occurrences.ToDictionary(o => o.EventId, o => o.ConflictUserIds);

        Assert.Contains(dad, conflicts[football.Id]);
        Assert.Contains(dad, conflicts[ballet.Id]);

        // Ir buscar às 16:00 e levar às 16:05 noutro sítio: a mãe também não chega.
        Assert.Contains(adminAuth.User.Id, conflicts[music.Id]);
        Assert.Contains(adminAuth.User.Id, conflicts[english.Id]);
    }

    [Fact]
    public async Task Whoever_is_away_is_not_available_and_children_do_not_drive()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (_, dad) = await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);
        var (_, bruno) = await Fase1.MemberAsync(factory, admin, household, "Bruno", HouseholdRole.Child);
        var day = factory.Today.AddDays(3);
        var start = ZonedTime.ToInstant(day.ToDateTime(new TimeOnly(17, 0)), Lisbon);
        var events = $"/api/households/{household}/events";
        (await admin.PostAsJsonAsync($"/api/households/{household}/absences",
            new SaveAbsenceRequest(dad, day, day.AddDays(2)), TestJson.Options)).EnsureSuccessStatusCode();

        var swimming = await NotificationTests.Post<CalendarEventDto>(admin, events, new SaveEventRequest(
            "Natação", StartsAt: start, EndsAt: start.AddHours(1), ParticipantUserIds: [bruno], DropOffUserId: dad));
        var childDriver = await admin.PostAsJsonAsync(events, new SaveEventRequest(
            "Dentista", StartsAt: start, EndsAt: start.AddHours(1), PickUpUserId: bruno), TestJson.Options);
        var calendar = (await admin.GetFromJsonAsync<CalendarDto>($"{events}?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}", TestJson.Options))!;

        Assert.Contains(dad, calendar.Occurrences.Single(o => o.EventId == swimming.Id).ConflictUserIds);
        Assert.Equal(HttpStatusCode.BadRequest, childDriver.StatusCode);
    }

    [Fact]
    public async Task A_weekly_repeat_starts_on_the_first_chosen_day()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var events = $"/api/households/{household}/events";

        // Quarta-feira, 28/10, 19:00 em Lisboa, mas só às segundas.
        var created = await NotificationTests.Post<CalendarEventDto>(admin, events, new SaveEventRequest(
            "Explicações", StartsAt: DateTimeOffset.Parse("2026-10-28T19:00:00Z"), EndsAt: DateTimeOffset.Parse("2026-10-28T20:00:00Z"),
            Recurrence: "FREQ=WEEKLY;BYDAY=MO"));
        var allDay = await NotificationTests.Post<CalendarEventDto>(admin, events, new SaveEventRequest(
            "Treino", IsAllDay: true, StartDate: new DateOnly(2026, 10, 28), Recurrence: "FREQ=WEEKLY;BYDAY=FR,SA"));
        var calendar = (await admin.GetFromJsonAsync<CalendarDto>($"{events}?from=2026-10-26&to=2026-11-15", TestJson.Options))!;

        Assert.Equal(DateTimeOffset.Parse("2026-11-02T19:00:00Z"), created.StartsAt);
        Assert.Equal(new DateOnly(2026, 10, 30), allDay.StartDate);
        Assert.Equal(
            [new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 9)],
            calendar.Occurrences.Where(o => o.EventId == created.Id).Select(o => o.OccurrenceDate));
    }

    [Fact]
    public async Task Control_characters_cannot_add_lines_to_the_calendar_feed()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var events = $"/api/households/{household}/events";

        var injected = await admin.PostAsJsonAsync(events, new SaveEventRequest(
            "Seg\rX-INJETADO:1", StartsAt: DateTimeOffset.Parse("2026-11-04T17:00:00Z"), EndsAt: DateTimeOffset.Parse("2026-11-04T18:00:00Z")),
            TestJson.Options);
        var notesWithLines = await admin.PostAsJsonAsync(events, new SaveEventRequest(
            "Reunião", Notes: "Levar:\r\n- caderno\n- lápis", StartsAt: DateTimeOffset.Parse("2026-11-05T17:00:00Z"),
            EndsAt: DateTimeOffset.Parse("2026-11-05T18:00:00Z")), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
        Assert.Equal(HttpStatusCode.Created, notesWithLines.StatusCode);
        Assert.Equal("Seg\\nX-INJETADO:1\\, ok", IcsWriter.Text("Seg\rX-INJETADO:1, ok\u0007"));
    }
}
