using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Activity;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Revisão — fase 3A: pontos, "não aceitar", desfazer e rotinas por criança (ENG-01/02/03/06/15, UX-03).</summary>
public class PointsAndUndoTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Deleting_a_chore_with_history_keeps_the_points_and_the_history_and_undo_brings_it_back()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (rita, ritaId, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        var chores = $"/api/households/{household}/chores";
        var table = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Pôr a mesa", Points: 10, AssigneeUserId: ritaId, Recurrence: ChoreRecurrence.Daily));
        await NotificationTests.Post<ChoreCompletedDto>(rita, $"{chores}/{table.Id}/complete", null);
        var unused = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest("Regar"));

        (await admin.DeleteAsync($"{chores}/{table.Id}")).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"{chores}/{unused.Id}")).EnsureSuccessStatusCode();
        var list = await admin.GetFromJsonAsync<List<ChoreDto>>($"{chores}?includeDone=true", TestJson.Options);
        var history = await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"{chores}/history", TestJson.Options);
        var balance = await Balance(admin, household, ritaId);
        var completeArchived = await rita.PostAsJsonAsync($"{chores}/{table.Id}/complete", new CompleteChoreRequest(), TestJson.Options);
        var editArchived = await admin.PutAsJsonAsync($"{chores}/{table.Id}", new SaveChoreRequest("Pôr a mesa"), TestJson.Options);

        var deleted = (await History(admin, household)).First(a => a.Summary == "apagou a tarefa «Pôr a mesa»");
        var undone = await admin.PostAsync(Undo(household, deleted.Id), null);
        var back = await admin.GetFromJsonAsync<List<ChoreDto>>(chores, TestJson.Options);

        Assert.DoesNotContain(list!, c => c.Id == table.Id || c.Id == unused.Id);
        Assert.Contains(history!, h => h.ChoreId == table.Id && h.Points == 10);
        Assert.Equal(10, balance);
        Assert.Equal(HttpStatusCode.NotFound, completeArchived.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, editArchived.StatusCode);
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.Contains(back!, c => c.Id == table.Id);
    }

    [Fact]
    public async Task A_one_off_chore_that_was_done_and_becomes_recurring_shows_up_again()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var chores = $"/api/households/{household}/chores";
        var bulb = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest("Trocar a lâmpada"));
        await NotificationTests.Post<ChoreCompletedDto>(admin, $"{chores}/{bulb.Id}/complete", null);

        var edited = await admin.PutAsJsonAsync($"{chores}/{bulb.Id}",
            new SaveChoreRequest("Trocar a lâmpada", Recurrence: ChoreRecurrence.Weekly), TestJson.Options);
        var chore = (await edited.Content.ReadFromJsonAsync<ChoreDto>(TestJson.Options))!;
        var list = await admin.GetFromJsonAsync<List<ChoreDto>>(chores, TestJson.Options);

        Assert.False(chore.IsDone);
        Assert.Contains(list!, c => c.Id == bulb.Id);
    }

    [Fact]
    public async Task Creating_child_profiles_gives_each_child_its_own_routines_from_the_template()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var created = await admin.PostAsJsonAsync("/api/households",
            new CreateHouseholdRequest("Casa", Template: HouseholdTemplate.FamilyWithKids), TestJson.Options);
        var household = (await created.Content.ReadFromJsonAsync<HouseholdDto>(TestJson.Options))!.Id;
        var chores = $"/api/households/{household}/chores";
        var weeklyDays = (await admin.GetFromJsonAsync<List<ChoreDto>>(chores, TestJson.Options))!
            .Where(c => c.Recurrence == ChoreRecurrence.Weekly).Select(c => c.DueDate).ToList();

        var (rita, ritaId, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        var (bruno, brunoId, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Bruno");
        var routines = (await admin.GetFromJsonAsync<List<ChoreDto>>(chores, TestJson.Options))!
            .Where(c => c.Steps is { Count: > 0 }).ToList();
        var ritaMorning = routines.Single(c => c.Title == "Rotina da manhã" && c.AssigneeUserId == ritaId);
        var brunoMorning = routines.Single(c => c.Title == "Rotina da manhã" && c.AssigneeUserId == brunoId);
        for (var step = 0; step < ritaMorning.Steps!.Count; step++)
        {
            (await rita.PostAsJsonAsync($"{chores}/{ritaMorning.Id}/steps/{step}", new { done = true }, TestJson.Options)).EnsureSuccessStatusCode();
        }

        var brunoStep = await bruno.PostAsJsonAsync($"{chores}/{brunoMorning.Id}/steps/0", new { done = true }, TestJson.Options);

        Assert.Equal(4, routines.Count);
        Assert.Equal(2, routines.Count(c => c.AssigneeUserId == ritaId));
        Assert.Equal(2, routines.Count(c => c.AssigneeUserId == brunoId));
        Assert.Equal(HttpStatusCode.OK, brunoStep.StatusCode);
        Assert.Equal(weeklyDays.Count, weeklyDays.Distinct().Count());
        Assert.DoesNotContain(factory.Today, weeklyDays.Cast<DateOnly>());
    }

    private static async Task<int> Balance(HttpClient client, Guid householdId, Guid userId) =>
        (await client.GetFromJsonAsync<RewardsDto>($"/api/households/{householdId}/rewards", TestJson.Options))!
            .Points.Single(p => p.UserId == userId).Available;

    internal static async Task<List<ActivityDto>> History(HttpClient client, Guid householdId) =>
        (await client.GetFromJsonAsync<List<ActivityDto>>($"/api/households/{householdId}/activity", TestJson.Options))!;

    internal static string Undo(Guid householdId, long entryId) => $"/api/households/{householdId}/activity/{entryId}/undo";
}

/// <summary>Os casos que avançam o relógio (servidor próprio).</summary>
public sealed class RejectAndUndoClockTests : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    [Fact]
    public async Task Turning_down_an_old_completion_only_takes_the_points_and_a_weekly_chore_keeps_its_day()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var chores = $"/api/households/{household}/chores";
        var start = _factory.Today;
        var daily = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Pôr a mesa", Points: 1, Recurrence: ChoreRecurrence.Daily));
        var weekly = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Tirar o lixo", Points: 1, Recurrence: ChoreRecurrence.Weekly));
        await NotificationTests.Post<ChoreCompletedDto>(admin, $"{chores}/{daily.Id}/complete", null);
        await NotificationTests.Post<ChoreCompletedDto>(admin, $"{chores}/{weekly.Id}/complete", null);
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        await NotificationTests.Post<ChoreCompletedDto>(admin, $"{chores}/{daily.Id}/complete", null);
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        var history = (await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"{chores}/history", TestJson.Options))!;
        var oldest = history.Where(h => h.ChoreId == daily.Id).MinBy(h => h.CompletedAt)!;
        var weeklyDone = history.Single(h => h.ChoreId == weekly.Id);

        (await admin.PostAsync($"{chores}/completions/{oldest.Id}/reject", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"{chores}/completions/{weeklyDone.Id}/reject", null)).EnsureSuccessStatusCode();
        var after = (await admin.GetFromJsonAsync<List<ChoreDto>>(chores, TestJson.Options))!;

        Assert.Equal(start.AddDays(2), after.Single(c => c.Id == daily.Id).DueDate);
        Assert.Equal(start, after.Single(c => c.Id == weekly.Id).DueDate);
    }

    [Fact]
    public async Task Completions_older_than_30_days_cannot_be_turned_down()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var chores = $"/api/households/{household}/chores";
        var chore = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest("Arrumar", Recurrence: ChoreRecurrence.Daily));
        await NotificationTests.Post<ChoreCompletedDto>(admin, $"{chores}/{chore.Id}/complete", null);
        var done = Assert.Single((await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"{chores}/history", TestJson.Options))!);

        _factory.Clock.Advance(TimeSpan.FromDays(31));
        var response = await admin.PostAsync($"{chores}/completions/{done.Id}/reject", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Undo_sends_a_newer_version_and_never_brings_back_someone_who_left()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var (_, rui) = await Fase1.MemberAsync(_factory, admin, household, "Rui", HouseholdRole.Adult);
        var chores = $"/api/households/{household}/chores";
        var chore = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest("Lavar o carro", AssigneeUserId: rui));
        _factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var renamed = await (await admin.PutAsJsonAsync($"{chores}/{chore.Id}",
            new SaveChoreRequest("Lavar o carro e a mota", AssigneeUserId: rui), TestJson.Options)).Content.ReadFromJsonAsync<ChoreDto>(TestJson.Options);
        (await admin.DeleteAsync($"/api/households/{household}/members/{rui}")).EnsureSuccessStatusCode();
        _factory.Clock.Advance(TimeSpan.FromMinutes(1));

        var entry = (await PointsAndUndoTests.History(admin, household)).First(a => a.Summary == "mudou a tarefa «Lavar o carro e a mota»");
        var undone = await admin.PostAsync(PointsAndUndoTests.Undo(household, entry.Id), null);
        var after = (await admin.GetFromJsonAsync<List<ChoreDto>>(chores, TestJson.Options))!.Single(c => c.Id == chore.Id);

        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.Equal("Lavar o carro", after.Title);
        Assert.True(after.UpdatedAt > renamed!.UpdatedAt);
        Assert.Null(after.AssigneeUserId);
    }

    public void Dispose() => _factory.Dispose();
}
