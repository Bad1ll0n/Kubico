using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Api.Realtime;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

public class ChoreTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Rotating_weekly_chore_moves_to_the_next_member_when_completed()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Filipe");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Ana");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await Join(admin, partner, household.Id, HouseholdRole.Adult);

        var chore = await CreateChore(admin, household.Id, new SaveChoreRequest(
            "Tirar o lixo",
            Points: 5,
            DueDate: factory.Today,
            Recurrence: ChoreRecurrence.Weekly,
            RotationUserIds: [adminAuth.User.Id, partnerAuth.User.Id]));
        Assert.Equal(adminAuth.User.Id, chore.AssigneeUserId);

        var completed = await Complete(admin, household.Id, chore.Id);

        Assert.Equal(5, completed.PointsEarned);
        Assert.Equal(partnerAuth.User.Id, completed.Chore.AssigneeUserId);
        Assert.Equal(factory.Today.AddDays(7), completed.Chore.DueDate);
        Assert.False(completed.Chore.IsDone);
    }

    [Fact]
    public async Task One_off_chore_is_hidden_once_done_and_cannot_be_completed_twice()
    {
        var (client, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var chore = await CreateChore(client, household.Id, new SaveChoreRequest("Mudar lâmpada da cozinha"));
        var url = $"/api/households/{household.Id}/chores";

        await Complete(client, household.Id, chore.Id);
        var again = await client.PostAsync($"{url}/{chore.Id}/complete", null);
        var open = await client.GetFromJsonAsync<List<ChoreDto>>(url, TestJson.Options);
        var all = await client.GetFromJsonAsync<List<ChoreDto>>($"{url}?includeDone=true", TestJson.Options);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Empty(open!);
        Assert.True(Assert.Single(all!).IsDone);
    }

    [Fact]
    public async Task Children_only_complete_their_own_or_unassigned_chores_and_cannot_create()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync();
        var (child, childAuth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await Join(admin, child, household.Id, HouseholdRole.Child);
        var url = $"/api/households/{household.Id}/chores";

        var parents = await CreateChore(admin, household.Id, new SaveChoreRequest("Pagar a luz", AssigneeUserId: adminAuth.User.Id));
        var mine = await CreateChore(admin, household.Id, new SaveChoreRequest("Fazer a cama", Points: 2, AssigneeUserId: childAuth.User.Id));
        var anyone = await CreateChore(admin, household.Id, new SaveChoreRequest("Regar plantas"));

        Assert.Equal(HttpStatusCode.Forbidden, (await child.PostAsync($"{url}/{parents.Id}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await child.PostAsync($"{url}/{mine.Id}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await child.PostAsync($"{url}/{anyone.Id}/complete", null)).StatusCode);

        var create = await child.PostAsJsonAsync(url, new SaveChoreRequest("Sem trabalhos de casa"), TestJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task Assignee_and_rotation_must_be_household_members()
    {
        var (client, auth) = await factory.CreateUserAsync();
        var (_, stranger) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var url = $"/api/households/{household.Id}/chores";

        var withStranger = await client.PostAsJsonAsync(url, new SaveChoreRequest(
            "Aspirar", Recurrence: ChoreRecurrence.Weekly, RotationUserIds: [auth.User.Id, stranger.User.Id]), TestJson.Options);
        var rotationWithoutRecurrence = await client.PostAsJsonAsync(url, new SaveChoreRequest(
            "Aspirar", RotationUserIds: [auth.User.Id]), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, withStranger.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rotationWithoutRecurrence.StatusCode);
    }

    [Fact]
    public async Task Stats_add_up_points_per_member()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Filipe");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Ana");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await Join(admin, partner, household.Id, HouseholdRole.Adult);

        // Cada ocorrência de uma tarefa diária conclui-se uma vez por dia (T1): duas tarefas diárias.
        var dishes = await CreateChore(admin, household.Id, new SaveChoreRequest("Loiça", Points: 3, Recurrence: ChoreRecurrence.Daily));
        var laundry = await CreateChore(admin, household.Id, new SaveChoreRequest("Roupa", Points: 3, Recurrence: ChoreRecurrence.Daily));
        var bathroom = await CreateChore(admin, household.Id, new SaveChoreRequest("Casa de banho", Points: 10));
        await Complete(partner, household.Id, dishes.Id);
        await Complete(partner, household.Id, laundry.Id);
        await Complete(admin, household.Id, bathroom.Id);

        var stats = await admin.GetFromJsonAsync<ChoreStatsDto>(
            $"/api/households/{household.Id}/chores/stats?days=30", TestJson.Options);

        Assert.Equal(factory.Today, stats!.To);
        Assert.Collection(stats.Members,
            first => Assert.Equal((adminAuth.User.Id, 1, 10), (first.UserId, first.Completed, first.Points)),
            second => Assert.Equal((partnerAuth.User.Id, 2, 6), (second.UserId, second.Completed, second.Points)));
    }

    [Fact]
    public async Task Chores_from_another_household_are_not_reachable()
    {
        var (owner, _) = await factory.CreateUserAsync();
        var (other, _) = await factory.CreateUserAsync();
        var ownerHousehold = await HouseholdTests.CreateHousehold(owner, "Casa A");
        var otherHousehold = await HouseholdTests.CreateHousehold(other, "Casa B");
        var chore = await CreateChore(owner, ownerHousehold.Id, new SaveChoreRequest("Privada"));

        var response = await other.PostAsync($"/api/households/{otherHousehold.Id}/chores/{chore.Id}/complete", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Members_see_completed_chores_in_real_time()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (partner, partnerAuth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await Join(admin, partner, household.Id, HouseholdRole.Adult);
        var chore = await CreateChore(admin, household.Id, new SaveChoreRequest("Estender a roupa"));

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HouseholdHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(partnerAuth.AccessToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = TestJson.Options)
            .Build();
        var received = new TaskCompletionSource<ChoreDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<ChoreDto>(HouseholdEvents.ChoreUpserted, c => received.TrySetResult(c));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinHousehold", household.Id);

        await Complete(admin, household.Id, chore.Id);

        var update = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(chore.Id, update.Id);
        Assert.True(update.IsDone);
    }

    private static async Task Join(HttpClient admin, HttpClient member, Guid householdId, HouseholdRole role)
    {
        var invite = await HouseholdTests.CreateInvite(admin, householdId, role);
        (await member.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();
    }

    private static async Task<ChoreDto> CreateChore(HttpClient client, Guid householdId, SaveChoreRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/households/{householdId}/chores", request, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChoreDto>(TestJson.Options))!;
    }

    private static async Task<ChoreCompletedDto> Complete(HttpClient client, Guid householdId, Guid choreId)
    {
        var response = await client.PostAsync($"/api/households/{householdId}/chores/{choreId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChoreCompletedDto>(TestJson.Options))!;
    }
}
