using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;

namespace Kubico.Api.Tests;

/// <summary>Regras automáticas (F4-26): recompensas aprovadas sozinhas e tarefas em atraso de quem fica fora.</summary>
public class AutomaticRuleTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Requests_up_to_the_limit_are_approved_on_their_own_and_only_managers_set_the_limit()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, ritaId, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        var chore = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Arrumar", Points: 40, AssigneeUserId: ritaId));
        (await child.PostAsJsonAsync($"/api/households/{household}/chores/{chore.Id}/complete", new { }, TestJson.Options))
            .EnsureSuccessStatusCode();
        var sticker = await NotificationTests.Post<RewardDto>(admin, $"/api/households/{household}/rewards", new SaveRewardRequest("Autocolante", Cost: 10));
        var cinema = await NotificationTests.Post<RewardDto>(admin, $"/api/households/{household}/rewards", new SaveRewardRequest("Cinema", Cost: 25));

        var childTries = await child.PutAsJsonAsync($"/api/households/{household}/rewards/settings", new RewardSettingsRequest(100), TestJson.Options);
        (await admin.PutAsJsonAsync($"/api/households/{household}/rewards/settings", new RewardSettingsRequest(15), TestJson.Options))
            .EnsureSuccessStatusCode();
        var small = await NotificationTests.Post<RedemptionDto>(child, $"/api/households/{household}/rewards/{sticker.Id}/redeem", null);
        var big = await NotificationTests.Post<RedemptionDto>(child, $"/api/households/{household}/rewards/{cinema.Id}/redeem", null);
        var overview = await admin.GetFromJsonAsync<RewardsDto>($"/api/households/{household}/rewards", TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, childTries.StatusCode);
        Assert.Equal(RedemptionStatus.Approved, small.Status);
        Assert.Equal(RedemptionStatus.Pending, big.Status);
        Assert.Equal(15, overview!.AutoApproveUpTo);
    }

    [Fact]
    public async Task Two_automatic_requests_at_once_never_spend_more_points_than_there_are()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, ritaId, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        var chore = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Arrumar", Points: 10, AssigneeUserId: ritaId));
        (await child.PostAsJsonAsync($"/api/households/{household}/chores/{chore.Id}/complete", new { }, TestJson.Options))
            .EnsureSuccessStatusCode();
        var sticker = await NotificationTests.Post<RewardDto>(admin, $"/api/households/{household}/rewards", new SaveRewardRequest("Autocolante", Cost: 10));
        (await admin.PutAsJsonAsync($"/api/households/{household}/rewards/settings", new RewardSettingsRequest(10), TestJson.Options))
            .EnsureSuccessStatusCode();

        var results = await Fase1.ParallelAsync(4, _ => child.PostAsync($"/api/households/{household}/rewards/{sticker.Id}/redeem", null));

        Assert.Equal(1, results.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(0, (await Fase1.PointsAsync(admin, household, ritaId)).Available);
    }

    [Fact]
    public async Task An_overdue_chore_goes_to_the_next_in_the_rotation_when_its_person_is_away_today()
    {
        var (admin, auth, household) = await Fase1.HouseholdAsync(factory);
        var (_, dadId) = await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);
        var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);
        var chore = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest(
            "Lixo", DueDate: today.AddDays(-1), Recurrence: Domain.Chores.ChoreRecurrence.Daily, AssigneeUserId: dadId,
            RotationUserIds: [dadId, auth.User.Id]));

        (await admin.PostAsJsonAsync($"/api/households/{household}/absences", new SaveAbsenceRequest(dadId, today, today.AddDays(2)), TestJson.Options))
            .EnsureSuccessStatusCode();
        var after = await admin.GetFromJsonAsync<List<ChoreDto>>($"/api/households/{household}/chores", TestJson.Options);

        Assert.Equal(auth.User.Id, after!.Single(c => c.Id == chore.Id).AssigneeUserId);
    }
}
