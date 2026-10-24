using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kubico.Api.Activity;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Tarefas e pontos (A1, A5, A11, T7, T8, T9).</summary>
public class ChoreFixesTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task When_an_adult_marks_a_childs_chore_the_child_gets_the_points()
    {
        var (admin, adminAuth, household) = await Fase1.HouseholdAsync(factory);
        var (child, bruno, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Bruno");
        var (_, carla) = await Fase1.MemberAsync(factory, admin, household, "Carla", HouseholdRole.Child);
        var room = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Arrumar o quarto", Points: 5, AssigneeUserId: bruno));
        var routine = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest(
            "Rotina da manhã", Points: 2, AssigneeUserId: bruno, Steps: ["Lavar os dentes"]));
        var table = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Pôr a mesa", Points: 3));
        var plants = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Regar", Points: 4, AssigneeUserId: bruno));
        var dog = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Passear o cão", Points: 6));

        var byMum = await Fase1.CompleteAsync(admin, household, room.Id);
        var step = await admin.PostAsJsonAsync($"/api/households/{household}/chores/{routine.Id}/steps/0", new SetChoreStepRequest(true), TestJson.Options);
        var forCarla = await Fase1.CompleteAsync(admin, household, table.Id, new CompleteChoreRequest(DoneByUserId: carla));
        var mumHerself = await Fase1.CompleteAsync(admin, household, plants.Id, new CompleteChoreRequest(DoneByUserId: adminAuth.User.Id));
        var notMember = await Fase1.CompleteAsync(admin, household, dog.Id, new CompleteChoreRequest(DoneByUserId: Guid.NewGuid()));
        var childForOther = await Fase1.CompleteAsync(child, household, dog.Id, new CompleteChoreRequest(DoneByUserId: carla));
        var history = await admin.GetFromJsonAsync<List<ActivityDto>>($"/api/households/{household}/activity", TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, byMum.StatusCode);
        Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forCarla.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mumHerself.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, childForOther.StatusCode);
        Assert.Equal(7, (await Fase1.PointsAsync(admin, household, bruno)).Earned);
        Assert.Equal(3, (await Fase1.PointsAsync(admin, household, carla)).Earned);
        Assert.Equal(4, (await Fase1.PointsAsync(admin, household, adminAuth.User.Id)).Earned);
        Assert.Contains(history!, a => a.Summary == "marcou «Arrumar o quarto» como feita por Bruno");
    }

    [Fact]
    public async Task A_repeating_chore_without_a_date_starts_today()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);

        var daily = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Pôr a mesa", Recurrence: ChoreRecurrence.Daily));
        var once = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Mudar a lâmpada"));

        Assert.Equal(factory.Today, daily.DueDate);
        Assert.Null(once.DueDate);
    }

    [Fact]
    public async Task A_monthly_chore_on_the_31st_goes_back_to_the_end_of_the_month_after_february()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var start = new DateOnly(factory.Today.Year - 1, 1, 31);
        var rent = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest(
            "Pagar a renda", DueDate: start, Recurrence: ChoreRecurrence.Monthly));

        var done = (await (await Fase1.CompleteAsync(admin, household, rent.Id)).Content.ReadFromJsonAsync<ChoreCompletedDto>(TestJson.Options))!;

        // Os prazos ficam no dia 31 (ou no último dia dos meses mais curtos); o próximo é o primeiro cujo mês começa
        // depois de hoje (o que se fez hoje conta para o mês de hoje).
        var dues = Enumerable.Range(0, 40)
            .Select(k => start.AddMonths(k))
            .Select(m => new DateOnly(m.Year, m.Month, Math.Min(31, DateTime.DaysInMonth(m.Year, m.Month))))
            .ToList();
        var expected = dues.Skip(1).Where((d, i) => dues[i] >= factory.Today).First();
        Assert.Equal(expected, done.Chore.DueDate);
        Assert.Equal(Math.Min(31, DateTime.DaysInMonth(expected.Year, expected.Month)), expected.Day);
    }

    [Fact]
    public async Task Chores_without_anyone_to_take_them_wait_while_their_person_is_away()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (_, dad) = await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);
        var today = factory.Today;
        var plants = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest(
            "Regar as plantas", DueDate: today, Recurrence: ChoreRecurrence.Weekly, AssigneeUserId: dad));
        var daily = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest(
            "Dar comida ao gato", DueDate: today, Recurrence: ChoreRecurrence.Daily, AssigneeUserId: dad));
        var bulb = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Mudar a lâmpada", DueDate: today.AddDays(2), AssigneeUserId: dad));
        var later = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Pintar", DueDate: today.AddDays(30), AssigneeUserId: dad));

        (await admin.PostAsJsonAsync($"/api/households/{household}/absences",
            new SaveAbsenceRequest(dad, today, today.AddDays(9)), TestJson.Options)).EnsureSuccessStatusCode();
        var chores = (await admin.GetFromJsonAsync<List<ChoreDto>>($"/api/households/{household}/chores", TestJson.Options))!;

        Assert.Equal(today.AddDays(14), chores.Single(c => c.Id == plants.Id).DueDate);
        Assert.Equal(today.AddDays(10), chores.Single(c => c.Id == daily.Id).DueDate);
        Assert.Equal(today.AddDays(10), chores.Single(c => c.Id == bulb.Id).DueDate);
        Assert.Equal(today.AddDays(30), chores.Single(c => c.Id == later.Id).DueDate);
        Assert.All(chores, c => Assert.Equal(dad, c.AssigneeUserId));
    }

    [Fact]
    public async Task Undoing_or_rejecting_a_chore_whose_points_were_spent_asks_before_leaving_a_debt()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, carla) = await Fase1.MemberAsync(factory, admin, household, "Carla", HouseholdRole.Child);
        var car = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Lavar o carro", Points: 30, AssigneeUserId: carla));
        var dishes = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Pôr a loiça", Points: 50, AssigneeUserId: carla));
        (await Fase1.CompleteAsync(child, household, car.Id)).EnsureSuccessStatusCode();
        (await Fase1.CompleteAsync(child, household, dishes.Id)).EnsureSuccessStatusCode();
        var cinema = await NotificationTests.Post<RewardDto>(admin, $"/api/households/{household}/rewards", new SaveRewardRequest("Cinema", Cost: 60));
        var request = await NotificationTests.Post<RedemptionDto>(child, $"/api/households/{household}/rewards/{cinema.Id}/redeem", null);
        (await admin.PostAsync($"/api/households/{household}/redemptions/{request.Id}/approve", null)).EnsureSuccessStatusCode();
        var activity = (await child.GetFromJsonAsync<List<ActivityDto>>($"/api/households/{household}/activity", TestJson.Options))!;
        var carEntry = activity.Single(a => a.Summary == "fez «Lavar o carro»");

        var childUndo = await child.PostAsync($"/api/households/{household}/activity/{carEntry.Id}/undo", null);
        var childForce = await child.PostAsync($"/api/households/{household}/activity/{carEntry.Id}/undo?force=true", null);
        var history = (await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"/api/households/{household}/chores/history", TestJson.Options))!;
        var reject = await admin.PostAsync($"/api/households/{household}/chores/completions/{history.Single(h => h.ChoreId == dishes.Id).Id}/reject", null);
        var forced = await admin.PostAsync($"/api/households/{household}/chores/completions/{history.Single(h => h.ChoreId == dishes.Id).Id}/reject?force=true", null);
        var balance = await Fase1.PointsAsync(admin, household, carla);

        var problem = await Fase1.ProblemAsync(childUndo);
        Assert.Equal(HttpStatusCode.Conflict, childUndo.StatusCode);
        Assert.Equal("points-spent", ((JsonElement)problem.Extensions["code"]!).GetString());
        Assert.Equal("Os pontos desta tarefa já foram trocados: Carla ficaria a dever 10 pontos.", problem.Title);
        Assert.Equal(HttpStatusCode.Conflict, childForce.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reject.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        Assert.Equal(-30, balance.Available);
    }

    [Theory]
    [InlineData("""{"title":"t","recurrence":9}""")]
    [InlineData("""{"title":"t","recurrence":"9"}""")]
    [InlineData("""{"title":"t","recurrence":"Hourly"}""")]
    [InlineData("""{"title":"t","timeOfDay":7}""")]
    public async Task Values_outside_the_enums_are_refused(string body)
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);

        var response = await admin.PostAsync($"/api/households/{household}/chores",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var chores = await admin.GetFromJsonAsync<List<ChoreDto>>($"/api/households/{household}/chores", TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Pedido inválido.", (await Fase1.ProblemAsync(response)).Title);
        Assert.Empty(chores!);
    }

    [Fact]
    public async Task Invites_and_roles_outside_the_enum_are_refused()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (_, dad) = await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);

        var invite = await admin.PostAsync($"/api/households/{household}/invites",
            new StringContent("""{"role":9}""", System.Text.Encoding.UTF8, "application/json"));
        var role = await admin.PutAsync($"/api/households/{household}/members/{dad}/role",
            new StringContent("""{"role":"Rei"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, invite.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, role.StatusCode);
    }
}
