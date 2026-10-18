using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Quem está fora sai das rotações enquanto está fora, e as tarefas que lhe cabiam passam ao seguinte.</summary>
public class AbsenceTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task While_someone_is_away_their_chores_go_to_the_next_person_in_the_rotation()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);
        var (me, rita, dad) = (adminAuth.User.Id, childAuth.User.Id, partnerAuth.User.Id);
        var today = factory.Today;
        var chores = $"/api/households/{household.Id}/chores";

        var trash = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Lixo", DueDate: today, Recurrence: ChoreRecurrence.Weekly, AssigneeUserId: rita, RotationUserIds: [me, rita, dad]));
        var table = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(
            "Pôr a mesa", DueDate: today, Recurrence: ChoreRecurrence.Daily, AssigneeUserId: me, RotationUserIds: [rita, me]));

        // A Rita vai passar uns dias a casa do pai: marca a sua ausência.
        var away = await child.PostAsJsonAsync($"/api/households/{household.Id}/absences",
            new SaveAbsenceRequest(rita, today, today.AddDays(10), "Casa do pai"), TestJson.Options);
        var afterAway = await admin.GetFromJsonAsync<List<ChoreDto>>(chores, TestJson.Options);
        var dadInbox = await NotificationTests.Inbox(partner);

        // A mãe põe a mesa hoje: amanhã seria a vez da Rita, mas está fora, por isso fica com a mãe.
        var done = await NotificationTests.Post<ChoreCompletedDto>(admin, $"{chores}/{table.Id}/complete", null);

        Assert.Equal(HttpStatusCode.Created, away.StatusCode);
        Assert.Equal(dad, afterAway!.Single(c => c.Id == trash.Id).AssigneeUserId);
        Assert.Contains(dadInbox.Items, n => n.Title == "Tarefa para ti: Lixo (Rita está fora)");
        Assert.Equal((today.AddDays(1), me), (done.Chore.DueDate!.Value, done.Chore.AssigneeUserId!.Value));
    }

    [Fact]
    public async Task Each_member_marks_their_own_absences_and_adults_mark_anyone_s()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var (stranger, _) = await factory.CreateUserAsync("Vizinho");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        var url = $"/api/households/{household.Id}/absences";
        var today = factory.Today;

        var childForMum = await child.PostAsJsonAsync(url, new SaveAbsenceRequest(adminAuth.User.Id, today, today.AddDays(2)), TestJson.Options);
        var mumForChild = await admin.PostAsJsonAsync(url, new SaveAbsenceRequest(childAuth.User.Id, today.AddDays(5), today.AddDays(6), "Visita de estudo"), TestJson.Options);
        var mumForHerself = await admin.PostAsJsonAsync(url, new SaveAbsenceRequest(adminAuth.User.Id, today, today), TestJson.Options);
        var backwards = await admin.PostAsJsonAsync(url, new SaveAbsenceRequest(adminAuth.User.Id, today.AddDays(3), today), TestJson.Options);
        var past = await admin.PostAsJsonAsync(url, new SaveAbsenceRequest(adminAuth.User.Id, today.AddDays(-5), today.AddDays(-1)), TestJson.Options);
        var notMember = await admin.PostAsJsonAsync(url, new SaveAbsenceRequest(Guid.NewGuid(), today, today), TestJson.Options);
        var strangerList = await stranger.GetAsync(url);

        var list = await child.GetFromJsonAsync<List<AbsenceDto>>(url, TestJson.Options);
        var mine = (await mumForHerself.Content.ReadFromJsonAsync<AbsenceDto>(TestJson.Options))!;
        var childCancelsMum = await child.DeleteAsync($"{url}/{mine.Id}");
        var mumCancels = await admin.DeleteAsync($"{url}/{mine.Id}");
        var listAfter = await child.GetFromJsonAsync<List<AbsenceDto>>(url, TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, childForMum.StatusCode);
        Assert.Equal(HttpStatusCode.Created, mumForChild.StatusCode);
        Assert.Equal(HttpStatusCode.Created, mumForHerself.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerList.StatusCode);
        Assert.Equal([today, today.AddDays(5)], list!.Select(a => a.StartDate));
        Assert.Equal(HttpStatusCode.Forbidden, childCancelsMum.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, mumCancels.StatusCode);
        Assert.Equal("Visita de estudo", Assert.Single(listAfter!).Reason);
    }
}
