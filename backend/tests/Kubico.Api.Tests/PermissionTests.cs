using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Realtime;
using Kubico.Api.Rewards;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>O administrador escolhe o que cada papel pode fazer em cada área; o servidor cumpre, também em tempo real.</summary>
public class PermissionTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task By_default_adults_manage_everything_and_children_use_the_areas_but_only_see_the_calendar()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var (child, _) = await factory.CreateUserAsync("Rita");
        var (partner, _) = await factory.CreateUserAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);
        var lists = $"/api/households/{household.Id}/lists";
        var events = $"/api/households/{household.Id}/events";
        var list = await NotificationTests.Post<ShoppingListDto>(admin, lists, new CreateShoppingListRequest("Supermercado"));
        var tomorrow = factory.Today.AddDays(1);

        var childMe = await Me(child);
        var adultHousehold = await admin.GetFromJsonAsync<HouseholdDto>($"/api/households/{household.Id}", TestJson.Options);
        var childAddsItem = await child.PostAsJsonAsync($"{lists}/{list.Id}/items", new AddShoppingItemRequest("Bolachas"), TestJson.Options);
        var childCreatesList = await child.PostAsJsonAsync(lists, new CreateShoppingListRequest("Doces"), TestJson.Options);
        var childSeesCalendar = await child.GetAsync($"{events}?from={tomorrow:yyyy-MM-dd}&to={tomorrow:yyyy-MM-dd}");
        var childCreatesEvent = await child.PostAsJsonAsync(events, AllDay("Festa", tomorrow), TestJson.Options);
        var partnerCreatesEvent = await partner.PostAsJsonAsync(events, AllDay("Jantar", tomorrow), TestJson.Options);
        var problem = await childCreatesEvent.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(TestJson.Options);

        var access = childMe.Households.Single(h => h.Id == household.Id).Access;
        Assert.Equal(ModuleAccess.Participate, access[HouseholdModule.Shopping]);
        Assert.Equal(ModuleAccess.Participate, access[HouseholdModule.Chores]);
        Assert.Equal(ModuleAccess.View, access[HouseholdModule.Calendar]);
        Assert.Equal(ModuleAccess.Participate, access[HouseholdModule.Rewards]);
        Assert.All(adultHousehold!.MyAccess.Values, a => Assert.Equal(ModuleAccess.Manage, a));
        Assert.Equal(HttpStatusCode.Created, childAddsItem.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, childCreatesList.StatusCode);
        Assert.Equal(HttpStatusCode.OK, childSeesCalendar.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, childCreatesEvent.StatusCode);
        Assert.Equal("Não tens permissão para isto em Calendário.", problem!.Title);
        Assert.Equal(HttpStatusCode.Created, partnerCreatesEvent.StatusCode);
    }

    [Fact]
    public async Task The_admin_chooses_what_each_role_can_do_and_the_server_enforces_it()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (child, _) = await factory.CreateUserAsync("Rita");
        var (guest, _) = await factory.CreateUserAsync("Avó");
        var (partner, _) = await factory.CreateUserAsync("Pai");
        var (stranger, _) = await factory.CreateUserAsync("Vizinho");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        await NotificationTests.Join(admin, guest, household.Id, HouseholdRole.Guest);
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);
        var url = $"/api/households/{household.Id}/permissions";
        var area = $"/api/households/{household.Id}";
        var tomorrow = factory.Today.AddDays(1);
        var chore = await NotificationTests.Post<ChoreDto>(admin, $"{area}/chores",
            new SaveChoreRequest("Regar as plantas", DueDate: tomorrow, AssigneeUserId: adminAuth.User.Id));

        // A avó não usa as compras e só espreita as tarefas; a Rita pode pôr coisas no calendário.
        var saved = await admin.PutAsJsonAsync(url, new SavePermissionsRequest([
            Role(HouseholdRole.Guest, (HouseholdModule.Shopping, ModuleAccess.None), (HouseholdModule.Chores, ModuleAccess.View)),
            Role(HouseholdRole.Child, (HouseholdModule.Calendar, ModuleAccess.Manage)),
        ]), TestJson.Options);
        var byAdult = await partner.PutAsJsonAsync(url, new SavePermissionsRequest([
            Role(HouseholdRole.Child, (HouseholdModule.Rewards, ModuleAccess.None)),
        ]), TestJson.Options);
        var halfCalendar = await admin.PutAsJsonAsync(url, new SavePermissionsRequest([
            Role(HouseholdRole.Guest, (HouseholdModule.Calendar, ModuleAccess.Participate)),
        ]), TestJson.Options);
        var forAdmins = await admin.PutAsJsonAsync(url, new SavePermissionsRequest([
            Role(HouseholdRole.Admin, (HouseholdModule.Shopping, ModuleAccess.View)),
        ]), TestJson.Options);
        var strangerReads = await stranger.GetAsync(url);

        var permissions = (await saved.Content.ReadFromJsonAsync<HouseholdPermissionsDto>(TestJson.Options))!;
        var guestMe = await Me(guest);
        var guestLists = await guest.GetAsync($"{area}/lists");
        var guestChores = await guest.GetAsync($"{area}/chores");
        var guestCompletes = await guest.PostAsync($"{area}/chores/{chore.Id}/complete", null);
        var childCreatesEvent = await child.PostAsJsonAsync($"{area}/events", AllDay("Anos da Inês", tomorrow), TestJson.Options);
        var guestRewards = await guest.GetAsync($"{area}/rewards");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byAdult.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, halfCalendar.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forAdmins.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerReads.StatusCode);
        Assert.Equal([HouseholdRole.Adult, HouseholdRole.Child, HouseholdRole.Guest], permissions.Roles.Select(r => r.Role));
        Assert.Equal(ModuleAccess.None, permissions.Roles.Single(r => r.Role == HouseholdRole.Guest).Access[HouseholdModule.Shopping]);
        Assert.Equal(ModuleAccess.Participate, permissions.Defaults.Single(r => r.Role == HouseholdRole.Guest).Access[HouseholdModule.Shopping]);
        Assert.Equal(ModuleAccess.None, guestMe.Households.Single().Access[HouseholdModule.Shopping]);
        Assert.Equal(HttpStatusCode.Forbidden, guestLists.StatusCode);
        Assert.Equal(HttpStatusCode.OK, guestChores.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, guestCompletes.StatusCode);
        Assert.Equal(HttpStatusCode.Created, childCreatesEvent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, guestRewards.StatusCode);

        // Voltar ao habitual: a avó volta a ver as compras.
        var reset = await admin.PutAsJsonAsync(url, new SavePermissionsRequest([
            Role(HouseholdRole.Guest, (HouseholdModule.Shopping, ModuleAccess.Participate), (HouseholdModule.Chores, ModuleAccess.Participate)),
        ]), TestJson.Options);
        var reread = await guest.GetFromJsonAsync<HouseholdPermissionsDto>(url, TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"{area}/lists")).StatusCode);
        Assert.Equal(reread!.Defaults.Single(r => r.Role == HouseholdRole.Guest).Access,
            reread.Roles.Single(r => r.Role == HouseholdRole.Guest).Access);
        Assert.Equal(ModuleAccess.Manage, reread.Roles.Single(r => r.Role == HouseholdRole.Child).Access[HouseholdModule.Calendar]);
    }

    [Fact]
    public async Task Adults_who_only_use_chores_complete_their_own_and_cannot_create_new_ones()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);
        var chores = $"/api/households/{household.Id}/chores";
        var today = factory.Today;
        var mine = await NotificationTests.Post<ChoreDto>(admin, chores,
            new SaveChoreRequest("Aspirar", DueDate: today, AssigneeUserId: partnerAuth.User.Id));
        var hers = await NotificationTests.Post<ChoreDto>(admin, chores,
            new SaveChoreRequest("Passar a ferro", DueDate: today, AssigneeUserId: adminAuth.User.Id));
        (await admin.PutAsJsonAsync($"/api/households/{household.Id}/permissions", new SavePermissionsRequest([
            Role(HouseholdRole.Adult, (HouseholdModule.Chores, ModuleAccess.Participate)),
        ]), TestJson.Options)).EnsureSuccessStatusCode();

        var creates = await partner.PostAsJsonAsync(chores, new SaveChoreRequest("Lavar o carro"), TestJson.Options);
        var completesOwn = await partner.PostAsync($"{chores}/{mine.Id}/complete", null);
        var completesHers = await partner.PostAsync($"{chores}/{hers.Id}/complete", null);
        var rewardsStillManaged = await partner.PostAsJsonAsync(
            $"/api/households/{household.Id}/rewards", new SaveRewardRequest("Cinema", Cost: 50), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, creates.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completesOwn.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, completesHers.StatusCode);
        Assert.Equal(HttpStatusCode.Created, rewardsStillManaged.StatusCode);
    }

    [Fact]
    public async Task Changes_in_an_area_only_reach_who_can_see_it_and_follow_permission_changes()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var (guest, guestAuth) = await factory.CreateUserAsync("Avó");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, guest, household.Id, HouseholdRole.Guest);
        var url = $"/api/households/{household.Id}/permissions";
        var list = await NotificationTests.Post<ShoppingListDto>(admin, $"/api/households/{household.Id}/lists",
            new CreateShoppingListRequest("Supermercado"));
        var items = $"/api/households/{household.Id}/lists/{list.Id}/items";
        (await admin.PutAsJsonAsync(url, new SavePermissionsRequest([
            Role(HouseholdRole.Guest, (HouseholdModule.Shopping, ModuleAccess.None)),
        ]), TestJson.Options)).EnsureSuccessStatusCode();

        var shopping = new List<string>();
        var chore = new TaskCompletionSource<ChoreDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new TaskCompletionSource<ShoppingItemDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = CreateHubConnection(guestAuth.AccessToken);
        connection.On<ShoppingItemDto>(HouseholdEvents.ShoppingItemUpserted, i =>
        {
            lock (shopping)
            {
                shopping.Add(i.Name);
            }

            if (i.Name == "Fruta")
            {
                item.TrySetResult(i);
            }
        });
        connection.On<ChoreDto>(HouseholdEvents.ChoreUpserted, c => chore.TrySetResult(c));
        connection.On<Guid>(HouseholdEvents.PermissionsChanged, id => changed.TrySetResult(id));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinHousehold", household.Id);

        // Sem acesso às compras, o café não chega; a tarefa, que vem depois, chega.
        await NotificationTests.Post<ShoppingItemDto>(admin, items, new AddShoppingItemRequest("Café"));
        await NotificationTests.Post<ChoreDto>(admin, $"/api/households/{household.Id}/chores", new SaveChoreRequest("Regar"));
        await chore.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A mãe deixa-a ver as compras: a app é avisada e as novidades começam a chegar.
        (await admin.PutAsJsonAsync(url, new SavePermissionsRequest([
            Role(HouseholdRole.Guest, (HouseholdModule.Shopping, ModuleAccess.View)),
        ]), TestJson.Options)).EnsureSuccessStatusCode();
        var changedHousehold = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await NotificationTests.Post<ShoppingItemDto>(admin, items, new AddShoppingItemRequest("Fruta"));
        await item.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(household.Id, changedHousehold);
        lock (shopping)
        {
            Assert.Equal(["Fruta"], shopping);
        }
    }

    [Fact]
    public async Task Whoever_is_removed_stops_receiving_the_household_s_changes()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);
        var chores = $"/api/households/{household.Id}/chores";

        var received = new List<string>();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = CreateHubConnection(partnerAuth.AccessToken);
        connection.On<ChoreDto>(HouseholdEvents.ChoreUpserted, c =>
        {
            lock (received)
            {
                received.Add(c.Title);
            }
        });
        connection.On<Guid, Guid>(HouseholdEvents.MemberDeleted, (_, _) => removed.TrySetResult());
        await connection.StartAsync();
        await connection.InvokeAsync("JoinHousehold", household.Id);

        (await admin.DeleteAsync($"/api/households/{household.Id}/members/{partnerAuth.User.Id}")).EnsureSuccessStatusCode();
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest("Segredo"));

        // Uma volta ao servidor pela mesma ligação: se a tarefa tivesse sido enviada, já teria chegado.
        await connection.InvokeAsync("LeaveHousehold", household.Id);
        lock (received)
        {
            Assert.Empty(received);
        }
    }

    internal static RolePermissionsDto Role(HouseholdRole role, params (HouseholdModule Module, ModuleAccess Access)[] access) =>
        new(role, access.ToDictionary(a => a.Module, a => a.Access));

    private static SaveEventRequest AllDay(string title, DateOnly date) =>
        new(title, IsAllDay: true, StartDate: date, EndDate: date);

    private static async Task<MeDto> Me(HttpClient client) =>
        (await client.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options))!;

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
}
