using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Rotação justa (F4-23): a próxima vez vai para quem fez menos tarefas nos últimos 14 dias.</summary>
public class FairRotationTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task The_next_turn_goes_to_whoever_did_least_not_the_next_in_the_list()
    {
        var (admin, auth, household) = await Fase1.HouseholdAsync(factory);
        var (dad, dadId) = await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);
        var (_, ritaId, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        var mumId = auth.User.Id;
        var today = factory.Today;

        // A Rita já fez duas tarefas esta semana; o Pai nenhuma.
        foreach (var title in new[] { "Arrumar o quarto", "Pôr a mesa" })
        {
            var chore = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest(title, AssigneeUserId: ritaId));
            (await admin.PostAsJsonAsync($"/api/households/{household}/chores/{chore.Id}/complete", new { }, TestJson.Options))
                .EnsureSuccessStatusCode();
        }

        // Rotação Mãe → Rita → Pai: em ordem, depois da Mãe seria a Rita; na justa, é o Pai.
        var fair = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Lixo", DueDate: today,
            Recurrence: ChoreRecurrence.Daily, AssigneeUserId: mumId, RotationUserIds: [mumId, ritaId, dadId], FairRotation: true));
        var ordered = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Loiça", DueDate: today,
            Recurrence: ChoreRecurrence.Daily, AssigneeUserId: mumId, RotationUserIds: [mumId, ritaId, dadId]));
        var afterFair = await NotificationTests.Post<ChoreCompletedDto>(admin, $"/api/households/{household}/chores/{fair.Id}/complete", new { });
        var afterOrdered = await NotificationTests.Post<ChoreCompletedDto>(admin, $"/api/households/{household}/chores/{ordered.Id}/complete", new { });

        Assert.True(fair.FairRotation);
        Assert.Equal(dadId, afterFair.Chore.AssigneeUserId);
        Assert.Equal(ritaId, afterOrdered.Chore.AssigneeUserId);
        _ = dad;
    }

    [Fact]
    public async Task Without_a_rotation_the_fair_option_is_ignored()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var chore = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Regar", FairRotation: true));

        Assert.False(chore.FairRotation);
    }
}
