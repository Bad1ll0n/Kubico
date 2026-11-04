using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Api.Realtime;
using Kubico.Api.Rewards;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

public class RewardTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Child_spends_chore_points_on_a_reward_once_an_adult_approves()
    {
        var (parent, _) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(parent, "Casa");
        await Join(parent, child, household.Id, HouseholdRole.Child);
        await EarnPoints(parent, child, household.Id, 30);
        var movie = await CreateReward(parent, household.Id, new SaveRewardRequest("Escolher o filme de sexta", Cost: 25));

        var redemption = await Redeem(child, household.Id, movie.Id);
        var reserved = await Points(child, household.Id, childAuth.User.Id);
        var approved = await parent.PostAsync($"/api/households/{household.Id}/redemptions/{redemption.Id}/approve", null);
        var spent = await Points(child, household.Id, childAuth.User.Id);

        Assert.Equal(RedemptionStatus.Pending, redemption.Status);
        Assert.Equal((30, 0, 25, 5), (reserved.Earned, reserved.Spent, reserved.Reserved, reserved.Available));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal((30, 25, 0, 5), (spent.Earned, spent.Spent, spent.Reserved, spent.Available));
    }

    [Fact]
    public async Task Not_enough_points_rejected_requests_and_cancellations_keep_the_balance_right()
    {
        var (parent, _) = await factory.CreateUserAsync();
        var (child, childAuth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(parent, "Casa");
        await Join(parent, child, household.Id, HouseholdRole.Child);
        await EarnPoints(parent, child, household.Id, 20);
        var games = await CreateReward(parent, household.Id, new SaveRewardRequest("30 minutos de consola", Cost: 15));
        var trip = await CreateReward(parent, household.Id, new SaveRewardRequest("Ida ao parque aquático", Cost: 100));
        var url = $"/api/households/{household.Id}";

        var tooExpensive = await child.PostAsync($"{url}/rewards/{trip.Id}/redeem", null);
        var first = await Redeem(child, household.Id, games.Id);
        var secondWhileReserved = await child.PostAsync($"{url}/rewards/{games.Id}/redeem", null);
        var rejected = await parent.PostAsync($"{url}/redemptions/{first.Id}/reject", null);
        var decideAgain = await parent.PostAsync($"{url}/redemptions/{first.Id}/approve", null);
        var second = await Redeem(child, household.Id, games.Id);
        var cancelled = await child.DeleteAsync($"{url}/redemptions/{second.Id}");
        var balance = await Points(child, household.Id, childAuth.User.Id);

        Assert.Equal(HttpStatusCode.Conflict, tooExpensive.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondWhileReserved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, decideAgain.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(20, balance.Available);
    }

    [Fact]
    public async Task Adults_get_their_rewards_at_once_and_children_cannot_manage_rewards_or_requests()
    {
        var (parent, _) = await factory.CreateUserAsync();
        var (child, _) = await factory.CreateUserAsync();
        var (sibling, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(parent, "Casa");
        await Join(parent, child, household.Id, HouseholdRole.Child);
        await Join(parent, sibling, household.Id, HouseholdRole.Child);
        await EarnPoints(parent, parent, household.Id, 10);
        await EarnPoints(parent, child, household.Id, 10);
        var coffee = await CreateReward(parent, household.Id, new SaveRewardRequest("Pequeno-almoço na cama", Cost: 10));
        var url = $"/api/households/{household.Id}";

        var own = await Redeem(parent, household.Id, coffee.Id);
        var childRequest = await Redeem(child, household.Id, coffee.Id);
        var create = await child.PostAsJsonAsync($"{url}/rewards", new SaveRewardRequest("Sem escola"), TestJson.Options);
        var approveOwn = await child.PostAsync($"{url}/redemptions/{childRequest.Id}/approve", null);
        var cancelOther = await sibling.DeleteAsync($"{url}/redemptions/{childRequest.Id}");

        Assert.Equal(RedemptionStatus.Approved, own.Status);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, approveOwn.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cancelOther.StatusCode);
    }

    [Fact]
    public async Task History_keeps_the_name_and_cost_after_the_reward_changes_or_is_deleted()
    {
        var (parent, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(parent, "Casa");
        await EarnPoints(parent, parent, household.Id, 50);
        var dinner = await CreateReward(parent, household.Id, new SaveRewardRequest("Jantar fora", Cost: 40));
        await Redeem(parent, household.Id, dinner.Id);
        var url = $"/api/households/{household.Id}/rewards";

        (await parent.PutAsJsonAsync($"{url}/{dinner.Id}", new SaveRewardRequest("Jantar fora (pizza)", Cost: 60), TestJson.Options))
            .EnsureSuccessStatusCode();
        (await parent.DeleteAsync($"{url}/{dinner.Id}")).EnsureSuccessStatusCode();
        var overview = (await parent.GetFromJsonAsync<RewardsDto>(url, TestJson.Options))!;

        Assert.Empty(overview.Rewards);
        var history = Assert.Single(overview.Redemptions);
        Assert.Equal(("Jantar fora", 40, (Guid?)null), (history.RewardTitle, history.Points, history.RewardId));
        Assert.Equal(10, Assert.Single(overview.Points).Available);
    }

    [Fact]
    public async Task Rewards_from_another_household_are_not_reachable()
    {
        var (owner, _) = await factory.CreateUserAsync();
        var (other, _) = await factory.CreateUserAsync();
        var ownerHousehold = await HouseholdTests.CreateHousehold(owner, "Casa A");
        var otherHousehold = await HouseholdTests.CreateHousehold(other, "Casa B");
        var reward = await CreateReward(owner, ownerHousehold.Id, new SaveRewardRequest("Privada", Cost: 1));

        var redeem = await other.PostAsync($"/api/households/{otherHousehold.Id}/rewards/{reward.Id}/redeem", null);
        var read = await other.GetAsync($"/api/households/{ownerHousehold.Id}/rewards");

        Assert.Equal(HttpStatusCode.NotFound, redeem.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task Adults_see_new_requests_in_real_time()
    {
        var (parent, parentAuth) = await factory.CreateUserAsync();
        var (child, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(parent, "Casa");
        await Join(parent, child, household.Id, HouseholdRole.Child);
        await EarnPoints(parent, child, household.Id, 5);
        var sweet = await CreateReward(parent, household.Id, new SaveRewardRequest("Um gelado", Cost: 5));

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HouseholdHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(parentAuth.AccessToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = TestJson.Options)
            .Build();
        var received = new TaskCompletionSource<RedemptionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<RedemptionDto>(HouseholdEvents.RedemptionUpserted, r => received.TrySetResult(r));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinHousehold", household.Id);

        var redemption = await Redeem(child, household.Id, sweet.Id);

        var update = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((redemption.Id, RedemptionStatus.Pending), (update.Id, update.Status));
    }

    private static async Task Join(HttpClient admin, HttpClient member, Guid householdId, HouseholdRole role)
    {
        var invite = await HouseholdTests.CreateInvite(admin, householdId, role);
        (await member.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();
    }

    /// <summary>Uma tarefa de "qualquer pessoa" com estes pontos, concluída por <paramref name="member"/>.</summary>
    private static async Task EarnPoints(HttpClient admin, HttpClient member, Guid householdId, int points)
    {
        var created = await admin.PostAsJsonAsync(
            $"/api/households/{householdId}/chores", new SaveChoreRequest("Arrumar o quarto", Points: points), TestJson.Options);
        var chore = (await created.Content.ReadFromJsonAsync<ChoreDto>(TestJson.Options))!;
        (await member.PostAsync($"/api/households/{householdId}/chores/{chore.Id}/complete", null)).EnsureSuccessStatusCode();
    }

    private static async Task<RewardDto> CreateReward(HttpClient client, Guid householdId, SaveRewardRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/households/{householdId}/rewards", request, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RewardDto>(TestJson.Options))!;
    }

    private static async Task<RedemptionDto> Redeem(HttpClient client, Guid householdId, Guid rewardId)
    {
        var response = await client.PostAsync($"/api/households/{householdId}/rewards/{rewardId}/redeem", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RedemptionDto>(TestJson.Options))!;
    }

    private static async Task<MemberPointsDto> Points(HttpClient client, Guid householdId, Guid userId)
    {
        var overview = await client.GetFromJsonAsync<RewardsDto>($"/api/households/{householdId}/rewards", TestJson.Options);
        return overview!.Points.Single(p => p.UserId == userId);
    }
}
