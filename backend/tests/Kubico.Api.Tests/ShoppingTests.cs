using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Realtime;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

public class ShoppingTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Members_add_check_and_clear_items()
    {
        var (client, auth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var list = await CreateList(client, household.Id, "Supermercado");
        var baseUrl = $"/api/households/{household.Id}/lists/{list.Id}/items";

        var milk = await AddItem(client, baseUrl, new AddShoppingItemRequest("Leite", "2", "Laticínios"));
        await AddItem(client, baseUrl, new AddShoppingItemRequest("Pão"));

        var patch = await client.PatchAsJsonAsync($"{baseUrl}/{milk.Id}", new UpdateShoppingItemRequest(IsChecked: true));
        var checkedMilk = await patch.Content.ReadFromJsonAsync<ShoppingItemDto>(TestJson.Options);
        Assert.True(checkedMilk!.IsChecked);
        Assert.Equal(auth.User.Id, checkedMilk.CheckedByUserId);

        var items = await client.GetFromJsonAsync<List<ShoppingItemDto>>(baseUrl, TestJson.Options);
        Assert.Equal(["Pão", "Leite"], items!.Select(i => i.Name));

        (await client.PostAsync($"{baseUrl}/clear-checked", null)).EnsureSuccessStatusCode();
        var remaining = await client.GetFromJsonAsync<List<ShoppingItemDto>>(baseUrl, TestJson.Options);
        Assert.Equal("Pão", Assert.Single(remaining!).Name);

        var lists = await client.GetFromJsonAsync<List<ShoppingListDto>>(
            $"/api/households/{household.Id}/lists", TestJson.Options);
        Assert.Equal(1, Assert.Single(lists!).ItemCount);
    }

    [Fact]
    public async Task Children_can_add_items_but_cannot_create_lists()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (child, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var invite = await HouseholdTests.CreateInvite(admin, household.Id, HouseholdRole.Child);
        (await child.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();
        var list = await CreateList(admin, household.Id, "Supermercado");

        var createList = await child.PostAsJsonAsync(
            $"/api/households/{household.Id}/lists", new CreateShoppingListRequest("Doces"));
        var addItem = await child.PostAsJsonAsync(
            $"/api/households/{household.Id}/lists/{list.Id}/items", new AddShoppingItemRequest("Cereais"));

        Assert.Equal(HttpStatusCode.Forbidden, createList.StatusCode);
        Assert.Equal(HttpStatusCode.Created, addItem.StatusCode);
    }

    [Fact]
    public async Task Lists_from_another_household_are_not_reachable()
    {
        var (owner, _) = await factory.CreateUserAsync();
        var (other, _) = await factory.CreateUserAsync();
        var ownerHousehold = await HouseholdTests.CreateHousehold(owner, "Casa A");
        var otherHousehold = await HouseholdTests.CreateHousehold(other, "Casa B");
        var list = await CreateList(owner, ownerHousehold.Id, "Privada");

        // Membro da casa B a tentar chegar à lista da casa A através do seu próprio agregado.
        var response = await other.PostAsJsonAsync(
            $"/api/households/{otherHousehold.Id}/lists/{list.Id}/items", new AddShoppingItemRequest("Intruso"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Empty_item_name_is_rejected()
    {
        var (client, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var list = await CreateList(client, household.Id, "Supermercado");

        var response = await client.PostAsJsonAsync(
            $"/api/households/{household.Id}/lists/{list.Id}/items", new AddShoppingItemRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Other_members_receive_new_items_in_real_time()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (partner, partnerAuth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var invite = await HouseholdTests.CreateInvite(admin, household.Id, HouseholdRole.Adult);
        (await partner.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();
        var list = await CreateList(admin, household.Id, "Supermercado");

        await using var connection = CreateHubConnection(partnerAuth.AccessToken);
        var received = new TaskCompletionSource<ShoppingItemDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<ShoppingItemDto>(HouseholdEvents.ShoppingItemUpserted, item => received.TrySetResult(item));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinHousehold", household.Id);

        await AddItem(admin, $"/api/households/{household.Id}/lists/{list.Id}/items", new AddShoppingItemRequest("Café"));

        var item = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Café", item.Name);
    }

    [Fact]
    public async Task Non_members_cannot_join_a_household_channel()
    {
        var (owner, _) = await factory.CreateUserAsync();
        var (_, strangerAuth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(owner, "Casa");

        await using var connection = CreateHubConnection(strangerAuth.AccessToken);
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinHousehold", household.Id));
    }

    private HubConnection CreateHubConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HouseholdHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = TestJson.Options)
            .Build();

    private static async Task<ShoppingListDto> CreateList(HttpClient client, Guid householdId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/households/{householdId}/lists", new CreateShoppingListRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ShoppingListDto>(TestJson.Options))!;
    }

    private static async Task<ShoppingItemDto> AddItem(HttpClient client, string url, AddShoppingItemRequest request)
    {
        var response = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ShoppingItemDto>(TestJson.Options))!;
    }
}
