using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Rotinas da manhã e da noite: tarefas com passos que se vão marcando; o último passo conclui-a.</summary>
public class RoutineTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Ticking_the_last_step_completes_the_routine_and_the_next_day_starts_over()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        var today = factory.Today;
        var routine = await NotificationTests.Post<ChoreDto>(admin, $"/api/households/{household.Id}/chores", new SaveChoreRequest(
            "Rotina da manhã", Points: 3, DueDate: today, Recurrence: ChoreRecurrence.Daily,
            AssigneeUserId: childAuth.User.Id, TimeOfDay: ChoreTimeOfDay.Morning,
            Steps: ["Lavar os dentes", "  ", "Vestir", "Fazer a cama"]));
        var steps = $"/api/households/{household.Id}/chores/{routine.Id}/steps";

        var first = await Step(child, $"{steps}/0", true);
        var second = await Step(child, $"{steps}/2", true);
        var undone = await Step(child, $"{steps}/0", false);
        await Step(child, $"{steps}/0", true);
        var last = await Step(child, $"{steps}/1", true);

        Assert.Equal(["Lavar os dentes", "Vestir", "Fazer a cama"], routine.Steps);
        Assert.Equal(ChoreTimeOfDay.Morning, routine.TimeOfDay);
        Assert.Equal((false, 0), (first.Completed, first.PointsEarned));
        Assert.Equal([0], first.Chore.DoneSteps);
        Assert.Equal([0, 2], second.Chore.DoneSteps);
        Assert.Equal([2], undone.Chore.DoneSteps);
        Assert.Equal((true, 3), (last.Completed, last.PointsEarned));
        Assert.Empty(last.Chore.DoneSteps!);
        Assert.Equal(today.AddDays(1), last.Chore.DueDate);
    }

    [Fact]
    public async Task Only_the_person_doing_the_routine_ticks_its_steps()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var (brother, _) = await factory.CreateUserAsync("Tomás");
        var (stranger, _) = await factory.CreateUserAsync("Vizinho");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        await NotificationTests.Join(admin, brother, household.Id, HouseholdRole.Child);
        var routine = await NotificationTests.Post<ChoreDto>(admin, $"/api/households/{household.Id}/chores", new SaveChoreRequest(
            "Rotina da noite", Recurrence: ChoreRecurrence.Daily, AssigneeUserId: childAuth.User.Id,
            TimeOfDay: ChoreTimeOfDay.Evening, Steps: ["Pijama", "Lavar os dentes"]));
        var steps = $"/api/households/{household.Id}/chores/{routine.Id}/steps";

        var byBrother = await brother.PostAsJsonAsync($"{steps}/0", new SetChoreStepRequest(true), TestJson.Options);
        var byStranger = await stranger.PostAsJsonAsync($"{steps}/0", new SetChoreStepRequest(true), TestJson.Options);
        var missingStep = await child.PostAsJsonAsync($"{steps}/5", new SetChoreStepRequest(true), TestJson.Options);
        var byMum = await admin.PostAsJsonAsync($"{steps}/0", new SetChoreStepRequest(true), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, byBrother.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingStep.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byMum.StatusCode);
    }

    [Fact]
    public async Task Changing_the_steps_starts_the_routine_over_and_there_is_a_limit_on_steps()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var chores = $"/api/households/{household.Id}/chores";
        var request = new SaveChoreRequest("Rotina da manhã", Recurrence: ChoreRecurrence.Daily, Steps: ["Lavar a cara", "Vestir"]);
        var routine = await NotificationTests.Post<ChoreDto>(admin, chores, request);
        await Step(admin, $"{chores}/{routine.Id}/steps/0", true);

        var renamed = await admin.PutAsJsonAsync($"{chores}/{routine.Id}", request with { Title = "Manhã" }, TestJson.Options);
        var sameSteps = (await renamed.Content.ReadFromJsonAsync<ChoreDto>(TestJson.Options))!;
        var changed = await admin.PutAsJsonAsync(
            $"{chores}/{routine.Id}", request with { Steps = ["Lavar a cara", "Vestir", "Pequeno-almoço"] }, TestJson.Options);
        var newSteps = (await changed.Content.ReadFromJsonAsync<ChoreDto>(TestJson.Options))!;
        var tooMany = await admin.PostAsJsonAsync(
            chores, request with { Steps = [.. Enumerable.Range(1, 21).Select(i => $"Passo {i}")] }, TestJson.Options);

        Assert.Equal([0], sameSteps.DoneSteps);
        Assert.Empty(newSteps.DoneSteps!);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    private static async Task<ChoreStepResultDto> Step(HttpClient client, string url, bool done)
    {
        var response = await client.PostAsJsonAsync(url, new SetChoreStepRequest(done), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChoreStepResultDto>(TestJson.Options))!;
    }
}
