using System.Net.Http.Json;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Notifications;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>Lembretes e resumos (F4-11, F4-21, F4-26). Cada teste tem o seu servidor, porque avança o relógio.</summary>
public sealed class ReminderTests : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    [Fact]
    public async Task An_event_reminder_goes_once_to_the_participants_at_the_right_time()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var (_, dadId) = await Fase1.MemberAsync(_factory, admin, household, "Pai", HouseholdRole.Adult);
        var start = _factory.Clock.GetUtcNow().AddHours(15);
        var swimming = await NotificationTests.Post<CalendarEventDto>(admin, $"/api/households/{household}/events",
            new SaveEventRequest("Natação", StartsAt: start, EndsAt: start.AddHours(1), ParticipantUserIds: [dadId],
                ReminderMinutes: [30]));

        _factory.Clock.SetUtcNow(start.AddMinutes(-31));
        var early = await RunAsync();
        _factory.Clock.SetUtcNow(start.AddMinutes(-29));
        var due = await RunAsync();
        var again = await RunAsync();

        Assert.Equal([30], swimming.ReminderMinutes);
        Assert.Equal(0, early);
        Assert.Equal(1, due);
        Assert.Equal(0, again);
        var reminder = Assert.Single(await RemindersAsync(NotificationKind.EventReminder));
        Assert.Equal(dadId, reminder.UserId);
        Assert.Equal($"Lembrete: Natação às {start:HH:mm}", reminder.Title);
    }

    [Fact]
    public async Task A_weekly_event_reminds_every_week_and_not_after_losing_access_to_the_calendar()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var (_, dadId) = await Fase1.MemberAsync(_factory, admin, household, "Pai", HouseholdRole.Adult);
        var (_, ritaId, _) = await Fase1.ChildProfileAsync(_factory, admin, household, "Rita");
        var start = _factory.Clock.GetUtcNow().AddHours(15);
        var day = start.DayOfWeek.ToString()[..2].ToUpperInvariant();
        await NotificationTests.Post<CalendarEventDto>(admin, $"/api/households/{household}/events",
            new SaveEventRequest("Futebol", StartsAt: start, EndsAt: start.AddHours(1), Recurrence: $"FREQ=WEEKLY;BYDAY={day}",
                ParticipantUserIds: [dadId, ritaId], ReminderMinutes: [60]));

        _factory.Clock.SetUtcNow(start.AddMinutes(-59));
        var firstWeek = await RunAsync();
        // A Rita deixa de ver o calendário: a partir daqui só o Pai é lembrado.
        (await admin.PutAsJsonAsync($"/api/households/{household}/permissions", new SavePermissionsRequest(
            [PermissionTests.Role(HouseholdRole.Child, (HouseholdModule.Calendar, ModuleAccess.None))]), TestJson.Options))
            .EnsureSuccessStatusCode();
        _factory.Clock.SetUtcNow(start.AddDays(7).AddMinutes(-59));
        var secondWeek = await RunAsync();

        Assert.Equal(2, firstWeek);
        Assert.Equal(1, secondWeek);
        Assert.Equal(3, (await RemindersAsync(NotificationKind.EventReminder)).Count);
    }

    [Fact]
    public async Task A_chore_reminder_goes_to_whoever_has_it_and_not_when_it_is_done()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var (dad, dadId) = await Fase1.MemberAsync(_factory, admin, household, "Pai", HouseholdRole.Adult);
        var now = _factory.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var bins = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Lixo", DueDate: today, AssigneeUserId: dadId,
            RemindAt: new TimeOnly(20, 0)));
        var dishes = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Loiça", DueDate: today, AssigneeUserId: dadId,
            RemindAt: new TimeOnly(20, 0)));
        (await dad.PostAsJsonAsync($"/api/households/{household}/chores/{dishes.Id}/complete", new { }, TestJson.Options))
            .EnsureSuccessStatusCode();

        _factory.Clock.SetUtcNow(now.Date.AddHours(20).AddMinutes(2));
        var sent = await RunAsync();

        Assert.Equal(new TimeOnly(20, 0), bins.RemindAt);
        Assert.Equal(1, sent);
        var reminder = Assert.Single(await RemindersAsync(NotificationKind.ChoreReminder));
        Assert.Equal(dadId, reminder.UserId);
        Assert.Equal("Lembrete: Lixo", reminder.Title);
    }

    [Fact]
    public async Task The_morning_summary_says_what_is_on_today_and_respects_the_preference()
    {
        var (admin, auth, household) = await Fase1.HouseholdAsync(_factory);
        var (dad, dadId) = await Fase1.MemberAsync(_factory, admin, household, "Pai", HouseholdRole.Adult);
        var now = _factory.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Lixo", DueDate: today, AssigneeUserId: auth.User.Id));
        var start = now.Date.AddHours(17).AddMinutes(30);
        await NotificationTests.Post<CalendarEventDto>(admin, $"/api/households/{household}/events",
            new SaveEventRequest("Natação", StartsAt: start, EndsAt: start.AddHours(1), ParticipantUserIds: [auth.User.Id]));
        // O Pai não quer o resumo da manhã.
        (await dad.PutAsJsonAsync("/api/notifications/preferences", new NotificationPreferencesDto(
            NotificationDelivery.Immediate, new TimeOnly(19, 0), null, null, "Europe/Lisbon", MorningSummary: false),
            TestJson.Options)).EnsureSuccessStatusCode();

        // O fuso da casa é Lisboa: às 7h30 de lá.
        var lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        var morning = new DateTimeOffset(today.ToDateTime(new TimeOnly(7, 31)), lisbon.GetUtcOffset(now.UtcDateTime));
        _factory.Clock.SetUtcNow(morning);
        await RunAsync();

        var summaries = await RemindersAsync(NotificationKind.MorningSummary);
        var mine = Assert.Single(summaries);
        Assert.Equal(auth.User.Id, mine.UserId);
        Assert.Equal("O teu dia", mine.Title);
        Assert.Contains("Natação às 17:30", mine.Body);
        Assert.Contains("1 tarefa", mine.Body);
        Assert.DoesNotContain(summaries, n => n.UserId == dadId);
    }

    [Fact]
    public async Task Two_runs_at_the_same_time_never_send_the_same_reminder_twice()
    {
        var (admin, auth, household) = await Fase1.HouseholdAsync(_factory);
        var start = _factory.Clock.GetUtcNow().AddHours(15);
        await NotificationTests.Post<CalendarEventDto>(admin, $"/api/households/{household}/events",
            new SaveEventRequest("Dentista", StartsAt: start, EndsAt: start.AddHours(1), ReminderMinutes: [10]));

        _factory.Clock.SetUtcNow(start.AddMinutes(-9));
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunAsync()));

        Assert.Equal(1, runs.Sum());
        Assert.Single(await RemindersAsync(NotificationKind.EventReminder), n => n.UserId == auth.User.Id);
    }

    [Fact]
    public async Task Invalid_reminders_are_refused()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var start = _factory.Clock.GetUtcNow().AddHours(15);

        var tooMany = await admin.PostAsJsonAsync($"/api/households/{household}/events",
            new SaveEventRequest("X", StartsAt: start, EndsAt: start.AddHours(1), ReminderMinutes: [5, 10, 30, 60]), TestJson.Options);
        var negative = await admin.PostAsJsonAsync($"/api/households/{household}/events",
            new SaveEventRequest("X", StartsAt: start, EndsAt: start.AddHours(1), ReminderMinutes: [-5]), TestJson.Options);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, negative.StatusCode);
    }

    private async Task<int> RunAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ReminderService>().RunAsync(default);
    }

    private async Task<List<Notification>> RemindersAsync(NotificationKind kind)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KubicoDbContext>();
        return await db.Notifications.AsNoTracking().Where(n => n.Kind == kind).ToListAsync();
    }

    public void Dispose() => _factory.Dispose();
}
